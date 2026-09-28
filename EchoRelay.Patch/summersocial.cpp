#include "summersocial.h"
#include <detours.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <string>
#include <vector>
#include <map>
#include <deque>
#include <mutex>
#include <atomic>
#include <unordered_set>

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "bcrypt.lib")

namespace SummerSocial
{
	// ------------------------------------------------------------------------------------------------------------
	// Logging
	// ------------------------------------------------------------------------------------------------------------
	static VOID NoLog(const CHAR*, ...) {}
	static VOID(*Log)(const CHAR* format, ...) = NoLog;

	VOID SetLogger(VOID(*log)(const CHAR* format, ...))
	{
		Log = log;
	}

	/// <summary>
	/// Verbose tracing of the social SDK emulation (what the game asks for and reads), capped to keep logs small.
	/// </summary>
	static std::atomic<int> g_traceBudget(0);

	VOID EnableTracing()
	{
		g_traceBudget = 4000;
	}
#define TRACE(...) do { if (g_traceBudget-- > 0) Log(__VA_ARGS__); } while (0)

	// ------------------------------------------------------------------------------------------------------------
	// Oculus Platform SDK constants used by pnsovr.dll
	// ------------------------------------------------------------------------------------------------------------
	const UINT32 MSG_USER_GET_LOGGED_IN_USER = 0x436F345D;
	const UINT32 MSG_USER_GET_LOGGED_IN_USER_FRIENDS = 0x587C2A8D;
	const UINT32 MSG_NOTIFICATION_ROOM_ROOM_UPDATE = 0x60EC3C2F;
	const UINT32 MSG_NOTIFICATION_ROOM_INVITE_RECEIVED = 0x6A499D54;
	const INT32 PRESENCE_ONLINE = 1;
	const INT32 PRESENCE_OFFLINE = 2;
	const INT32 LOCK_STATUS_LOCK = 1;

	/// <summary>
	/// ovrKeyValuePair (40 bytes), as passed to ovr_Room_UpdateDataStore.
	/// </summary>
	struct KeyValuePair
	{
		const CHAR* key;
		INT32 valueType; // 0 = string, 1 = int, 2 = double
		const CHAR* stringValue;
		INT32 intValue;
		double doubleValue;
	};
	static_assert(sizeof(KeyValuePair) == 40, "ovrKeyValuePair layout");

	// ------------------------------------------------------------------------------------------------------------
	// Minimal JSON (the social service protocol)
	// ------------------------------------------------------------------------------------------------------------
	struct Json
	{
		enum Type { Null, Bool, Number, String, Array, Object } type = Null;
		bool boolean = false;
		double number = 0;
		UINT64 integer = 0;
		std::string string;
		std::vector<Json> items;
		std::vector<std::pair<std::string, Json>> members;

		const Json* Get(const char* key) const
		{
			for (const auto& member : members)
				if (member.first == key)
					return &member.second;
			return NULL;
		}
		UINT64 U64(const char* key) const { const Json* v = Get(key); return v && v->type == Number ? v->integer : 0; }
		INT64 I64(const char* key) const { const Json* v = Get(key); return v && v->type == Number ? (INT64)v->number : 0; }
		std::string Str(const char* key) const { const Json* v = Get(key); return v && v->type == String ? v->string : ""; }
		bool Boolean(const char* key) const { const Json* v = Get(key); return v && v->type == Bool && v->boolean; }
	};

	struct JsonParser
	{
		const char* p;
		const char* end;

		void Skip() { while (p < end && (*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t')) p++; }

		bool ParseString(std::string& out)
		{
			if (p >= end || *p != '"') return false;
			p++;
			while (p < end && *p != '"')
			{
				char c = *p++;
				if (c == '\\' && p < end)
				{
					char e = *p++;
					switch (e)
					{
					case 'n': out += '\n'; break;
					case 't': out += '\t'; break;
					case 'r': out += '\r'; break;
					case 'b': out += '\b'; break;
					case 'f': out += '\f'; break;
					case 'u':
					{
						if (end - p < 4) return false;
						unsigned code = strtoul(std::string(p, 4).c_str(), NULL, 16);
						p += 4;
						if (code < 0x80) out += (char)code;
						else if (code < 0x800) { out += (char)(0xC0 | (code >> 6)); out += (char)(0x80 | (code & 0x3F)); }
						else { out += (char)(0xE0 | (code >> 12)); out += (char)(0x80 | ((code >> 6) & 0x3F)); out += (char)(0x80 | (code & 0x3F)); }
						break;
					}
					default: out += e; break;
					}
				}
				else
				{
					out += c;
				}
			}
			if (p >= end) return false;
			p++;
			return true;
		}

		bool Parse(Json& out)
		{
			Skip();
			if (p >= end) return false;
			if (*p == '{')
			{
				out.type = Json::Object;
				p++;
				Skip();
				if (p < end && *p == '}') { p++; return true; }
				while (p < end)
				{
					Skip();
					std::string key;
					if (!ParseString(key)) return false;
					Skip();
					if (p >= end || *p != ':') return false;
					p++;
					Json value;
					if (!Parse(value)) return false;
					out.members.emplace_back(key, std::move(value));
					Skip();
					if (p < end && *p == ',') { p++; continue; }
					if (p < end && *p == '}') { p++; return true; }
					return false;
				}
				return false;
			}
			if (*p == '[')
			{
				out.type = Json::Array;
				p++;
				Skip();
				if (p < end && *p == ']') { p++; return true; }
				while (p < end)
				{
					Json value;
					if (!Parse(value)) return false;
					out.items.push_back(std::move(value));
					Skip();
					if (p < end && *p == ',') { p++; continue; }
					if (p < end && *p == ']') { p++; return true; }
					return false;
				}
				return false;
			}
			if (*p == '"')
			{
				out.type = Json::String;
				return ParseString(out.string);
			}
			if (end - p >= 4 && strncmp(p, "true", 4) == 0) { out.type = Json::Bool; out.boolean = true; p += 4; return true; }
			if (end - p >= 5 && strncmp(p, "false", 5) == 0) { out.type = Json::Bool; out.boolean = false; p += 5; return true; }
			if (end - p >= 4 && strncmp(p, "null", 4) == 0) { out.type = Json::Null; p += 4; return true; }

			// Numbers: keep integers exact (ids are 64-bit).
			const char* start = p;
			if (p < end && (*p == '-' || *p == '+')) p++;
			while (p < end && ((*p >= '0' && *p <= '9') || *p == '.' || *p == 'e' || *p == 'E' || *p == '-' || *p == '+')) p++;
			if (p == start) return false;
			std::string text(start, p);
			out.type = Json::Number;
			out.number = strtod(text.c_str(), NULL);
			out.integer = text[0] == '-' ? (UINT64)_strtoi64(text.c_str(), NULL, 10) : _strtoui64(text.c_str(), NULL, 10);
			return true;
		}
	};

	static bool ParseJson(const std::string& text, Json& out)
	{
		JsonParser parser{ text.data(), text.data() + text.size() };
		return parser.Parse(out);
	}

	static std::string JsonString(const std::string& value)
	{
		std::string out = "\"";
		for (unsigned char c : value)
		{
			switch (c)
			{
			case '"': out += "\\\""; break;
			case '\\': out += "\\\\"; break;
			case '\n': out += "\\n"; break;
			case '\r': out += "\\r"; break;
			case '\t': out += "\\t"; break;
			default:
				if (c < 0x20) { char buf[8]; sprintf_s(buf, "\\u%04x", c); out += buf; }
				else out += (char)c;
			}
		}
		return out + "\"";
	}

	static std::string Base64Encode(const void* data, size_t size)
	{
		static const char* table = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
		const unsigned char* bytes = (const unsigned char*)data;
		std::string out;
		for (size_t i = 0; i < size; i += 3)
		{
			UINT32 v = bytes[i] << 16 | (i + 1 < size ? bytes[i + 1] << 8 : 0) | (i + 2 < size ? bytes[i + 2] : 0);
			out += table[(v >> 18) & 63];
			out += table[(v >> 12) & 63];
			out += i + 1 < size ? table[(v >> 6) & 63] : '=';
			out += i + 2 < size ? table[v & 63] : '=';
		}
		return out;
	}

	static std::string Base64Decode(const std::string& text)
	{
		std::string out;
		UINT32 buffer = 0;
		int bits = 0;
		for (char c : text)
		{
			int v;
			if (c >= 'A' && c <= 'Z') v = c - 'A';
			else if (c >= 'a' && c <= 'z') v = c - 'a' + 26;
			else if (c >= '0' && c <= '9') v = c - '0' + 52;
			else if (c == '+') v = 62;
			else if (c == '/') v = 63;
			else continue;
			buffer = buffer << 6 | v;
			bits += 6;
			if (bits >= 8)
			{
				bits -= 8;
				out += (char)((buffer >> bits) & 0xFF);
			}
		}
		return out;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Fake SDK objects. Every handle we hand out is registered, so accessors can tell ours from the real SDK's.
	// ------------------------------------------------------------------------------------------------------------
	enum Kind { K_MESSAGE = 1, K_ROOM, K_USER, K_USERS, K_DATASTORE, K_ORGID, K_INVITE, K_INVITES, K_ERROR, K_PACKET };

	struct Object
	{
		Kind kind;
		explicit Object(Kind k) : kind(k) {}
		virtual ~Object() {}
	};
	struct User : Object { UINT64 id = 0; std::string name, presence, token; bool online = false; User() : Object(K_USER) {} };
	struct Users : Object { std::vector<User*> items; Users() : Object(K_USERS) {} };
	struct DataStore : Object { std::map<std::string, std::string> values; DataStore() : Object(K_DATASTORE) {} };
	struct Room : Object { UINT64 id = 0; User* owner = NULL; Users* users = NULL; DataStore* data = NULL; Room() : Object(K_ROOM) {} };
	struct OrgId : Object { UINT64 id = 0; OrgId() : Object(K_ORGID) {} };
	struct Invite : Object { UINT64 id = 0, room = 0, sent = 0; Invite() : Object(K_INVITE) {} };
	struct Invites : Object { std::vector<Invite*> items; Invites() : Object(K_INVITES) {} };
	struct Error : Object { INT32 code = 0; std::string message; Error() : Object(K_ERROR) {} };
	struct Packet : Object { UINT64 sender = 0; std::string bytes; Packet() : Object(K_PACKET) {} };
	struct Message : Object
	{
		UINT32 type = 0;
		UINT64 requestId = 0;
		Error* error = NULL;
		Room* room = NULL;
		Users* users = NULL;
		User* user = NULL;
		OrgId* orgId = NULL;
		Invite* invite = NULL;
		Invites* invites = NULL;
		std::string string;
		std::vector<Object*> owned; // everything created for this message, freed with it
		Message() : Object(K_MESSAGE) {}
	};

	static std::mutex g_objectLock;
	static std::unordered_set<const void*> g_objects;
	static std::deque<Message*> g_freedMessages;

	template <typename T> static T* New(Message* owner)
	{
		T* object = new T();
		{
			std::lock_guard<std::mutex> guard(g_objectLock);
			g_objects.insert(object);
		}
		if (owner != NULL)
			owner->owned.push_back(object);
		return object;
	}

	static Object* Ours(const void* handle, Kind kind)
	{
		if (handle == NULL)
			return NULL;
		std::lock_guard<std::mutex> guard(g_objectLock);
		if (g_objects.find(handle) == g_objects.end())
			return NULL;
		Object* object = (Object*)handle;
		return object->kind == kind ? object : NULL;
	}

	static bool IsOurs(const void* handle)
	{
		if (handle == NULL)
			return false;
		std::lock_guard<std::mutex> guard(g_objectLock);
		return g_objects.find(handle) != g_objects.end();
	}

	static void Destroy(Object* object)
	{
		{
			std::lock_guard<std::mutex> guard(g_objectLock);
			g_objects.erase(object);
		}
		delete object;
	}

	/// <summary>
	/// Frees a message. pnsovr may still read objects it got from a message right after freeing it, so freed messages are
	/// kept for a while before their objects are released.
	/// </summary>
	static void ReleaseMessage(Message* message)
	{
		std::vector<Message*> expired;
		{
			std::lock_guard<std::mutex> guard(g_objectLock);
			g_freedMessages.push_back(message);
			while (g_freedMessages.size() > 256)
			{
				expired.push_back(g_freedMessages.front());
				g_freedMessages.pop_front();
			}
		}
		for (Message* old : expired)
		{
			for (Object* object : old->owned)
				Destroy(object);
			Destroy(old);
		}
	}

	static User* BuildUser(const Json& json, Message* owner)
	{
		User* user = New<User>(owner);
		user->id = json.U64("id");
		user->name = json.Str("name");
		user->online = json.Boolean("online");
		user->presence = json.Str("presence");
		user->token = std::to_string(user->id);
		return user;
	}

	static Users* BuildUsers(const Json* json, Message* owner)
	{
		Users* users = New<Users>(owner);
		if (json != NULL)
			for (const Json& item : json->items)
				users->items.push_back(BuildUser(item, owner));
		return users;
	}

	static Room* BuildRoom(const Json& json, Message* owner)
	{
		Room* room = New<Room>(owner);
		room->id = json.U64("id");
		const Json* ownerJson = json.Get("owner");
		room->owner = ownerJson != NULL && ownerJson->type == Json::Object ? BuildUser(*ownerJson, owner) : NULL;
		room->users = BuildUsers(json.Get("users"), owner);
		room->data = New<DataStore>(owner);
		if (const Json* data = json.Get("data"))
			for (const auto& member : data->members)
				room->data->values[member.first] = member.second.type == Json::String ? member.second.string : "";
		return room;
	}

	static Invite* BuildInvite(const Json& json, Message* owner)
	{
		Invite* invite = New<Invite>(owner);
		invite->id = json.U64("id");
		invite->room = json.U64("room");
		invite->sent = json.U64("sent");
		return invite;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Message and packet queues (drained by the hooked ovr_PopMessage / ovr_Net_ReadPacket on the game thread)
	// ------------------------------------------------------------------------------------------------------------
	static std::mutex g_queueLock;
	static std::deque<Message*> g_messages;
	static std::deque<Packet*> g_packets;
	static std::map<UINT64, std::string> g_pending; // request id -> operation
	static std::atomic<UINT64> g_nextRequestId(0x5E00000000000001ULL);

	static void Enqueue(Message* message)
	{
		std::lock_guard<std::mutex> guard(g_queueLock);
		g_messages.push_back(message);
	}

	static Message* ErrorMessage(UINT64 requestId, INT32 code, const std::string& text)
	{
		Message* message = New<Message>(NULL);
		message->requestId = requestId;
		message->error = New<Error>(message);
		message->error->code = code;
		message->error->message = text;
		return message;
	}

	// ------------------------------------------------------------------------------------------------------------
	// EchoRelay social service connection
	// ------------------------------------------------------------------------------------------------------------
	const UINT64 PACKET_HEADER = 0xBB8CE7A278BB40F6;
	const UINT64 SYMBOL_SOCIAL = 0x7777777777771000;

	static HINTERNET g_session = NULL, g_connection = NULL, g_socket = NULL;
	static std::mutex g_sendLock;
	static std::atomic<bool> g_connected(false);
	static std::atomic<bool> g_started(false);
	static std::wstring g_url;
	static UINT64 g_localUserId = 0;

	typedef UINT64(*ovr_GetLoggedInUserID_t)();
	static ovr_GetLoggedInUserID_t Real_ovr_GetLoggedInUserID = NULL;

	static std::string ReadFileText(const std::string& path)
	{
		FILE* f = NULL;
		if (fopen_s(&f, path.c_str(), "rb") != 0 || f == NULL)
			return "";
		std::string data;
		char buf[4096];
		size_t n;
		while ((n = fread(buf, 1, sizeof(buf), f)) > 0)
			data.append(buf, n);
		fclose(f);
		return data;
	}

	/// <summary>
	/// Reads socialservice_host from _local/config.json, or derives it from loginservice_host (same host, path /social).
	/// </summary>
	static std::wstring GetSocialServiceUrl()
	{
		char path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
			root = root.substr(0, root.find_last_of('\\'));
		Json config;
		std::string url;
		if (ParseJson(ReadFileText(root + "\\_local\\config.json"), config))
		{
			url = config.Str("socialservice_host");
			if (url.empty())
			{
				// The christmas 2017 build's config names it login_host.
				std::string login = config.Str("loginservice_host");
				if (login.empty())
					login = config.Str("login_host");
				size_t scheme = login.find("://");
				size_t slash = scheme == std::string::npos ? std::string::npos : login.find('/', scheme + 3);
				if (scheme != std::string::npos)
					url = (slash == std::string::npos ? login : login.substr(0, slash)) + "/social";
			}
		}
		if (url.empty())
			url = "ws://127.0.0.1:777/social";
		return std::wstring(url.begin(), url.end());
	}

	// ------------------------------------------------------------------------------------------------------------
	// Unique ids for Revive players
	// ------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Revive's Oculus platform emulation gives every player the same user (0x4C01DB400B0C9, seen by the game as 0xB400B0C9).
	/// The game identifies players by that id, so shared-id players got each other's names, teams, profiles and party slots.
	/// Without Revive, a game started outside the Oculus store has no Oculus user at all (id 0), which every such player
	/// shares just the same (each saw everyone else under their own name), so 0 counts as shared too.
	/// </summary>
	static bool IsSharedReviveId(UINT64 id)
	{
		return id == 0 || id == 0xB400B0C9ULL || id == 0x4C01DB400B0C9ULL;
	}

	static UINT64 g_uniqueUserId = 0;
	static std::string g_uniqueUserName;

	static std::string UrlDecode(const std::string& text)
	{
		std::string out;
		for (size_t i = 0; i < text.size(); i++)
		{
			if (text[i] == '+') out += ' ';
			else if (text[i] == '%' && i + 2 < text.size()) { out += (char)strtoul(text.substr(i + 1, 2).c_str(), NULL, 16); i += 2; }
			else out += text[i];
		}
		return out;
	}

	/// <summary>
	/// Derives this player's own id from the display name in _local/config.json's loginservice_host, exactly as EchoRelay
	/// derives the account of a shared-id player (LoginService.GetSummerAccountId): so the player keeps the same account.
	/// </summary>
	static void ComputeUniqueUserId()
	{
		char path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
			root = root.substr(0, root.find_last_of('\\'));
		Json config;
		if (!ParseJson(ReadFileText(root + "\\_local\\config.json"), config))
			return;
		std::string login = config.Str("loginservice_host");
		size_t query = login.find('?');
		if (query == std::string::npos)
			return;
		std::string name;
		std::string params = login.substr(query + 1);
		for (size_t start = 0; start <= params.size();)
		{
			size_t end = params.find('&', start);
			std::string pair = params.substr(start, end == std::string::npos ? std::string::npos : end - start);
			if (pair.rfind("displayname=", 0) == 0)
				name = UrlDecode(pair.substr(12));
			if (end == std::string::npos)
				break;
			start = end + 1;
		}
		// Trim and lower-case (ASCII), like the server's Trim().ToLowerInvariant().
		size_t first = name.find_first_not_of(" \t\r\n"), last = name.find_last_not_of(" \t\r\n");
		name = first == std::string::npos ? "" : name.substr(first, last - first + 1);
		if (name.empty())
			return;
		std::string key = "echorelay-summer-account:";
		for (char c : name)
			key += (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;

		BYTE hash[32] = {};
		BCRYPT_ALG_HANDLE algorithm = NULL;
		if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0)
			return;
		BCryptHash(algorithm, NULL, 0, (PUCHAR)key.data(), (ULONG)key.size(), hash, sizeof(hash));
		BCryptCloseAlgorithmProvider(algorithm, 0);
		UINT64 id;
		memcpy(&id, hash, 8);
		g_uniqueUserId = (id & 0x3FFFFFFFFFFFFFFFULL) | 0x4000000000000000ULL;
		g_uniqueUserName = name;
	}

	/// <summary>
	/// The player's display name from _local/config.json's loginservice_host (or christmas' login_host), as written.
	/// </summary>
	static const std::string& ConfigDisplayName()
	{
		static std::string name;
		static bool read = false;
		if (read)
			return name;
		read = true;
		char path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
			root = root.substr(0, root.find_last_of('\\'));
		Json config;
		if (!ParseJson(ReadFileText(root + "\\_local\\config.json"), config))
			return name;
		std::string login = config.Str("loginservice_host");
		if (login.find("displayname=") == std::string::npos)
			login = config.Str("login_host");
		size_t at = login.find("displayname=");
		if (at == std::string::npos)
			return name;
		std::string value = login.substr(at + 12);
		value = UrlDecode(value.substr(0, value.find('&')));
		size_t first = value.find_first_not_of(" \t\r\n"), last = value.find_last_not_of(" \t\r\n");
		name = first == std::string::npos ? "" : value.substr(first, last - first + 1);
		return name;
	}

	/// <summary>
	/// Replaces Revive's shared user id with this player's own id.
	/// </summary>
	static UINT64 g_forcedUserId = 0;

	static UINT64 MapUserId(UINT64 id)
	{
		if (g_forcedUserId != 0)
			return g_forcedUserId;
		if (!IsSharedReviveId(id))
			return id;
		static bool computed = false;
		if (!computed)
		{
			computed = true;
			ComputeUniqueUserId();
			if (g_uniqueUserId != 0)
				Log("[SOCIAL] Revive's shared user id %llu -> %llu (from display name '%s')", id, g_uniqueUserId, g_uniqueUserName.c_str());
			else
				Log("[SOCIAL] Revive's shared user id in use and no display name in the config: players may be mixed up");
		}
		return g_uniqueUserId != 0 ? g_uniqueUserId : id;
	}

	static void CloseSocket()
	{
		g_connected = false;
		if (g_socket) { WinHttpCloseHandle(g_socket); g_socket = NULL; }
		if (g_connection) { WinHttpCloseHandle(g_connection); g_connection = NULL; }
		if (g_session) { WinHttpCloseHandle(g_session); g_session = NULL; }
	}

	static bool OpenSocket()
	{
		std::wstring url = g_url;
		bool secure = false;
		if (url.rfind(L"wss://", 0) == 0) { url = L"https://" + url.substr(6); secure = true; }
		else if (url.rfind(L"ws://", 0) == 0) { url = L"http://" + url.substr(5); }
		URL_COMPONENTS parts = {};
		parts.dwStructSize = sizeof(parts);
		WCHAR host[256] = {}, urlPath[2048] = {}, extra[2048] = {};
		parts.lpszHostName = host; parts.dwHostNameLength = 256;
		parts.lpszUrlPath = urlPath; parts.dwUrlPathLength = 2048;
		parts.lpszExtraInfo = extra; parts.dwExtraInfoLength = 2048;
		if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts))
			return false;
		std::wstring pathAndQuery = std::wstring(urlPath) + extra;

		g_session = WinHttpOpen(L"EchoRelay.Patch", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
		if (!g_session) return false;
		g_connection = WinHttpConnect(g_session, host, parts.nPort, 0);
		if (!g_connection) return false;
		HINTERNET request = WinHttpOpenRequest(g_connection, L"GET", pathAndQuery.c_str(), NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0);
		if (!request) return false;
		bool ok = WinHttpSetOption(request, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, NULL, 0)
			&& WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, NULL, 0, 0, 0)
			&& WinHttpReceiveResponse(request, NULL);
		if (ok)
			g_socket = WinHttpWebSocketCompleteUpgrade(request, 0);
		WinHttpCloseHandle(request);
		if (!g_socket) return false;
		g_connected = true;
		return true;
	}

	static bool SendJson(const std::string& json)
	{
		std::string packet;
		UINT64 size = json.size();
		packet.append((const char*)&PACKET_HEADER, 8);
		packet.append((const char*)&SYMBOL_SOCIAL, 8);
		packet.append((const char*)&size, 8);
		packet += json;
		std::lock_guard<std::mutex> guard(g_sendLock);
		if (!g_connected || !g_socket)
			return false;
		return WinHttpWebSocketSend(g_socket, WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE, (PVOID)packet.data(), (DWORD)packet.size()) == NO_ERROR;
	}

	/// <summary>
	/// Turns a social service message into SDK messages/packets.
	/// </summary>
	static void HandleServiceMessage(const Json& json)
	{
		std::string type = json.Str("t");
		if (type == "welcome")
		{
			Log("[SOCIAL] Connected to EchoRelay social service as %s (%llu)", json.Str("name").c_str(), json.U64("id"));
			return;
		}
		if (type == "res")
		{
			UINT64 requestId = json.U64("rid");
			std::string op;
			{
				std::lock_guard<std::mutex> guard(g_queueLock);
				auto it = g_pending.find(requestId);
				if (it == g_pending.end())
					return;
				op = it->second;
				g_pending.erase(it);
			}
			if (!json.Boolean("ok"))
			{
				Log("[SOCIAL] %s failed: %s", op.c_str(), json.Str("msg").c_str());
				Enqueue(ErrorMessage(requestId, (INT32)json.I64("code"), json.Str("msg")));
				return;
			}
			Message* message = New<Message>(NULL);
			message->requestId = requestId;
			if (op == "friends")
				message->type = MSG_USER_GET_LOGGED_IN_USER_FRIENDS;
			if (const Json* users = json.Get("users"))
				message->users = BuildUsers(users, message);
			if (const Json* room = json.Get("room"))
				if (room->type == Json::Object)
					message->room = BuildRoom(*room, message);
			if (const Json* invites = json.Get("invites"))
			{
				message->invites = New<Invites>(message);
				for (const Json& item : invites->items)
					message->invites->items.push_back(BuildInvite(item, message));
			}
			Enqueue(message);
			return;
		}
		if (type == "note")
		{
			std::string kind = json.Str("kind");
			Message* message = New<Message>(NULL);
			if (kind == "roomupdate" && json.Get("room"))
			{
				message->type = MSG_NOTIFICATION_ROOM_ROOM_UPDATE;
				message->room = BuildRoom(*json.Get("room"), message);
			}
			else if (kind == "invite" && json.Get("invite"))
			{
				message->type = MSG_NOTIFICATION_ROOM_INVITE_RECEIVED;
				message->invite = BuildInvite(*json.Get("invite"), message);
				Log("[SOCIAL] Party invite received from %llu", json.Get("invite")->U64("from"));
			}
			else
			{
				Destroy(message);
				return;
			}
			Enqueue(message);
			return;
		}
		if (type == "pkt")
		{
			Packet* packet = New<Packet>(NULL);
			packet->sender = json.U64("from");
			packet->bytes = Base64Decode(json.Str("data"));
			std::lock_guard<std::mutex> guard(g_queueLock);
			g_packets.push_back(packet);
		}
	}

	static void FailPendingRequests()
	{
		std::vector<UINT64> pending;
		{
			std::lock_guard<std::mutex> guard(g_queueLock);
			for (const auto& entry : g_pending)
				pending.push_back(entry.first);
			g_pending.clear();
		}
		for (UINT64 requestId : pending)
			Enqueue(ErrorMessage(requestId, 1, "Lost connection to the EchoRelay social service"));
	}

	static DWORD WINAPI ConnectionThread(LPVOID)
	{
		DWORD retryDelay = 1000;
		for (;;)
		{
			if (!OpenSocket())
			{
				{
					std::lock_guard<std::mutex> guard(g_sendLock);
					CloseSocket();
				}
				Sleep(retryDelay);
				retryDelay = min(retryDelay * 2, (DWORD)30000);
				continue;
			}
			retryDelay = 1000;
			SendJson("{\"t\":\"hello\",\"id\":" + std::to_string(g_localUserId) + "}");

			std::string message;
			std::vector<BYTE> buffer(0x10000);
			for (;;)
			{
				DWORD read = 0;
				WINHTTP_WEB_SOCKET_BUFFER_TYPE bufferType;
				DWORD error = WinHttpWebSocketReceive(g_socket, buffer.data(), (DWORD)buffer.size(), &read, &bufferType);
				if (error != NO_ERROR || bufferType == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE)
					break;
				message.append((const char*)buffer.data(), read);
				if (bufferType != WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE && bufferType != WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE)
					continue;

				// EchoRelay framing: header | symbol | size | payload, possibly several per websocket message.
				size_t offset = 0;
				while (offset + 24 <= message.size())
				{
					UINT64 header, symbol, size;
					memcpy(&header, message.data() + offset, 8);
					memcpy(&symbol, message.data() + offset + 8, 8);
					memcpy(&size, message.data() + offset + 16, 8);
					if (header != PACKET_HEADER || offset + 24 + size > message.size())
						break;
					if (symbol == SYMBOL_SOCIAL)
					{
						Json json;
						if (ParseJson(message.substr(offset + 24, (size_t)size), json))
							HandleServiceMessage(json);
					}
					offset += 24 + (size_t)size;
				}
				message.clear();
			}
			Log("[SOCIAL] Lost connection to the EchoRelay social service, reconnecting");
			{
				std::lock_guard<std::mutex> guard(g_sendLock);
				CloseSocket();
			}
			FailPendingRequests();
			Sleep(1000);
		}
		return 0;
	}

	/// <summary>
	/// Connects to the social service once the Oculus platform knows the local user.
	/// </summary>
	static void EnsureStarted()
	{
		if (g_started || (Real_ovr_GetLoggedInUserID == NULL && g_forcedUserId == 0))
			return;
		UINT64 userId = g_forcedUserId != 0 ? g_forcedUserId : MapUserId(Real_ovr_GetLoggedInUserID());
		if (userId == 0)
			return;
		if (g_started.exchange(true))
			return;
		g_localUserId = userId;
		g_url = GetSocialServiceUrl();
		Log("[SOCIAL] Using EchoRelay social service at %ls for user %llu", g_url.c_str(), userId);
		CreateThread(NULL, 0, ConnectionThread, NULL, 0, NULL);
	}

	/// <summary>
	/// Sends a request to the social service and returns its request id. If the service isn't connected, the request fails
	/// immediately (so the game doesn't wait on it).
	/// </summary>
	static UINT64 Request(const std::string& op, const std::string& fields = "")
	{
		EnsureStarted();
		UINT64 requestId = g_nextRequestId++;
		{
			std::lock_guard<std::mutex> guard(g_queueLock);
			g_pending[requestId] = op;
		}
		std::string json = "{\"t\":\"req\",\"rid\":" + std::to_string(requestId) + ",\"op\":\"" + op + "\"" + fields + "}";
		TRACE("[SOCIAL] request %s", json.c_str());
		if (!SendJson(json))
		{
			{
				std::lock_guard<std::mutex> guard(g_queueLock);
				g_pending.erase(requestId);
			}
			Enqueue(ErrorMessage(requestId, 1, "EchoRelay social service unavailable"));
		}
		return requestId;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Hooks
	// ------------------------------------------------------------------------------------------------------------
#define REAL(ret, name, params) typedef ret (*name##_t) params; static name##_t Real_##name = NULL;

	// Message queue
	REAL(void*, ovr_PopMessage, ())
	REAL(void, ovr_FreeMessage, (void*))
	REAL(UINT32, ovr_Message_GetType, (void*))
	REAL(bool, ovr_Message_IsError, (void*))
	REAL(void*, ovr_Message_GetError, (void*))
	REAL(UINT64, ovr_Message_GetRequestID, (void*))
	REAL(const char*, ovr_Message_GetString, (void*))
	REAL(void*, ovr_Message_GetRoom, (void*))
	REAL(void*, ovr_Message_GetUserArray, (void*))
	REAL(void*, ovr_Message_GetUser, (void*))
	REAL(void*, ovr_Message_GetOrgScopedID, (void*))
	REAL(void*, ovr_Message_GetRoomInviteNotification, (void*))
	REAL(void*, ovr_Message_GetRoomInviteNotificationArray, (void*))
	// Accessors
	REAL(INT32, ovr_Error_GetCode, (void*))
	REAL(INT32, ovr_Error_GetHttpCode, (void*))
	REAL(const char*, ovr_Error_GetMessage, (void*))
	REAL(UINT64, ovr_Room_GetID, (void*))
	REAL(void*, ovr_Room_GetOwner, (void*))
	REAL(void*, ovr_Room_GetUsers, (void*))
	REAL(void*, ovr_Room_GetDataStore, (void*))
	REAL(const char*, ovr_DataStore_GetValue, (void*, const char*))
	REAL(size_t, ovr_UserArray_GetSize, (void*))
	REAL(void*, ovr_UserArray_GetElement, (void*, size_t))
	REAL(bool, ovr_UserArray_HasNextPage, (void*))
	REAL(UINT64, ovr_User_GetID, (void*))
	REAL(const char*, ovr_User_GetOculusID, (void*))
	REAL(const char*, ovr_User_GetPresence, (void*))
	REAL(INT32, ovr_User_GetPresenceStatus, (void*))
	REAL(const char*, ovr_User_GetInviteToken, (void*))
	REAL(UINT64, ovr_OrgScopedID_GetID, (void*))
	REAL(size_t, ovr_RoomInviteNotificationArray_GetSize, (void*))
	REAL(void*, ovr_RoomInviteNotificationArray_GetElement, (void*, size_t))
	REAL(bool, ovr_RoomInviteNotificationArray_HasNextPage, (void*))
	REAL(UINT64, ovr_RoomInviteNotification_GetID, (void*))
	REAL(UINT64, ovr_RoomInviteNotification_GetRoomID, (void*))
	REAL(UINT64, ovr_RoomInviteNotification_GetSentTime, (void*))
	REAL(void*, ovr_Net_ReadPacket, ())
	REAL(const void*, ovr_Packet_GetBytes, (void*))
	REAL(size_t, ovr_Packet_GetSize, (void*))
	REAL(UINT64, ovr_Packet_GetSenderID, (void*))
	REAL(void, ovr_Packet_Free, (void*))
	// Requests (answered by the social service)
	REAL(UINT64, ovr_User_GetLoggedInUserFriends, ())
	REAL(UINT64, ovr_User_GetLoggedInUser, ())
	REAL(UINT64, ovr_User_GetNextUserArrayPage, (void*))
	REAL(UINT64, ovr_User_GetOrgScopedID, (UINT64))
	REAL(UINT64, ovr_Room_CreateAndJoinPrivate2, (INT32, UINT32, void*))
	REAL(UINT64, ovr_Room_Join2, (UINT64, void*))
	REAL(UINT64, ovr_Room_Leave, (UINT64))
	REAL(UINT64, ovr_Room_Get, (UINT64))
	REAL(UINT64, ovr_Room_InviteUser, (UINT64, const char*))
	REAL(UINT64, ovr_Room_KickUser, (UINT64, UINT64, INT32))
	REAL(UINT64, ovr_Room_UpdateOwner, (UINT64, UINT64))
	REAL(UINT64, ovr_Room_UpdateMembershipLockStatus, (UINT64, INT32))
	REAL(UINT64, ovr_Room_UpdateDataStore, (UINT64, KeyValuePair*, UINT32))
	REAL(UINT64, ovr_Room_LaunchInvitableUserFlow, (UINT64))
	REAL(UINT64, ovr_Room_GetInvitableUsers2, (void*))
	REAL(UINT64, ovr_Notification_GetRoomInvites, ())
	REAL(UINT64, ovr_Notification_GetNextRoomInviteNotificationArrayPage, (void*))
	REAL(UINT64, ovr_Notification_MarkAsRead, (UINT64))
	REAL(bool, ovr_Net_SendPacketToCurrentRoom, (size_t, const void*, INT32))
	REAL(bool, ovr_Net_SendPacket, (UINT64, size_t, const void*, INT32))
	REAL(bool, ovr_Net_AcceptForCurrentRoom, ())
	REAL(void, ovr_Net_CloseForCurrentRoom, ())

	// --- message queue
	static void PatchPnsOvrImports();

	static void* Hook_ovr_PopMessage()
	{
		PatchPnsOvrImports();
		EnsureStarted();
		{
			std::lock_guard<std::mutex> guard(g_queueLock);
			if (!g_messages.empty())
			{
				Message* message = g_messages.front();
				g_messages.pop_front();
				TRACE("[SOCIAL] pop msg=%p type=0x%08X rid=%llu error=%d room=%llu users=%zu orgid=%llu invites=%zu", message, message->type, message->requestId,
					message->error != NULL, message->room ? message->room->id : 0, message->users ? message->users->items.size() : (size_t)-1,
					message->orgId ? message->orgId->id : 0, message->invites ? message->invites->items.size() : (size_t)-1);
				return message;
			}
		}
		return Real_ovr_PopMessage();
	}
	static void Hook_ovr_FreeMessage(void* h) { if (Message* m = (Message*)Ours(h, K_MESSAGE)) ReleaseMessage(m); else Real_ovr_FreeMessage(h); }
	static UINT32 Hook_ovr_Message_GetType(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->type : Real_ovr_Message_GetType(h); }
	static bool Hook_ovr_Message_IsError(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->error != NULL : Real_ovr_Message_IsError(h); }
	static void* Hook_ovr_Message_GetError(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->error : Real_ovr_Message_GetError(h); }
	static UINT64 Hook_ovr_Message_GetRequestID(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); if (!m) { UINT64 r = Real_ovr_Message_GetRequestID(h); TRACE("[SOCIAL] real message %p rid=%llu type=0x%08X", h, r, Real_ovr_Message_GetType(h)); return r; } return m->requestId; }
	static const char* Hook_ovr_Message_GetString(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->string.c_str() : Real_ovr_Message_GetString(h); }
	static void* Hook_ovr_Message_GetRoom(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->room : Real_ovr_Message_GetRoom(h); }
	static void* Hook_ovr_Message_GetUserArray(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->users : Real_ovr_Message_GetUserArray(h); }
	static void* Hook_ovr_Message_GetUser(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->user : Real_ovr_Message_GetUser(h); }
	static void* Hook_ovr_Message_GetOrgScopedID(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->orgId : Real_ovr_Message_GetOrgScopedID(h); }
	static void* Hook_ovr_Message_GetRoomInviteNotification(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->invite : Real_ovr_Message_GetRoomInviteNotification(h); }
	static void* Hook_ovr_Message_GetRoomInviteNotificationArray(void* h) { Message* m = (Message*)Ours(h, K_MESSAGE); return m ? m->invites : Real_ovr_Message_GetRoomInviteNotificationArray(h); }

	// --- accessors
	static INT32 Hook_ovr_Error_GetCode(void* h) { Error* e = (Error*)Ours(h, K_ERROR); return e ? e->code : Real_ovr_Error_GetCode(h); }
	static INT32 Hook_ovr_Error_GetHttpCode(void* h) { Error* e = (Error*)Ours(h, K_ERROR); return e ? 0 : Real_ovr_Error_GetHttpCode(h); }
	static const char* Hook_ovr_Error_GetMessage(void* h) { Error* e = (Error*)Ours(h, K_ERROR); return e ? e->message.c_str() : Real_ovr_Error_GetMessage(h); }
	static UINT64 Hook_ovr_Room_GetID(void* h) { Room* r = (Room*)Ours(h, K_ROOM); return r ? r->id : Real_ovr_Room_GetID(h); }
	static void* Hook_ovr_Room_GetOwner(void* h) { Room* r = (Room*)Ours(h, K_ROOM); return r ? r->owner : Real_ovr_Room_GetOwner(h); }
	static void* Hook_ovr_Room_GetUsers(void* h) { Room* r = (Room*)Ours(h, K_ROOM); return r ? r->users : Real_ovr_Room_GetUsers(h); }
	static void* Hook_ovr_Room_GetDataStore(void* h) { Room* r = (Room*)Ours(h, K_ROOM); return r ? r->data : Real_ovr_Room_GetDataStore(h); }
	static const char* Hook_ovr_DataStore_GetValue(void* h, const char* key)
	{
		DataStore* data = (DataStore*)Ours(h, K_DATASTORE);
		if (!data)
			return Real_ovr_DataStore_GetValue(h, key);
		auto it = key ? data->values.find(key) : data->values.end();
		return it == data->values.end() ? NULL : it->second.c_str();
	}
	static size_t Hook_ovr_UserArray_GetSize(void* h) { Users* u = (Users*)Ours(h, K_USERS); return u ? u->items.size() : Real_ovr_UserArray_GetSize(h); }
	static void* Hook_ovr_UserArray_GetElement(void* h, size_t i) { Users* u = (Users*)Ours(h, K_USERS); return u ? (i < u->items.size() ? u->items[i] : NULL) : Real_ovr_UserArray_GetElement(h, i); }
	static bool Hook_ovr_UserArray_HasNextPage(void* h) { return Ours(h, K_USERS) ? false : Real_ovr_UserArray_HasNextPage(h); }
	static UINT64 Hook_ovr_User_GetID(void* h) { User* u = (User*)Ours(h, K_USER); if (u) TRACE("[SOCIAL] User_GetID(%p) -> %llu", h, u->id); else TRACE("[SOCIAL] User_GetID(real %p)", h); return u ? u->id : MapUserId(Real_ovr_User_GetID(h)); }
	static UINT64 Hook_ovr_GetLoggedInUserID() { return g_forcedUserId != 0 ? g_forcedUserId : MapUserId(Real_ovr_GetLoggedInUserID()); }
	static const char* Hook_ovr_User_GetOculusID(void* h)
	{
		User* u = (User*)Ours(h, K_USER);
		if (u)
		{
			TRACE("[SOCIAL] User_GetOculusID(%p) -> %s", h, u->name.c_str());
			return u->name.c_str();
		}
		// Revive's shared user has a placeholder name (NO_CONFIG_FOUND); show the player's display name instead.
		if (h != NULL && IsSharedReviveId(Real_ovr_User_GetID(h)) && MapUserId(Real_ovr_User_GetID(h)) != Real_ovr_User_GetID(h) && !g_uniqueUserName.empty())
			return g_uniqueUserName.c_str();
		// Any other placeholder (or missing) name: the player's display name from the config.
		const char* name = Real_ovr_User_GetOculusID(h);
		if ((name == NULL || name[0] == 0 || strcmp(name, "NO_CONFIG_FOUND") == 0) && !ConfigDisplayName().empty())
			return ConfigDisplayName().c_str();
		return name;
	}
	static const char* Hook_ovr_User_GetPresence(void* h) { User* u = (User*)Ours(h, K_USER); return u ? u->presence.c_str() : Real_ovr_User_GetPresence(h); }
	static INT32 Hook_ovr_User_GetPresenceStatus(void* h) { User* u = (User*)Ours(h, K_USER); return u ? (u->online ? PRESENCE_ONLINE : PRESENCE_OFFLINE) : Real_ovr_User_GetPresenceStatus(h); }
	static const char* Hook_ovr_User_GetInviteToken(void* h) { User* u = (User*)Ours(h, K_USER); return u ? u->token.c_str() : Real_ovr_User_GetInviteToken(h); }
	static UINT64 Hook_ovr_OrgScopedID_GetID(void* h) { OrgId* o = (OrgId*)Ours(h, K_ORGID); if (o) TRACE("[SOCIAL] OrgScopedID_GetID(%p) -> %llu", h, o->id); else TRACE("[SOCIAL] OrgScopedID_GetID(real %p)", h); return o ? o->id : Real_ovr_OrgScopedID_GetID(h); }
	static size_t Hook_ovr_RoomInviteNotificationArray_GetSize(void* h) { Invites* a = (Invites*)Ours(h, K_INVITES); return a ? a->items.size() : Real_ovr_RoomInviteNotificationArray_GetSize(h); }
	static void* Hook_ovr_RoomInviteNotificationArray_GetElement(void* h, size_t i) { Invites* a = (Invites*)Ours(h, K_INVITES); return a ? (i < a->items.size() ? a->items[i] : NULL) : Real_ovr_RoomInviteNotificationArray_GetElement(h, i); }
	static bool Hook_ovr_RoomInviteNotificationArray_HasNextPage(void* h) { return Ours(h, K_INVITES) ? false : Real_ovr_RoomInviteNotificationArray_HasNextPage(h); }
	static UINT64 Hook_ovr_RoomInviteNotification_GetID(void* h) { Invite* i = (Invite*)Ours(h, K_INVITE); return i ? i->id : Real_ovr_RoomInviteNotification_GetID(h); }
	static UINT64 Hook_ovr_RoomInviteNotification_GetRoomID(void* h) { Invite* i = (Invite*)Ours(h, K_INVITE); return i ? i->room : Real_ovr_RoomInviteNotification_GetRoomID(h); }
	static UINT64 Hook_ovr_RoomInviteNotification_GetSentTime(void* h) { Invite* i = (Invite*)Ours(h, K_INVITE); return i ? i->sent : Real_ovr_RoomInviteNotification_GetSentTime(h); }

	// --- room packets
	static void* Hook_ovr_Net_ReadPacket()
	{
		{
			std::lock_guard<std::mutex> guard(g_queueLock);
			if (!g_packets.empty())
			{
				Packet* packet = g_packets.front();
				g_packets.pop_front();
				return packet;
			}
		}
		return Real_ovr_Net_ReadPacket();
	}
	static const void* Hook_ovr_Packet_GetBytes(void* h) { Packet* p = (Packet*)Ours(h, K_PACKET); return p ? p->bytes.data() : Real_ovr_Packet_GetBytes(h); }
	static size_t Hook_ovr_Packet_GetSize(void* h) { Packet* p = (Packet*)Ours(h, K_PACKET); return p ? p->bytes.size() : Real_ovr_Packet_GetSize(h); }
	static UINT64 Hook_ovr_Packet_GetSenderID(void* h) { Packet* p = (Packet*)Ours(h, K_PACKET); return p ? p->sender : Real_ovr_Packet_GetSenderID(h); }
	static void Hook_ovr_Packet_Free(void* h) { if (Packet* p = (Packet*)Ours(h, K_PACKET)) Destroy(p); else Real_ovr_Packet_Free(h); }
	static bool Hook_ovr_Net_SendPacketToCurrentRoom(size_t size, const void* bytes, INT32 policy)
	{
		return SendJson("{\"t\":\"pkt\",\"to\":0,\"data\":\"" + Base64Encode(bytes, size) + "\"}");
	}
	static bool Hook_ovr_Net_SendPacket(UINT64 userId, size_t size, const void* bytes, INT32 policy)
	{
		return SendJson("{\"t\":\"pkt\",\"to\":" + std::to_string(userId) + ",\"data\":\"" + Base64Encode(bytes, size) + "\"}");
	}
	static bool Hook_ovr_Net_AcceptForCurrentRoom() { return true; }
	static void Hook_ovr_Net_CloseForCurrentRoom() {}

	// --- requests
	static UINT64 Hook_ovr_User_GetLoggedInUserFriends() { return Request("friends"); }

	/// <summary>
	/// The local player (the party screen's own entry, among others). Without Oculus services the real request fails
	/// ("Must call get_signature first"), leaving that entry blank, and Revive answers with a placeholder name
	/// (NO_CONFIG_FOUND). Answered here with the player's own id and their display name from the config.
	/// </summary>
	static UINT64 Hook_ovr_User_GetLoggedInUser()
	{
		UINT64 id = g_forcedUserId != 0 ? g_forcedUserId : (Real_ovr_GetLoggedInUserID != NULL ? MapUserId(Real_ovr_GetLoggedInUserID()) : 0);
		if (id == 0 || ConfigDisplayName().empty())
			return Real_ovr_User_GetLoggedInUser();
		Message* message = New<Message>(NULL);
		message->type = MSG_USER_GET_LOGGED_IN_USER;
		message->requestId = g_nextRequestId++;
		message->user = New<User>(message);
		message->user->id = id;
		message->user->name = ConfigDisplayName();
		message->user->online = true;
		message->user->token = std::to_string(id);
		UINT64 requestId = message->requestId;
		Log("[SOCIAL] Logged in user: %s (%llu)", message->user->name.c_str(), id);
		Enqueue(message);
		return requestId;
	}
	static UINT64 Hook_ovr_Room_GetInvitableUsers2(void* options) { return Request("invitable"); }
	static UINT64 Hook_ovr_User_GetNextUserArrayPage(void* h)
	{
		if (!Ours(h, K_USERS))
			return Real_ovr_User_GetNextUserArrayPage(h);
		UINT64 requestId = g_nextRequestId++;
		Enqueue(ErrorMessage(requestId, 1, "No more pages"));
		return requestId;
	}
	static UINT64 Hook_ovr_User_GetOrgScopedID(UINT64 userId)
	{
		// Echo accounts are keyed by org-scoped id; the ids EchoRelay hands out already are org-scoped, and so are the local
		// user's (app- and org-scoped ids are the same for this app). Answer locally.
		UINT64 requestId = g_nextRequestId++;
		TRACE("[SOCIAL] User_GetOrgScopedID(%llu) -> rid %llu", userId, requestId);
		Message* message = New<Message>(NULL);
		message->requestId = requestId;
		message->orgId = New<OrgId>(message);
		message->orgId->id = userId;
		Enqueue(message);
		return requestId;
	}
	static UINT64 Hook_ovr_Room_CreateAndJoinPrivate2(INT32 joinPolicy, UINT32 maxUsers, void* options) { return Request("create", ",\"max\":" + std::to_string(maxUsers)); }
	static UINT64 Hook_ovr_Room_Join2(UINT64 roomId, void* options) { return Request("join", ",\"room\":" + std::to_string(roomId)); }
	static UINT64 Hook_ovr_Room_Leave(UINT64 roomId) { return Request("leave", ",\"room\":" + std::to_string(roomId)); }
	static UINT64 Hook_ovr_Room_Get(UINT64 roomId) { return Request("get", ",\"room\":" + std::to_string(roomId)); }
	static UINT64 Hook_ovr_Room_InviteUser(UINT64 roomId, const char* token) { return Request("invite", ",\"room\":" + std::to_string(roomId) + ",\"token\":" + JsonString(token ? token : "")); }
	static UINT64 Hook_ovr_Room_KickUser(UINT64 roomId, UINT64 userId, INT32 duration) { return Request("kick", ",\"room\":" + std::to_string(roomId) + ",\"user\":" + std::to_string(userId)); }
	static UINT64 Hook_ovr_Room_UpdateOwner(UINT64 roomId, UINT64 userId) { return Request("owner", ",\"room\":" + std::to_string(roomId) + ",\"user\":" + std::to_string(userId)); }
	static UINT64 Hook_ovr_Room_UpdateMembershipLockStatus(UINT64 roomId, INT32 status)
	{
		return Request("lock", ",\"room\":" + std::to_string(roomId) + ",\"locked\":" + (status == LOCK_STATUS_LOCK ? "true" : "false"));
	}
	static UINT64 Hook_ovr_Room_UpdateDataStore(UINT64 roomId, KeyValuePair* data, UINT32 count)
	{
		std::string values;
		for (UINT32 i = 0; data != NULL && i < count; i++)
		{
			if (data[i].key == NULL)
				continue;
			std::string value = data[i].valueType == 0 ? (data[i].stringValue ? data[i].stringValue : "")
				: data[i].valueType == 1 ? std::to_string(data[i].intValue) : std::to_string(data[i].doubleValue);
			values += (values.empty() ? "" : ",") + JsonString(data[i].key) + ":" + JsonString(value);
		}
		return Request("data", ",\"room\":" + std::to_string(roomId) + ",\"data\":{" + values + "}");
	}
	static UINT64 Hook_ovr_Room_LaunchInvitableUserFlow(UINT64 roomId)
	{
		// The Oculus invite overlay doesn't exist here; the tablet's own invite list (GetInvitableUsers2) works instead.
		UINT64 requestId = g_nextRequestId++;
		Enqueue(ErrorMessage(requestId, 1, "Use the tablet's invite list"));
		return requestId;
	}
	static UINT64 Hook_ovr_Notification_GetRoomInvites() { return Request("invites"); }
	static UINT64 Hook_ovr_Notification_GetNextRoomInviteNotificationArrayPage(void* h)
	{
		if (!Ours(h, K_INVITES))
			return Real_ovr_Notification_GetNextRoomInviteNotificationArrayPage(h);
		UINT64 requestId = g_nextRequestId++;
		Enqueue(ErrorMessage(requestId, 1, "No more pages"));
		return requestId;
	}
	static UINT64 Hook_ovr_Notification_MarkAsRead(UINT64 notificationId) { return Request("markread", ",\"id\":" + std::to_string(notificationId)); }

	// ------------------------------------------------------------------------------------------------------------
	// Installation
	// ------------------------------------------------------------------------------------------------------------
	struct HookEntry { const char* name; PVOID* real; PVOID hook; };
#define HOOK(name) { #name, (PVOID*)&Real_##name, (PVOID)Hook_##name }

	static HookEntry g_hooks[] = {
		HOOK(ovr_PopMessage), HOOK(ovr_FreeMessage), HOOK(ovr_Message_GetType), HOOK(ovr_Message_IsError), HOOK(ovr_Message_GetError),
		HOOK(ovr_Message_GetRequestID), HOOK(ovr_Message_GetString), HOOK(ovr_Message_GetRoom), HOOK(ovr_Message_GetUserArray),
		HOOK(ovr_Message_GetUser), HOOK(ovr_Message_GetOrgScopedID), HOOK(ovr_Message_GetRoomInviteNotification),
		HOOK(ovr_Message_GetRoomInviteNotificationArray),
		HOOK(ovr_Error_GetCode), HOOK(ovr_Error_GetHttpCode), HOOK(ovr_Error_GetMessage),
		HOOK(ovr_Room_GetID), HOOK(ovr_Room_GetOwner), HOOK(ovr_Room_GetUsers), HOOK(ovr_Room_GetDataStore), HOOK(ovr_DataStore_GetValue),
		HOOK(ovr_UserArray_GetSize), HOOK(ovr_UserArray_GetElement), HOOK(ovr_UserArray_HasNextPage),
		HOOK(ovr_User_GetID), HOOK(ovr_User_GetOculusID), HOOK(ovr_User_GetPresence), HOOK(ovr_User_GetPresenceStatus), HOOK(ovr_User_GetInviteToken),
		HOOK(ovr_OrgScopedID_GetID),
		HOOK(ovr_RoomInviteNotificationArray_GetSize), HOOK(ovr_RoomInviteNotificationArray_GetElement), HOOK(ovr_RoomInviteNotificationArray_HasNextPage),
		HOOK(ovr_RoomInviteNotification_GetID), HOOK(ovr_RoomInviteNotification_GetRoomID), HOOK(ovr_RoomInviteNotification_GetSentTime),
		HOOK(ovr_Net_ReadPacket), HOOK(ovr_Packet_GetBytes), HOOK(ovr_Packet_GetSize), HOOK(ovr_Packet_GetSenderID), HOOK(ovr_Packet_Free),
		HOOK(ovr_User_GetLoggedInUserFriends), HOOK(ovr_User_GetNextUserArrayPage), HOOK(ovr_User_GetOrgScopedID), HOOK(ovr_User_GetLoggedInUser),
		HOOK(ovr_Room_CreateAndJoinPrivate2), HOOK(ovr_Room_Join2), HOOK(ovr_Room_Leave), HOOK(ovr_Room_Get), HOOK(ovr_Room_InviteUser),
		HOOK(ovr_Room_KickUser), HOOK(ovr_Room_UpdateOwner), HOOK(ovr_Room_UpdateMembershipLockStatus), HOOK(ovr_Room_UpdateDataStore),
		HOOK(ovr_Room_LaunchInvitableUserFlow), HOOK(ovr_Room_GetInvitableUsers2),
		HOOK(ovr_Notification_GetRoomInvites), HOOK(ovr_Notification_GetNextRoomInviteNotificationArrayPage), HOOK(ovr_Notification_MarkAsRead),
		HOOK(ovr_Net_SendPacketToCurrentRoom), HOOK(ovr_Net_SendPacket), HOOK(ovr_Net_AcceptForCurrentRoom), HOOK(ovr_Net_CloseForCurrentRoom),
		HOOK(ovr_GetLoggedInUserID),
	};

	static bool g_hooked = false;
	static std::atomic<bool> g_importsPatched(false);

	/// <summary>
	/// Points pnsovr.dll's imports of the SDK functions we answer at our hooks.
	/// Hooking pnsovr's import table (rather than the SDK's code) matters because Revive detours some SDK functions itself:
	/// its ovr_User_GetID, for one, returns its fixed user id for every user handle, which made every friend look like the
	/// local player. Calls for handles that aren't ours still go to the original import (through Revive and the real SDK).
	/// </summary>
	static void PatchPnsOvrImports()
	{
		if (g_importsPatched)
			return;
		BYTE* base = (BYTE*)GetModuleHandleA("pnsovr.dll");
		if (base == NULL)
			return;
		if (g_importsPatched.exchange(true))
			return;

		IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
		IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
		int patched = 0;
		for (IMAGE_IMPORT_DESCRIPTOR* desc = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); desc->Name != 0; desc++)
		{
			if (_stricmp((const char*)(base + desc->Name), "LibOVRPlatform64_1.dll") != 0)
				continue;
			IMAGE_THUNK_DATA64* names = (IMAGE_THUNK_DATA64*)(base + (desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk));
			IMAGE_THUNK_DATA64* slots = (IMAGE_THUNK_DATA64*)(base + desc->FirstThunk);
			for (; names->u1.AddressOfData != 0; names++, slots++)
			{
				if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal))
					continue;
				const char* name = (const char*)((IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData))->Name;
				for (const HookEntry& entry : g_hooks)
				{
					if (strcmp(entry.name, name) != 0 || entry.hook == (PVOID)Hook_ovr_PopMessage)
						continue;
					DWORD oldProtect;
					if (VirtualProtect(&slots->u1.Function, sizeof(ULONGLONG), PAGE_READWRITE, &oldProtect))
					{
						*entry.real = (PVOID)slots->u1.Function;
						slots->u1.Function = (ULONGLONG)entry.hook;
						VirtualProtect(&slots->u1.Function, sizeof(ULONGLONG), oldProtect, &oldProtect);
						patched++;
					}
				}
			}
		}
		Log("[SOCIAL] Redirected %d of pnsovr.dll's Oculus Platform SDK imports to EchoRelay", patched);
	}

	/// <summary>
	/// pnsovr.dll's RadPluginInit, called by the game right after loading the plugin (imports bound, nothing run yet).
	/// Redirecting pnsovr's imports there, before its Oculus login, makes the login use the player's own id too.
	/// </summary>
	typedef INT64(*RadPluginInit_t)();
	static RadPluginInit_t Real_RadPluginInit = NULL;
	static INT64 Hook_RadPluginInit()
	{
		PatchPnsOvrImports();
		return Real_RadPluginInit();
	}

	VOID SetLocalUserId(UINT64 userId)
	{
		g_forcedUserId = userId;
	}

	VOID HookPnsOvrModule(HMODULE module)
	{
		if (Real_RadPluginInit != NULL)
			return;
		Real_RadPluginInit = (RadPluginInit_t)GetProcAddress(module, "RadPluginInit");
		if (Real_RadPluginInit == NULL)
			return;
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach((PVOID*)&Real_RadPluginInit, (PVOID)Hook_RadPluginInit);
		if (DetourTransactionCommit() != NO_ERROR)
			Log("[SOCIAL] Hooking pnsovr.dll's RadPluginInit failed; its imports are redirected on the first message instead");
	}

	VOID HookPlatformModule(HMODULE module, const WCHAR* name)
	{
		if (g_hooked)
			return;

		// Resolve everything first: only hook if this module provides the whole SDK surface pnsovr uses.
		for (const HookEntry& entry : g_hooks)
		{
			PVOID target = (PVOID)GetProcAddress(module, entry.name);
			if (target == NULL)
			{
				Log("[SOCIAL] %ls lacks %s, parties/friends not hooked", name, entry.name);
				return;
			}
			*entry.real = target;
		}

		// pnsovr looks ovr_PopMessage up with GetProcAddress rather than importing it, so that one is detoured in place.
		// Everything else is redirected in pnsovr's import table once it's loaded (PatchPnsOvrImports).
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach((PVOID*)&Real_ovr_PopMessage, (PVOID)Hook_ovr_PopMessage);
		LONG error = DetourTransactionCommit();
		if (error != NO_ERROR)
		{
			Log("[SOCIAL] Hooking ovr_PopMessage in %ls failed (%ld), parties/friends unavailable", name, error);
			return;
		}
		g_hooked = true;
		Log("[SOCIAL] Hooked the Oculus Platform SDK in %ls: parties and friends go through EchoRelay", name);
	}
}
