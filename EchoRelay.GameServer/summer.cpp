#include "pch.h"
#include "summer.h"
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <winhttp.h>
#include <cstdarg>
#include <share.h>
#include <cstdint>
#include <string>
#include <vector>
#include <deque>
#include <mutex>
#include <atomic>

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "iphlpapi.lib")

namespace Summer
{
	// ------------------------------------------------------------------------------------------------------------
	// Engine addresses for each lobby build (relative to the image base, 0x140000000).
	// ------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The lobby build specific parts of this library: three engine functions and the prologue the SetState hook relocates.
	/// Both builds share the IServerLib interface and CNSLobby glue (the halloween build's is an older revision of the same code).
	/// </summary>
	struct BuildProfile
	{
		const CHAR* name;
		DWORD executableTimestamp;
		/// Broadcaster::ReceiveLocalEvent(broadcaster, eventId, name, msg, msgSize): delivers a message to the game's local listeners.
		UINT64 receiveLocalEvent;
		/// The hash used for local event ids (differs from the CSymbol64 used for service message ids).
		UINT64 eventSymbol;
		/// NetGame::SetState(netgame, state). State 7 is "in game": the session's level finished loading.
		UINT64 netGameSetState;
		/// The first bytes of NetGame::SetState, which the hook relocates (all position-independent; whole instructions,
		/// at least the 14 bytes the hook's jump takes).
		BYTE setStatePrologue[16];
		/// How many of setStatePrologue's bytes are the prologue.
		SIZE_T setStatePrologueSize;
		/// The NetGame state "in game" (the session's level finished loading).
		INT32 inGameState;
		/// The build's session success message is SNSLobbySessionSuccessv3: v4 without its leading u64 game type.
		BOOL sessionSuccessV3;
		/// IServerLib::RequestRegistration takes no region: (server id, version lock, local config).
		BOOL registrationWithoutRegion;
		/// SNSLobbyStartSessionv2 has no lobby type: u64 entrant slots | guid session | settings json \0 (halloween 2017).
		BOOL startSessionWithoutLobbyType;
	};

	static const BuildProfile BUILDS[] = {
		{ "summer (rad15_summer, goldmaster 340872)", 0x5D388D3C, 0xE700A0, 0x17620, 0x604610,
			{ 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x70, 0x8B, 0xF2, 0x48, 0x8B, 0xF9 }, 15, 7, FALSE, FALSE },
		{ "halloween (rad15_halloween, goldmaster 253636)", 0x5BC7B897, 0x46E9E0, 0x95270, 0x8EEC90,
			{ 0x48, 0x89, 0x6C, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x50, 0x8B, 0xEA, 0x48, 0x8B, 0xF9 }, 15, 7, FALSE, FALSE },
		// The christmas 2018 ("winter") build: the same code as halloween, relocated.
		{ "christmas 2018 (rad15_winter, goldmaster 268902)", 0x5C17F6B9, 0x47BDE0, 0xA67C0, 0x90A7E0,
			{ 0x48, 0x89, 0x6C, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x50, 0x8B, 0xEA, 0x48, 0x8B, 0xF9 }, 15, 7, FALSE, FALSE },
		// The April Fools 2019 build (goldmaster 298283): christmas 2018's code, relocated.
		{ "april fools 2019 (goldmaster 298283)", 0x5C9EA0A9, 0x450410, 0x53A30, 0x92C1C0,
			{ 0x48, 0x89, 0x6C, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x50, 0x8B, 0xEA, 0x48, 0x8B, 0xF9 }, 15, 7, FALSE, FALSE },
		// The christmas 2017 build (rad14, EchoArena.exe) uses the message's CSymbol64 as the local event id (eventSymbol 0),
		// has no "loading global" NetGame state (so "in game" is 5) and the older SNSLobbySessionSuccessv3.
		{ "christmas 2017 (rad14, ea_rel6_0)", 0x5A39494F, 0x267630, 0, 0x3D2330,
			{ 0x48, 0x89, 0x5C, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x50, 0x8B, 0xFA, 0x48, 0x8B, 0xD9 }, 15, 5, TRUE, TRUE },
		// The halloween 2017 build (rad14, Echo Arena 1.76): christmas 2017's code two months earlier. Its NetGame state is
		// 64-bit (the hook reads the low half) and SetState's prologue is 16 bytes.
		{ "halloween 2017 (rad14, release4_5)", 0x59E8F804, 0x2662E0, 0, 0x3B0030,
			{ 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x60, 0x48, 0x8B, 0xF2, 0x48, 0x8B, 0xF9 }, 16, 5, TRUE, TRUE, TRUE },
		// Echo Arena 1.58 (September 2017, rad14, publisher lock release4): halloween 2017's code a month earlier.
		{ "1.58 2017 (rad14, release4)", 0x59B81FFD, 0x260840, 0, 0x3A9D60,
			{ 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x60, 0x48, 0x8B, 0xF2, 0x48, 0x8B, 0xF9 }, 16, 5, TRUE, TRUE, TRUE },
		// Lone Echo's final patch (loneecho.exe, March 2019, rad14): Echo Arena's multiplayer code, as halloween 2017's.
		{ "lone echo (rad14, localization_dev)", 0x5C9D6E49, 0x27CAF0, 0, 0x3CF060,
			{ 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x60, 0x48, 0x8B, 0xF2, 0x48, 0x8B, 0xF9 }, 16, 5, TRUE, TRUE, TRUE },
	};

	typedef UINT64 ReceiveLocalEventFunc(VOID* broadcaster, UINT64 eventId, const CHAR* name, const VOID* msg, UINT64 msgSize);
	typedef UINT64 EventSymbolFunc(const CHAR* name);
	typedef UINT64 SetStateFunc(VOID* netgame, UINT64 state, UINT64 a3, UINT64 a4);

	// ------------------------------------------------------------------------------------------------------------
	// EchoRelay ServerDB message symbols (see messages.h / EchoRelay.Core/Server/Messages/ServerDB).
	// ------------------------------------------------------------------------------------------------------------
	const UINT64 PACKET_HEADER = 0xBB8CE7A278BB40F6;
	const UINT64 SYM_REGISTRATION_REQUEST = 0x7777777777777777;
	const UINT64 SYM_START_SESSION = 0x7777777777770000;
	const UINT64 SYM_SESSION_STARTED = 0x7777777777770100;
	const UINT64 SYM_END_SESSION = 0x7777777777770200;
	const UINT64 SYM_PLAYER_SESSIONS_LOCKED = 0x7777777777770300;
	const UINT64 SYM_PLAYER_SESSIONS_UNLOCKED = 0x7777777777770400;
	const UINT64 SYM_ACCEPT_PLAYERS = 0x7777777777770500;
	const UINT64 SYM_PLAYERS_ACCEPTED = 0x7777777777770600;
	const UINT64 SYM_PLAYERS_REJECTED = 0x7777777777770700;
	const UINT64 SYM_REMOVE_PLAYER = 0x7777777777770800;
	const UINT64 SYM_REGISTRATION_SUCCESS = 0xB57A31CDD0F6FEDF; // SNSLobbyRegistrationSuccess
	const UINT64 SYM_REGISTRATION_FAILURE = 0xB56F25C7DFE6FFC9; // SNSLobbyRegistrationFailure
	const UINT64 SYM_SESSION_SUCCESS_V4 = 0x6D4DE3650EE3110E;   // SNSLobbySessionSuccessv4

	// ------------------------------------------------------------------------------------------------------------
	// State
	// ------------------------------------------------------------------------------------------------------------

	struct GuidArray { GUID* items; UINT64 count; };

	/// <summary>
	/// The summer IServerLib object. The game only uses the vtable pointer at offset 0.
	/// </summary>
	struct SummerServerLib
	{
		VOID** vtbl;
	};

	struct IncomingMessage
	{
		UINT64 symbol;
		std::string payload;
	};

	static CHAR* g_base = (CHAR*)GetModuleHandleA(NULL);
	static const BuildProfile* g_build = NULL;
	static ReceiveLocalEventFunc* ReceiveLocalEvent = NULL;
	static EventSymbolFunc* EventSymbol = NULL;
	static SetStateFunc* SetStateTrampoline = NULL;

	static SummerServerLib* g_lib = NULL;
	static VOID* g_lobby = NULL;
	static VOID* g_broadcaster = NULL;

	static std::mutex g_logLock;
	static FILE* g_log = NULL;

	// ServerDB connection
	static std::wstring g_serverDbUrl;
	static HINTERNET g_session = NULL, g_connection = NULL, g_request = NULL, g_socket = NULL;
	static std::mutex g_sendLock;
	static std::mutex g_queueLock;
	static std::deque<IncomingMessage> g_incoming;
	static HANDLE g_thread = NULL;
	static std::atomic<bool> g_stop(false);
	static std::atomic<bool> g_connected(false);

	// Registration / session
	static std::atomic<bool> g_registrationRequested(false);
	static std::atomic<bool> g_registered(false);
	static std::atomic<bool> g_sessionActive(false);
	static std::atomic<bool> g_sessionLoadReported(false);
	static std::atomic<bool> g_inGame(false);
	static std::atomic<bool> g_needRegistration(false);
	static std::string g_registrationMessage;

	// ------------------------------------------------------------------------------------------------------------
	// Logging (the final build's WriteLog address does not exist in this build, so we keep our own log next to the exe).
	// ------------------------------------------------------------------------------------------------------------
	static VOID Log(const CHAR* format, ...)
	{
		std::lock_guard<std::mutex> guard(g_logLock);
		if (g_log == NULL)
		{
			CHAR path[MAX_PATH];
			GetModuleFileNameA(NULL, path, MAX_PATH);
			CHAR* slash = strrchr(path, '\\');
			if (slash != NULL)
				*(slash + 1) = 0;
			strcat_s(path, "echorelay_gameserver.log");
			g_log = _fsopen(path, "a", _SH_DENYNO);
			if (g_log == NULL)
				return;
		}
		SYSTEMTIME t;
		GetLocalTime(&t);
		fprintf(g_log, "[%02d:%02d:%02d.%03d] [ECHORELAY.GAMESERVER] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
		va_list args;
		va_start(args, format);
		vfprintf(g_log, format, args);
		va_end(args);
		fputc('\n', g_log);
		fflush(g_log);
	}

	/// <summary>
	/// Finds the profile for the build hosting us, or NULL if it is not a lobby build we support.
	/// </summary>
	static const BuildProfile* DetectBuild()
	{
		IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)g_base;
		IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(g_base + dos->e_lfanew);
		for (const BuildProfile& build : BUILDS)
		{
			if (nt->FileHeader.TimeDateStamp == build.executableTimestamp)
				return &build;
		}
		return NULL;
	}

	BOOL IsLobbyBuild()
	{
		return DetectBuild() != NULL;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Local config (./_local/config.json relative to the game root, two directories above bin\win7\echovr.exe).
	// ------------------------------------------------------------------------------------------------------------
	static std::string ReadFileText(const std::string& path)
	{
		FILE* f = NULL;
		if (fopen_s(&f, path.c_str(), "rb") != 0 || f == NULL)
			return "";
		std::string data;
		CHAR buf[4096];
		size_t n;
		while ((n = fread(buf, 1, sizeof(buf), f)) > 0)
			data.append(buf, n);
		fclose(f);
		return data;
	}

	/// <summary>
	/// Extracts a string value for a key from a flat JSON document (enough for _local/config.json).
	/// </summary>
	static std::string JsonGetString(const std::string& json, const std::string& key)
	{
		size_t pos = json.find("\"" + key + "\"");
		if (pos == std::string::npos)
			return "";
		pos = json.find(':', pos);
		if (pos == std::string::npos)
			return "";
		pos = json.find('"', pos);
		if (pos == std::string::npos)
			return "";
		std::string value;
		for (size_t i = pos + 1; i < json.size() && json[i] != '"'; i++)
		{
			if (json[i] == '\\' && i + 1 < json.size())
				i++;
			value += json[i];
		}
		return value;
	}

	static std::string GetServerDbHost()
	{
		CHAR path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
		{
			size_t slash = root.find_last_of('\\');
			if (slash == std::string::npos)
				break;
			root = root.substr(0, slash);
		}
		std::string config = ReadFileText(root + "\\_local\\config.json");
		std::string host = JsonGetString(config, "serverdb_host");
		if (host.empty())
		{
			Log("No serverdb_host in %s\\_local\\config.json, using ws://localhost:777/serverdb", root.c_str());
			host = "ws://localhost:777/serverdb";
		}
		return host;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Network helpers
	// ------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The game broadcaster binds the first free UDP port from 6792 upward; find the one this process owns.
	/// </summary>
	static UINT16 GetBroadcasterPort()
	{
		DWORD size = 0;
		GetExtendedUdpTable(NULL, &size, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0);
		std::vector<BYTE> buf(size + 1024);
		size = (DWORD)buf.size();
		if (GetExtendedUdpTable(buf.data(), &size, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0) != NO_ERROR)
			return 0;
		MIB_UDPTABLE_OWNER_PID* table = (MIB_UDPTABLE_OWNER_PID*)buf.data();
		UINT16 best = 0;
		for (DWORD i = 0; i < table->dwNumEntries; i++)
		{
			UINT16 port = ntohs((u_short)table->table[i].dwLocalPort);
			if (table->table[i].dwOwningPid == GetCurrentProcessId() && port >= 6792 && port < 6892 && (best == 0 || port < best))
				best = port;
		}
		return best;
	}

	/// <summary>
	/// Determines the local IPv4 address used to reach the ServerDB host (network byte order).
	/// </summary>
	static UINT32 GetLocalAddressToward(const std::wstring& host, INTERNET_PORT port)
	{
		WSADATA wsa;
		WSAStartup(MAKEWORD(2, 2), &wsa);
		std::string hostA(host.begin(), host.end());
		addrinfo hints = {};
		hints.ai_family = AF_INET;
		hints.ai_socktype = SOCK_DGRAM;
		addrinfo* result = NULL;
		UINT32 address = htonl(INADDR_LOOPBACK);
		if (getaddrinfo(hostA.c_str(), std::to_string(port).c_str(), &hints, &result) == 0 && result != NULL)
		{
			SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
			if (s != INVALID_SOCKET)
			{
				if (connect(s, result->ai_addr, (int)result->ai_addrlen) == 0)
				{
					sockaddr_in local = {};
					int len = sizeof(local);
					if (getsockname(s, (sockaddr*)&local, &len) == 0)
						address = local.sin_addr.S_un.S_addr;
				}
				closesocket(s);
			}
			freeaddrinfo(result);
		}
		return address;
	}

	static std::wstring Widen(const std::string& s)
	{
		return std::wstring(s.begin(), s.end());
	}

	// ------------------------------------------------------------------------------------------------------------
	// ServerDB websocket (WinHTTP)
	// ------------------------------------------------------------------------------------------------------------
	static VOID CloseSocket()
	{
		g_connected = false;
		if (g_socket) { WinHttpCloseHandle(g_socket); g_socket = NULL; }
		if (g_request) { WinHttpCloseHandle(g_request); g_request = NULL; }
		if (g_connection) { WinHttpCloseHandle(g_connection); g_connection = NULL; }
		if (g_session) { WinHttpCloseHandle(g_session); g_session = NULL; }
	}

	static BOOL OpenSocket()
	{
		// WinHttpCrackUrl does not know ws:// or wss://, so map them to http(s) for parsing.
		std::wstring url = g_serverDbUrl;
		BOOL secure = FALSE;
		if (url.rfind(L"wss://", 0) == 0) { url = L"https://" + url.substr(6); secure = TRUE; }
		else if (url.rfind(L"ws://", 0) == 0) { url = L"http://" + url.substr(5); }

		URL_COMPONENTS parts = {};
		parts.dwStructSize = sizeof(parts);
		WCHAR host[256] = {}, path[2048] = {}, extra[2048] = {};
		parts.lpszHostName = host; parts.dwHostNameLength = 256;
		parts.lpszUrlPath = path; parts.dwUrlPathLength = 2048;
		parts.lpszExtraInfo = extra; parts.dwExtraInfoLength = 2048;
		if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts))
		{
			Log("Invalid serverdb_host URL (error %lu)", GetLastError());
			return FALSE;
		}
		std::wstring pathAndQuery = std::wstring(path) + extra;

		g_session = WinHttpOpen(L"EchoRelay.GameServer", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
		if (!g_session) return FALSE;
		g_connection = WinHttpConnect(g_session, host, parts.nPort, 0);
		if (!g_connection) return FALSE;
		g_request = WinHttpOpenRequest(g_connection, L"GET", pathAndQuery.c_str(), NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0);
		if (!g_request) return FALSE;
		if (!WinHttpSetOption(g_request, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, NULL, 0)) return FALSE;
		if (!WinHttpSendRequest(g_request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, NULL, 0, 0, 0)) return FALSE;
		if (!WinHttpReceiveResponse(g_request, NULL)) return FALSE;
		g_socket = WinHttpWebSocketCompleteUpgrade(g_request, 0);
		if (!g_socket) return FALSE;
		WinHttpCloseHandle(g_request);
		g_request = NULL;

		// Remember our local address toward the ServerDB host for registration.
		g_connected = true;
		return TRUE;
	}

	/// <summary>
	/// Sends one EchoRelay message (as its own websocket packet) to ServerDB.
	/// </summary>
	static BOOL SendMessageToServerDb(UINT64 symbol, const VOID* data, UINT64 size)
	{
		std::string packet;
		packet.append((const CHAR*)&PACKET_HEADER, 8);
		packet.append((const CHAR*)&symbol, 8);
		packet.append((const CHAR*)&size, 8);
		if (size > 0)
			packet.append((const CHAR*)data, (size_t)size);

		std::lock_guard<std::mutex> guard(g_sendLock);
		if (!g_connected || g_socket == NULL)
		{
			Log("Dropped message 0x%016llx: not connected to ServerDB", symbol);
			return FALSE;
		}
		DWORD error = WinHttpWebSocketSend(g_socket, WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE, (PVOID)packet.data(), (DWORD)packet.size());
		if (error != NO_ERROR)
		{
			Log("Failed to send message 0x%016llx to ServerDB (error %lu)", symbol, error);
			return FALSE;
		}
		return TRUE;
	}

	/// <summary>
	/// Splits a websocket packet into messages and queues them for the game thread.
	/// </summary>
	static VOID QueuePacket(const std::string& packet)
	{
		size_t offset = 0;
		while (offset + 24 <= packet.size())
		{
			UINT64 header, symbol, size;
			memcpy(&header, packet.data() + offset, 8);
			memcpy(&symbol, packet.data() + offset + 8, 8);
			memcpy(&size, packet.data() + offset + 16, 8);
			if (header != PACKET_HEADER || offset + 24 + size > packet.size())
			{
				Log("Received a malformed packet from ServerDB");
				return;
			}
			std::lock_guard<std::mutex> guard(g_queueLock);
			g_incoming.push_back({ symbol, packet.substr(offset + 24, (size_t)size) });
			offset += 24 + (size_t)size;
		}
	}

	/// <summary>
	/// Connection thread: connects to ServerDB, reconnecting as needed, and receives packets.
	/// </summary>
	static DWORD WINAPI ConnectionThread(LPVOID)
	{
		DWORD retryDelay = 1000;
		while (!g_stop)
		{
			if (!OpenSocket())
			{
				Log("Could not connect to ServerDB at %ls (error %lu), retrying", g_serverDbUrl.c_str(), GetLastError());
				CloseSocket();
				Sleep(retryDelay);
				retryDelay = min(retryDelay * 2, (DWORD)15000);
				continue;
			}
			retryDelay = 1000;
			Log("Connected to ServerDB at %ls", g_serverDbUrl.c_str());

			// (Re-)register on every connection; the game thread sends it.
			if (g_registrationRequested)
				g_needRegistration = true;

			std::string message;
			std::vector<BYTE> buffer(0x10000);
			while (!g_stop)
			{
				DWORD read = 0;
				WINHTTP_WEB_SOCKET_BUFFER_TYPE type;
				DWORD error = WinHttpWebSocketReceive(g_socket, buffer.data(), (DWORD)buffer.size(), &read, &type);
				if (error != NO_ERROR || type == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE)
				{
					Log("ServerDB connection closed (error %lu)", error);
					break;
				}
				message.append((const CHAR*)buffer.data(), read);
				if (type == WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE || type == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE)
				{
					QueuePacket(message);
					message.clear();
				}
			}
			{
				std::lock_guard<std::mutex> guard(g_sendLock);
				CloseSocket();
			}
			g_registered = false;
			if (!g_stop)
				Sleep(1000);
		}
		return 0;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Game side
	// ------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The engine's CSymbol64 hash (case-insensitive CRC-64 variant), also used for EchoRelay's message ids.
	/// </summary>
	static UINT64 Symbol64(const CHAR* name)
	{
		static UINT64 table[256];
		static bool initialized = false;
		const UINT64 polynomial = 0x95AC9329AC4BC9B5ULL;
		if (!initialized)
		{
			for (int i = 0; i < 256; i++)
			{
				UINT64 c = 0;
				for (int b = 7; b >= 0; b--)
					c = (c << 1) ^ (((i >> b) & 1) ? polynomial : 0);
				table[i] = c << 1;
			}
			initialized = true;
		}
		UINT64 hash = 0xFFFFFFFFFFFFFFFFULL;
		for (const unsigned char* p = (const unsigned char*)name; *p; p++)
		{
			unsigned char ch = (*p >= 'A' && *p <= 'Z') ? *p + 32 : *p;
			hash = ch ^ table[hash >> 56] ^ (hash << 8);
		}
		return hash;
	}

	/// <summary>
	/// Delivers a message to the game's broadcaster as a local event, as if it came from the official services.
	/// </summary>
	static VOID InjectEvent(const CHAR* name, const VOID* data, UINT64 size)
	{
		if (g_broadcaster == NULL)
		{
			Log("Cannot deliver %s: not initialized", name);
			return;
		}
		// Builds without a separate event hash use the message's CSymbol64 as its local event id.
		UINT64 id = EventSymbol != NULL ? EventSymbol(name) : Symbol64(name);
		Log("Delivering %s (%llu bytes)", name, size);
		ReceiveLocalEvent(g_broadcaster, id, name, data, size);
	}

	static VOID SendRegistration()
	{
		g_needRegistration = false;
		if (SendMessageToServerDb(SYM_REGISTRATION_REQUEST, g_registrationMessage.data(), g_registrationMessage.size()))
			Log("Requested game server registration");
	}

	/// <summary>
	/// Translates a ServerDB message into the summer build's lobby message and delivers it (game thread).
	/// </summary>
	static VOID HandleServerDbMessage(const IncomingMessage& message)
	{
		const std::string& p = message.payload;
		switch (message.symbol)
		{
		case SYM_REGISTRATION_SUCCESS:
		{
			// EchoRelay: u64 server id | u32 external ip | u64 unk. Summer: u64 server id | u32 | u8 | 3 pad.
			BYTE msg[16] = {};
			if (p.size() >= 8)
				memcpy(msg, p.data(), 8);
			g_registered = true;
			InjectEvent("SNSLobbyRegistrationSuccess", msg, sizeof(msg));
			break;
		}
		case SYM_REGISTRATION_FAILURE:
		{
			g_registered = false;
			Log("ServerDB rejected the game server registration");
			BYTE msg[8] = {};
			memcpy(msg, p.data(), min(p.size(), sizeof(msg)));
			InjectEvent("SNSLobbyRegistrationFailure", msg, sizeof(msg));
			break;
		}
		case SYM_START_SESSION:
		{
			// EchoRelay: guid session | guid channel | u8 player limit | u8 entrant count | u8 lobby type | u8 pad | settings json \0 | entrants
			// Summer SNSLobbyStartSessionv2: u64 entrant slots | guid session | u8 lobby type | pad to 0x20 | settings json \0
			if (p.size() < 37)
			{
				Log("Received a malformed start session message");
				break;
			}
			size_t jsonEnd = p.find('\0', 36);
			std::string json = p.substr(36, jsonEnd == std::string::npos ? std::string::npos : jsonEnd - 36);
			// Halloween 2017's has no lobby type: its session settings start right after the session guid.
			std::string msg(g_build->startSessionWithoutLobbyType ? 0x18 : 0x20, '\0');
			UINT64 slots = 16;
			memcpy(&msg[0], &slots, 8);
			memcpy(&msg[8], p.data(), 16);
			if (!g_build->startSessionWithoutLobbyType)
				msg[0x18] = p[34];
			msg += json;
			msg.push_back('\0');
			g_sessionActive = true;
			g_sessionLoadReported = false;
			g_inGame = false;
			Log("Starting session (lobby type %u): %s", (unsigned)(BYTE)p[34], json.c_str());
			InjectEvent("SNSLobbyStartSessionv2", msg.data(), msg.size());
			break;
		}
		case SYM_SESSION_SUCCESS_V4:
			// Tells the server the connecting client's packet encoder settings. Older builds take v3: v4 without the game type.
			if (g_build->sessionSuccessV3)
			{
				if (p.size() > 8)
					InjectEvent("SNSLobbySessionSuccessv3", p.data() + 8, p.size() - 8);
			}
			else
			{
				InjectEvent("SNSLobbySessionSuccessv4", p.data(), p.size());
			}
			break;
		case SYM_PLAYERS_ACCEPTED:
			// u8 + player session guids, identical in both builds.
			InjectEvent("SNSLobbyAcceptPlayersSuccessv2", p.data(), p.size());
			break;
		case SYM_PLAYERS_REJECTED:
			InjectEvent("SNSLobbyAcceptPlayersFailurev2", p.data(), p.size());
			break;
		default:
			// Final-build only messages (SessionSuccessv5, TcpConnectionUnrequireEvent, ...) are ignored.
			break;
		}
	}

	/// <summary>
	/// Reports to ServerDB that the session's level finished loading, so it can send waiting clients in.
	/// </summary>
	static VOID ReportSessionLoaded()
	{
		if (!g_sessionActive || g_sessionLoadReported || !g_inGame)
			return;
		BYTE unused = 0;
		if (SendMessageToServerDb(SYM_SESSION_STARTED, &unused, sizeof(unused)))
		{
			g_sessionLoadReported = true;
			Log("Session loaded");
		}
	}

	/// <summary>
	/// NetGame::SetState hook: tracks when a session's level is loaded (state 7, in game).
	/// </summary>
	static UINT64 SetStateHook(VOID* netgame, UINT64 state, UINT64 a3, UINT64 a4)
	{
		INT32 previous = *(INT32*)netgame;
		UINT64 result = SetStateTrampoline(netgame, state, a3, a4);
		INT32 current = *(INT32*)netgame;
		if (current != previous)
		{
			Log("NetGame state %d -> %d", previous, current);
			g_inGame = current == g_build->inGameState;
		}
		return result;
	}

	/// <summary>
	/// Installs the NetGame::SetState hook (relocating its 15-byte prologue into a trampoline).
	/// </summary>
	static VOID InstallSetStateHook()
	{
		const BYTE* prologue = g_build->setStatePrologue;
		const SIZE_T prologueSize = g_build->setStatePrologueSize;
		BYTE* target = (BYTE*)(g_base + g_build->netGameSetState);
		if (memcmp(target, prologue, prologueSize) != 0)
		{
			Log("NetGame::SetState prologue mismatch, session load reporting disabled (clients wait for the ServerDB timeout)");
			return;
		}

		// Trampoline: the original prologue, then jmp [rip] back to the rest of the function.
		BYTE* trampoline = (BYTE*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
		if (trampoline == NULL)
			return;
		memcpy(trampoline, prologue, prologueSize);
		BYTE jumpBack[14] = { 0xFF, 0x25, 0, 0, 0, 0 };
		UINT64 back = (UINT64)(target + prologueSize);
		memcpy(jumpBack + 6, &back, 8);
		memcpy(trampoline + prologueSize, jumpBack, sizeof(jumpBack));
		SetStateTrampoline = (SetStateFunc*)trampoline;

		// Patch the target: jmp [rip] to our hook (14 bytes), then nops over the rest of the prologue.
		BYTE patch[sizeof(g_build->setStatePrologue)];
		memset(patch, 0x90, sizeof(patch));
		patch[0] = 0xFF; patch[1] = 0x25; memset(patch + 2, 0, 4);
		UINT64 hook = (UINT64)&SetStateHook;
		memcpy(patch + 6, &hook, 8);
		DWORD oldProtect;
		if (VirtualProtect(target, prologueSize, PAGE_EXECUTE_READWRITE, &oldProtect))
		{
			memcpy(target, patch, prologueSize);
			VirtualProtect(target, prologueSize, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), target, prologueSize);
			Log("Hooked NetGame::SetState");
		}
	}

	// ------------------------------------------------------------------------------------------------------------
	// Summer IServerLib vtable methods
	// ------------------------------------------------------------------------------------------------------------

	static UINT64 Initialize(SummerServerLib* self, VOID* lobby, VOID* broadcaster, VOID* unk)
	{
		g_lobby = lobby;
		g_broadcaster = broadcaster;
		Log("Initialized game server (lobby=%p, broadcaster=%p)", lobby, broadcaster);
		InstallSetStateHook();

		// Connect to ServerDB in the background.
		g_serverDbUrl = Widen(GetServerDbHost());
		g_stop = false;
		if (g_thread == NULL)
			g_thread = CreateThread(NULL, 0, ConnectionThread, NULL, 0, NULL);
		return 1;
	}

	static VOID Terminate(SummerServerLib* self)
	{
		Log("Terminated game server");
		g_stop = true;
		{
			std::lock_guard<std::mutex> guard(g_sendLock);
			if (g_socket)
				WinHttpWebSocketClose(g_socket, WINHTTP_WEB_SOCKET_SUCCESS_CLOSE_STATUS, NULL, 0);
		}
		if (g_thread != NULL)
		{
			WaitForSingleObject(g_thread, 3000);
			CloseHandle(g_thread);
			g_thread = NULL;
		}
		std::lock_guard<std::mutex> guard(g_sendLock);
		CloseSocket();
	}

	static VOID Update(SummerServerLib* self, FLOAT dt)
	{
		// Send (re-)registration once connected.
		if (g_needRegistration && g_connected)
			SendRegistration();

		// Deliver queued ServerDB messages on the game thread.
		std::deque<IncomingMessage> messages;
		{
			std::lock_guard<std::mutex> guard(g_queueLock);
			messages.swap(g_incoming);
		}
		for (const IncomingMessage& message : messages)
			HandleServerDbMessage(message);

		ReportSessionLoaded();

		// A headless server has no vsync; a short sleep keeps it from pegging a CPU core.
		Sleep(3);
	}

	static VOID RequestRegistration(SummerServerLib* self, INT64 serverId, UINT64 regionId, UINT64 versionLock, VOID* localConfig)
	{
		// Determine our game server endpoint.
		URL_COMPONENTS parts = {};
		parts.dwStructSize = sizeof(parts);
		WCHAR host[256] = {};
		parts.lpszHostName = host; parts.dwHostNameLength = 256;
		std::wstring url = g_serverDbUrl;
		if (url.rfind(L"wss://", 0) == 0) url = L"https://" + url.substr(6);
		else if (url.rfind(L"ws://", 0) == 0) url = L"http://" + url.substr(5);
		WinHttpCrackUrl(url.c_str(), 0, 0, &parts);
		// Older builds pass no region, so the arguments are one place earlier.
		if (g_build->registrationWithoutRegion)
		{
			versionLock = regionId;
			regionId = 0;
		}
		UINT32 address = GetLocalAddressToward(host, parts.nPort);
		UINT16 port = GetBroadcasterPort();
		if (port == 0)
			port = 6792;

		// The rad14 builds (christmas and halloween 2017, lone echo) pass the same server id on every server, and EchoRelay keys game
		// servers by it, so a second server would replace the first. Give each server process its own id (kept for
		// re-registrations).
		if (g_build->executableTimestamp == 0x5A39494F || g_build->executableTimestamp == 0x59E8F804 || g_build->executableTimestamp == 0x59B81FFD || g_build->executableTimestamp == 0x5C9D6E49)
		{
			static UINT64 processId = 0;
			if (processId == 0)
			{
				LARGE_INTEGER counter;
				QueryPerformanceCounter(&counter);
				UINT64 z = ((UINT64)GetCurrentProcessId() << 32) ^ (UINT64)counter.QuadPart;
				z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
				z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
				processId = (z ^ (z >> 31)) | 1;
			}
			serverId ^= (INT64)processId;
		}

		// ERGameServerRegistrationRequest: u64 server id | u32 internal ip (network order) | u16 port | u16 pad | i64 region | i64 version lock
		std::string msg(32, '\0');
		memcpy(&msg[0], &serverId, 8);
		memcpy(&msg[8], &address, 4);
		memcpy(&msg[12], &port, 2);
		memcpy(&msg[16], &regionId, 8);
		memcpy(&msg[24], &versionLock, 8);
		g_registrationMessage = msg;
		g_registrationRequested = true;

		in_addr a;
		a.S_un.S_addr = address;
		Log("Registration requested: server id %lld, endpoint %s:%u, region 0x%016llx, version lock 0x%016llx", serverId, inet_ntoa(a), port, regionId, versionLock);
		if (g_connected)
			SendRegistration();
		else
			g_needRegistration = true;
	}

	static VOID Unregister(SummerServerLib* self)
	{
		Log("Unregistered game server");
		g_registrationRequested = false;
		g_registered = false;
		g_sessionActive = false;
	}

	static VOID EndSession(SummerServerLib* self)
	{
		if (g_sessionActive)
		{
			BYTE unused = 0;
			SendMessageToServerDb(SYM_END_SESSION, &unused, sizeof(unused));
		}
		g_sessionActive = false;
		g_sessionLoadReported = false;
		Log("Signaling end of session");
	}

	static VOID LockPlayerSessions(SummerServerLib* self)
	{
		if (g_sessionActive)
		{
			BYTE unused = 0;
			SendMessageToServerDb(SYM_PLAYER_SESSIONS_LOCKED, &unused, sizeof(unused));
		}
		Log("Signaling game server locked");
	}

	static VOID UnlockPlayerSessions(SummerServerLib* self)
	{
		if (g_sessionActive)
		{
			BYTE unused = 0;
			SendMessageToServerDb(SYM_PLAYER_SESSIONS_UNLOCKED, &unused, sizeof(unused));
		}
		Log("Signaling game server unlocked");
	}

	static VOID AcceptPlayerSessions(SummerServerLib* self, GuidArray* playerUuids)
	{
		if (playerUuids == NULL)
			return;
		if (g_sessionActive)
			SendMessageToServerDb(SYM_ACCEPT_PLAYERS, playerUuids->items, playerUuids->count * sizeof(GUID));
		Log("Accepted %llu players into game server", playerUuids->count);
	}

	static VOID RemovePlayerSession(SummerServerLib* self, GUID* playerUuid)
	{
		if (g_sessionActive && playerUuid != NULL)
			SendMessageToServerDb(SYM_REMOVE_PLAYER, playerUuid, sizeof(GUID));
		Log("Removed a player from game server");
	}

	static UINT64 UnknownMethod(SummerServerLib* self)
	{
		Log("Unknown IServerLib method called");
		return 0;
	}

	/// <summary>
	/// The summer IServerLib vtable (order taken from the summer build's CNSLobby call sites).
	/// </summary>
	static VOID* g_vtable[16] = {
		(VOID*)Initialize, (VOID*)Terminate, (VOID*)Update, (VOID*)RequestRegistration, (VOID*)Unregister,
		(VOID*)EndSession, (VOID*)LockPlayerSessions, (VOID*)UnlockPlayerSessions, (VOID*)AcceptPlayerSessions, (VOID*)RemovePlayerSession,
		(VOID*)UnknownMethod, (VOID*)UnknownMethod, (VOID*)UnknownMethod, (VOID*)UnknownMethod, (VOID*)UnknownMethod, (VOID*)UnknownMethod
	};

	VOID* GetServerLib()
	{
		if (g_lib == NULL)
		{
			g_build = DetectBuild();
			if (g_build == NULL)
				return NULL;
			ReceiveLocalEvent = (ReceiveLocalEventFunc*)(g_base + g_build->receiveLocalEvent);
			EventSymbol = g_build->eventSymbol != 0 ? (EventSymbolFunc*)(g_base + g_build->eventSymbol) : NULL;
			g_lib = new SummerServerLib();
			g_lib->vtbl = g_vtable;
			Log("%s build detected, providing the lobby ServerLib", g_build->name);
		}
		return g_lib;
	}

	VOID Shutdown()
	{
		delete g_lib;
		g_lib = NULL;
	}
}
