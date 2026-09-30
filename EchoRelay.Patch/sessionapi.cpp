#include "sessionapi.h"
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <initializer_list>
#include <deque>
#include <map>
#include <cmath>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace SessionApi
{
	// ------------------------------------------------------------------------------------------------------------
	// A minimal JSON document: numbers keep their source text, objects keep their key order.
	// ------------------------------------------------------------------------------------------------------------
	struct Value
	{
		enum Kind { Null, Bool, Number, String, Array, Object } kind = Null;
		bool boolean = false;
		std::string text; // a number's source text, or a string's (unescaped) value
		std::vector<Value> items;       // an array's items, or an object's values
		std::vector<std::string> keys;  // an object's keys (parallel to items)

		const Value* Get(const char* key) const
		{
			for (size_t i = 0; i < keys.size(); i++)
				if (keys[i] == key)
					return &items[i];
			return nullptr;
		}
		Value* Find(const char* key)
		{
			for (size_t i = 0; i < keys.size(); i++)
				if (keys[i] == key)
					return &items[i];
			return nullptr;
		}
		void Set(const std::string& key, Value value)
		{
			for (size_t i = 0; i < keys.size(); i++)
				if (keys[i] == key)
				{
					items[i] = std::move(value);
					return;
				}
			keys.push_back(key);
			items.push_back(std::move(value));
		}
	};

	static Value Num(const char* text) { Value v; v.kind = Value::Number; v.text = text; return v; }
	static Value Num(long long n) { return Num(std::to_string(n).c_str()); }
	static Value Str(const std::string& s) { Value v; v.kind = Value::String; v.text = s; return v; }
	static Value Bool(bool b) { Value v; v.kind = Value::Bool; v.boolean = b; return v; }
	static Value Obj() { Value v; v.kind = Value::Object; return v; }
	static Value Vec3(const char* x, const char* y, const char* z)
	{
		Value v; v.kind = Value::Array;
		v.items = { Num(x), Num(y), Num(z) };
		return v;
	}

	struct Parser
	{
		const char* p;
		const char* end;
		bool ok = true;

		void Space() { while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++; }
		bool Take(char c) { Space(); if (p < end && *p == c) { p++; return true; } return false; }

		std::string ParseString()
		{
			std::string out;
			if (!Take('"')) { ok = false; return out; }
			while (p < end && *p != '"')
			{
				if (*p == '\\' && p + 1 < end)
				{
					p++;
					switch (*p)
					{
					case 'n': out += '\n'; break;
					case 't': out += '\t'; break;
					case 'r': out += '\r'; break;
					case 'b': out += '\b'; break;
					case 'f': out += '\f'; break;
					case 'u':
						// keep \uXXXX escapes as written (re-emitted verbatim below)
						out += "\\u";
						break;
					default: out += *p; break;
					}
					p++;
				}
				else
					out += *p++;
			}
			if (p >= end) { ok = false; return out; }
			p++;
			return out;
		}

		Value ParseValue(int depth = 0)
		{
			Value v;
			if (depth > 64) { ok = false; return v; }
			Space();
			if (p >= end) { ok = false; return v; }
			if (*p == '{')
			{
				p++;
				v.kind = Value::Object;
				if (Take('}')) return v;
				do
				{
					Space();
					std::string key = ParseString();
					if (!ok || !Take(':')) { ok = false; return v; }
					v.keys.push_back(key);
					v.items.push_back(ParseValue(depth + 1));
					if (!ok) return v;
				} while (Take(','));
				if (!Take('}')) ok = false;
			}
			else if (*p == '[')
			{
				p++;
				v.kind = Value::Array;
				if (Take(']')) return v;
				do
				{
					v.items.push_back(ParseValue(depth + 1));
					if (!ok) return v;
				} while (Take(','));
				if (!Take(']')) ok = false;
			}
			else if (*p == '"')
			{
				v.kind = Value::String;
				v.text = ParseString();
			}
			else if (end - p >= 4 && strncmp(p, "true", 4) == 0) { v.kind = Value::Bool; v.boolean = true; p += 4; }
			else if (end - p >= 5 && strncmp(p, "false", 5) == 0) { v.kind = Value::Bool; p += 5; }
			else if (end - p >= 4 && strncmp(p, "null", 4) == 0) { p += 4; }
			else
			{
				const char* start = p;
				while (p < end && (strchr("+-.eE", *p) || (*p >= '0' && *p <= '9'))) p++;
				if (p == start) { ok = false; return v; }
				v.kind = Value::Number;
				v.text.assign(start, p);
			}
			return v;
		}
	};

	static void Write(const Value& v, std::string& out)
	{
		switch (v.kind)
		{
		case Value::Null: out += "null"; break;
		case Value::Bool: out += v.boolean ? "true" : "false"; break;
		case Value::Number: out += v.text; break;
		case Value::String:
			out += '"';
			for (size_t i = 0; i < v.text.size(); i++)
			{
				char c = v.text[i];
				if (c == '\\' && i + 1 < v.text.size() && v.text[i + 1] == 'u') { out += "\\u"; i++; }
				else if (c == '"' || c == '\\') { out += '\\'; out += c; }
				else if (c == '\n') out += "\\n";
				else if (c == '\r') out += "\\r";
				else if (c == '\t') out += "\\t";
				else if ((unsigned char)c < 0x20) { char buf[8]; sprintf_s(buf, "\\u%04x", c); out += buf; }
				else out += c;
			}
			out += '"';
			break;
		case Value::Array:
			out += '[';
			for (size_t i = 0; i < v.items.size(); i++) { if (i) out += ','; Write(v.items[i], out); }
			out += ']';
			break;
		case Value::Object:
			out += '{';
			for (size_t i = 0; i < v.keys.size(); i++)
			{
				if (i) out += ',';
				Write(Str(v.keys[i]), out);
				out += ':';
				Write(v.items[i], out);
			}
			out += '}';
			break;
		}
	}

	// ------------------------------------------------------------------------------------------------------------
	// The current level, from the client's own log ("[LEVELLOAD] Loading level '0x...'").
	// ------------------------------------------------------------------------------------------------------------
	struct LevelName { unsigned long long symbol; const char* map; const char* kind; };
	static const LevelName LEVELS[] = {
		{ 0x576ED3F8428EBC4Bull, "mpl_arena_a", "arena" },
		{ 0xD09AFD15B1C75C04ull, "mpl_lobby_b2", "lobby" },
		// the event lobbies are reported as the plain lobby, which is what tools look for
		{ 0xA9B30EF16760761Cull, "mpl_lobby_b2", "lobby" }, // mpl_lobby_b2_summer
		{ 0xA9B30EF465627817ull, "mpl_lobby_b2", "lobby" }, // mpl_lobby_b2_spooky
		{ 0x6DAA6D98D9354BA5ull, "mpl_lobby_b2", "lobby" }, // mpl_lobby_b2_xmas
		{ 0xDF5CA7B7DFA383D4ull, "mpl_combat_fission", "combat" },
		{ 0x42670F2BED45703Cull, "mpl_combat_combustion", "combat" },
		{ 0x43E2DA7914642604ull, "mpl_combat_dyson", "combat" },
		{ 0x43E2DA7A0C623A19ull, "mpl_combat_gauss", "combat" },
	};

	static std::mutex g_levelLock;
	static std::string g_logPath;
	static long long g_logOffset = 0;
	static const LevelName* g_level = nullptr;
	static std::string g_serverIp;   // the game server the client last connected to, from its log
	static int g_serverPort = 0;
	static std::string g_clientName;

	VOID SetClientName(const std::string& name)
	{
		std::lock_guard<std::mutex> lock(g_levelLock);
		g_clientName = name;
	}

	static std::string FindClientLog()
	{
		// <install>\_local\r14logs, two directories above bin\win7; the newest client log is ours
		char path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string dir = path;
		for (int i = 0; i < 3; i++)
			dir = dir.substr(0, dir.find_last_of('\\'));
		dir += "\\_local\\r14logs\\";
		WIN32_FIND_DATAA data;
		HANDLE find = FindFirstFileA((dir + "*client*.log").c_str(), &data);
		if (find == INVALID_HANDLE_VALUE)
			return "";
		std::string best;
		FILETIME bestTime = { 0, 0 };
		do
		{
			if (strstr(data.cFileName, "_json") != nullptr)
				continue;
			if (CompareFileTime(&data.ftLastWriteTime, &bestTime) > 0)
			{
				bestTime = data.ftLastWriteTime;
				best = dir + data.cFileName;
			}
		} while (FindNextFileA(find, &data));
		FindClose(find);
		return best;
	}

	static const LevelName* CurrentLevel()
	{
		std::lock_guard<std::mutex> lock(g_levelLock);
		if (g_logPath.empty())
		{
			g_logPath = FindClientLog();
			g_logOffset = 0;
			if (g_logPath.empty())
				return nullptr;
		}
		HANDLE file = CreateFileA(g_logPath.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
		if (file == INVALID_HANDLE_VALUE)
		{
			g_logPath.clear();
			return g_level;
		}
		LARGE_INTEGER size;
		if (GetFileSizeEx(file, &size) && size.QuadPart > g_logOffset)
		{
			// read what was appended since last time (whole lines only)
			long long toRead = size.QuadPart - g_logOffset;
			if (toRead > 4 * 1024 * 1024) { g_logOffset = size.QuadPart - 4 * 1024 * 1024; toRead = 4 * 1024 * 1024; }
			std::string buffer((size_t)toRead, '\0');
			LARGE_INTEGER at; at.QuadPart = g_logOffset;
			DWORD read = 0;
			if (SetFilePointerEx(file, at, NULL, FILE_BEGIN) && ReadFile(file, &buffer[0], (DWORD)toRead, &read, NULL))
			{
				buffer.resize(read);
				size_t lastNewline = buffer.find_last_of('\n');
				if (lastNewline != std::string::npos)
				{
					buffer.resize(lastNewline + 1);
					g_logOffset += buffer.size();
					const char* marker = "Loading level '0x";
					for (size_t pos = buffer.find(marker); pos != std::string::npos; pos = buffer.find(marker, pos + 1))
					{
						unsigned long long symbol = _strtoui64(buffer.c_str() + pos + strlen(marker), nullptr, 16);
						for (const LevelName& level : LEVELS)
							if (level.symbol == symbol)
								g_level = &level; // menus, globals and sub-levels are not maps: keep the last map
					}
					// "[NSLOBBY] connected to host peer [ip:port]" / "connected to internal host peer [ip:port]"
					for (const char* peerMarker : { "connected to host peer [", "connected to internal host peer [" })
						for (size_t pos = buffer.find(peerMarker); pos != std::string::npos; pos = buffer.find(peerMarker, pos + 1))
						{
							size_t start = pos + strlen(peerMarker), end = buffer.find(']', start);
							size_t colon = end != std::string::npos ? buffer.rfind(':', end) : std::string::npos;
							if (colon != std::string::npos && colon > start)
							{
								g_serverIp = buffer.substr(start, colon - start);
								g_serverPort = atoi(buffer.c_str() + colon + 1);
							}
						}
				}
			}
		}
		CloseHandle(file);
		return g_level;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Conversion to the later layout.
	// ------------------------------------------------------------------------------------------------------------
	static Value Or(const Value* v, Value fallback) { return v != nullptr ? *v : fallback; }

	/// <summary>
	/// Appends the members of 'from' that 'out' doesn't have (and that aren't in 'skip'): nothing the game sent is lost.
	/// </summary>
	static void KeepRest(const Value& from, Value& out, std::initializer_list<const char*> skip)
	{
		for (size_t i = 0; i < from.keys.size(); i++)
		{
			bool skipped = false;
			for (const char* k : skip)
				if (from.keys[i] == k)
					skipped = true;
			if (!skipped && out.Get(from.keys[i].c_str()) == nullptr)
				out.Set(from.keys[i], from.items[i]);
		}
	}

	static Value Stats(const Value* from)
	{
		static const char* KEYS[] = { "possession_time", "points", "saves", "goals", "stuns", "passes", "catches", "steals",
			"blocks", "interceptions", "assists", "shots_taken" };
		Value out = Obj();
		for (const char* key : KEYS)
			out.Set(key, Or(from ? from->Get(key) : nullptr, strcmp(key, "possession_time") == 0 ? Num("0.0") : Num("0")));
		return out;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Builds that send no player velocity (halloween): derived from the position's change between requests.
	// ------------------------------------------------------------------------------------------------------------
	struct Track { double pos[3]; double time; double vel[3]; };
	static std::mutex g_trackLock;
	static std::map<std::string, Track> g_tracks;

	static double Seconds()
	{
		static LARGE_INTEGER frequency = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
		LARGE_INTEGER now;
		QueryPerformanceCounter(&now);
		return (double)now.QuadPart / (double)frequency.QuadPart;
	}

	static Value Vec3d(const double v[3])
	{
		char buf[3][32];
		for (int i = 0; i < 3; i++)
			sprintf_s(buf[i], "%.6f", v[i]);
		return Vec3(buf[0], buf[1], buf[2]);
	}

	static Value DerivedVelocity(const Value& player)
	{
		const Value* position = player.Get("position");
		const Value* id = player.Get("userid");
		if (position == nullptr || position->kind != Value::Array || position->items.size() != 3)
			return Vec3("0.0", "0.0", "0.0");
		double pos[3];
		for (int i = 0; i < 3; i++)
			pos[i] = atof(position->items[i].text.c_str());
		std::string key = id != nullptr ? id->text : "";
		if (const Value* name = player.Get("name"))
			key += "/" + name->text;
		double now = Seconds();
		std::lock_guard<std::mutex> lock(g_trackLock);
		auto it = g_tracks.find(key);
		if (it == g_tracks.end())
		{
			Track t = { { pos[0], pos[1], pos[2] }, now, { 0, 0, 0 } };
			g_tracks[key] = t;
			return Vec3("0.0", "0.0", "0.0");
		}
		Track& t = it->second;
		double dt = now - t.time;
		if (dt >= 0.01)
		{
			if (dt < 1.0)
				for (int i = 0; i < 3; i++)
				{
					double v = (pos[i] - t.pos[i]) / dt;
					t.vel[i] = t.vel[i] * 0.3 + v * 0.7; // light smoothing against uneven polling
				}
			else
				t.vel[0] = t.vel[1] = t.vel[2] = 0; // a long gap (respawn, pause in polling): start over
			for (int i = 0; i < 3; i++)
				t.pos[i] = pos[i];
			t.time = now;
		}
		return Vec3d(t.vel);
	}

	static Value Transform(const Value* position, const Value& forward, const Value& left, const Value& up, const char* positionKey)
	{
		Value out = Obj();
		out.Set(positionKey, Or(position, Vec3("0.0", "0.0", "0.0")));
		out.Set("forward", forward);
		out.Set("left", left);
		out.Set("up", up);
		return out;
	}

	static Value Player(const Value& from)
	{
		Value forward = Or(from.Get("forward"), Vec3("0.0", "0.0", "1.0"));
		Value left = Or(from.Get("left"), Vec3("1.0", "0.0", "0.0"));
		Value up = Or(from.Get("up"), Vec3("0.0", "1.0", "0.0"));
		Value out = Obj();
		out.Set("name", Or(from.Get("name"), Str("")));
		out.Set("playerid", Or(from.Get("playerid"), Num("0")));
		out.Set("userid", Or(from.Get("userid"), Num("0")));
		out.Set("number", Or(from.Get("number"), Num("0")));
		out.Set("level", Or(from.Get("level"), Num("1")));
		out.Set("ping", Or(from.Get("ping"), Num("0")));
		out.Set("packetlossratio", Or(from.Get("packetlossratio"), Num("0.0")));
		out.Set("stunned", Or(from.Get("stunned"), Bool(false)));
		out.Set("invulnerable", Or(from.Get("invulnerable"), Bool(false)));
		out.Set("holding_left", Or(from.Get("holding_left"), Str("none")));
		out.Set("holding_right", Or(from.Get("holding_right"), Str("none")));
		out.Set("blocking", Or(from.Get("blocking"), Bool(false)));
		out.Set("is_emote_playing", Or(from.Get("is_emote_playing"), Bool(false)));
		out.Set("possession", Or(from.Get("possession"), Bool(false)));
		// later builds: head/body objects; these builds give one position and orientation for the player
		const Value* head = from.Get("head");
		out.Set("head", head && head->kind == Value::Object ? *head : Transform(from.Get("position"), forward, left, up, "position"));
		const Value* body = from.Get("body");
		out.Set("body", body && body->kind == Value::Object ? *body : Transform(from.Get("position"), forward, left, up, "position"));
		// later builds: hands are objects ({pos, forward, left, up}); these builds give their position only
		for (const char* hand : { "lhand", "rhand" })
		{
			const Value* h = from.Get(hand);
			out.Set(hand, h && h->kind == Value::Object ? *h : Transform(h, forward, left, up, "pos"));
		}
		const Value* velocity = from.Get("velocity");
		out.Set("velocity", velocity != nullptr ? *velocity : DerivedVelocity(from));
		out.Set("stats", Stats(from.Get("stats")));
		KeepRest(from, out, { "position", "forward", "left", "up" }); // the flat pose went into head/body
		return out;
	}

	static Value Team(const Value& from)
	{
		Value out = Obj();
		Value players; players.kind = Value::Array;
		if (const Value* list = from.Get("players"))
			for (const Value& p : list->items)
				if (p.kind == Value::Object)
					players.items.push_back(Player(p));
		out.Set("players", players);
		out.Set("team", Or(from.Get("team"), Str("")));
		out.Set("possession", Or(from.Get("possession"), Bool(false)));
		out.Set("stats", Stats(from.Get("stats")));
		KeepRest(from, out, {});
		return out;
	}

	// ------------------------------------------------------------------------------------------------------------
	// Scores counted from last_score, for builds that report it but not the points/goals it adds.
	// ------------------------------------------------------------------------------------------------------------
	static std::mutex g_scoreLock;
	static std::string g_scoreSession;       // the session the counts are for
	static std::string g_lastScoreSeen;      // last_score as last seen (a change is a new score)
	static std::string g_lastStatus;
	static long long g_teamPoints[2] = {};   // blue, orange
	static std::map<std::string, long long> g_playerGoals;

	static void RaiseTo(Value* stats, const char* key, long long value)
	{
		const Value* current = stats->Get(key);
		if (current == nullptr || _atoi64(current->text.c_str()) < value)
			stats->Set(key, Num(value));
	}

	static void TrackScores(const Value& in)
	{
		const Value* score = in.Get("last_score");
		const Value* session = in.Get("sessionid");
		const Value* status = in.Get("game_status");
		std::string sessionId = session ? session->text : "";
		std::string gameStatus = status ? status->text : "";
		std::string scoreText;
		if (score != nullptr)
			Write(*score, scoreText);
		std::lock_guard<std::mutex> lock(g_scoreLock);
		// a new session or a restarted match starts from zero; the score shown when counting starts isn't counted
		bool restarted = gameStatus == "pre_match" && g_lastStatus != "pre_match" && !g_lastStatus.empty();
		if (sessionId != g_scoreSession || restarted)
		{
			if (sessionId != g_scoreSession)
				g_lastScoreSeen = scoreText;
			g_scoreSession = sessionId;
			g_teamPoints[0] = g_teamPoints[1] = 0;
			g_playerGoals.clear();
		}
		g_lastStatus = gameStatus;
		if (score == nullptr || scoreText == g_lastScoreSeen)
			return;
		g_lastScoreSeen = scoreText;
		const Value* team = score->Get("team");
		const Value* points = score->Get("point_amount");
		const Value* type = score->Get("goal_type");
		const Value* scorer = score->Get("person_scored");
		long long amount = points ? _atoi64(points->text.c_str()) : 0;
		if (amount <= 0 || team == nullptr)
			return;
		g_teamPoints[team->text == "blue" ? 0 : 1] += amount;
		if (scorer != nullptr && scorer->text != "[INVALID]" && !(type != nullptr && type->text == "SELF GOAL"))
			g_playerGoals[scorer->text]++;
	}

	// ------------------------------------------------------------------------------------------------------------
	// last_score for builds that don't report it (halloween 2018): a team's points going up is a score, 2 points an
	// inside shot and 3 a long shot, by the player whose points went up, at the disc's top speed just before it.
	// ------------------------------------------------------------------------------------------------------------
	static std::deque<std::pair<double, double>> g_discSpeeds; // (time, speed) over the last seconds
	static std::string g_synthSession;
	static long long g_synthTeamPoints[2] = { -1, -1 };
	static std::map<std::string, long long> g_synthPlayerPoints;
	static bool g_haveSynthScore = false;
	static Value g_synthScore;

	static void NoteDiscSpeed(double speed)
	{
		std::lock_guard<std::mutex> lock(g_scoreLock);
		double now = Seconds();
		g_discSpeeds.emplace_back(now, speed);
		while (!g_discSpeeds.empty() && now - g_discSpeeds.front().first > 2.0)
			g_discSpeeds.pop_front();
	}

	static bool SynthesizeScore(const Value& teams, const Value& in, Value& out)
	{
		const Value* session = in.Get("sessionid");
		std::string sessionId = session ? session->text : "";
		std::lock_guard<std::mutex> lock(g_scoreLock);
		if (sessionId != g_synthSession)
		{
			g_synthSession = sessionId;
			g_synthTeamPoints[0] = g_synthTeamPoints[1] = -1;
			g_synthPlayerPoints.clear();
			g_haveSynthScore = false;
		}
		// every player's points now (to find the scorer), then each team's
		std::map<std::string, long long> playerPoints;
		std::map<std::string, int> playerTeam;
		for (size_t i = 0; i < teams.items.size(); i++)
			if (const Value* players = teams.items[i].Get("players"))
				for (const Value& p : players->items)
				{
					const Value* name = p.Get("name");
					const Value* stats = p.Get("stats");
					const Value* points = stats ? stats->Get("points") : nullptr;
					if (name != nullptr && points != nullptr)
					{
						playerPoints[name->text] = _atoi64(points->text.c_str());
						playerTeam[name->text] = (int)i;
					}
				}
		for (int t = 0; t < 2 && t < (int)teams.items.size(); t++)
		{
			const Value* stats = teams.items[t].Get("stats");
			const Value* points = stats ? stats->Get("points") : nullptr;
			long long now = points ? _atoi64(points->text.c_str()) : 0;
			long long before = g_synthTeamPoints[t];
			g_synthTeamPoints[t] = now;
			if (before < 0 || now <= before)
				continue;
			long long amount = now - before;
			std::string scorer = "[INVALID]";
			bool selfGoal = true;
			for (const auto& p : playerPoints)
			{
				auto prev = g_synthPlayerPoints.find(p.first);
				if (p.second > (prev != g_synthPlayerPoints.end() ? prev->second : 0))
				{
					scorer = p.first;
					selfGoal = playerTeam[p.first] != t;
				}
			}
			if (scorer == "[INVALID]")
				selfGoal = false;
			double speed = 0;
			for (const auto& s : g_discSpeeds)
				speed = max(speed, s.second);
			char speedText[32];
			snprintf(speedText, sizeof(speedText), "%.6f", speed);
			Value score = Obj();
			score.Set("disc_speed", Num(speedText));
			score.Set("team", Str(t == 0 ? "blue" : "orange"));
			score.Set("goal_type", Str(selfGoal ? "SELF GOAL" : amount >= 3 ? "LONG SHOT" : amount == 2 ? "INSIDE SHOT" : "[NO GOAL]"));
			score.Set("point_amount", Num(amount));
			score.Set("distance_thrown", Num("0.0"));
			score.Set("person_scored", Str(scorer));
			score.Set("assist_scored", Str("[INVALID]"));
			g_synthScore = score;
			g_haveSynthScore = true;
		}
		g_synthPlayerPoints = playerPoints;
		if (!g_haveSynthScore)
			return false;
		out = g_synthScore;
		return true;
	}

	static bool IsInvalid(const Value* v)
	{
		return v == nullptr || v->kind != Value::String || v->text.empty() || v->text.find("INVALID") != std::string::npos;
	}

	static bool Readable(const void* p, size_t n);
	static GameMemory g_memory = {};

	VOID SetGameMemory(const GameMemory& memory)
	{
		g_memory = memory;
	}

	// the last throw's values in the order the game keeps them (the F10 menu's)
	static const char* const LAST_THROW[] = { "arm_speed", "rot_per_sec", "pot_speed_from_rot", "total_speed",
		"speed_from_arm", "speed_from_wrist", "speed_from_movement", "off_axis_spin_deg", "wrist_align_to_throw_deg",
		"throw_align_to_movement_deg", "off_axis_penalty", "wrist_throw_penalty", "throw_move_penalty" };

	static UINT64 ReadPointer(UINT64 address)
	{
		return Readable((const void*)address, 8) ? *(const UINT64*)address : 0;
	}

	static std::string FloatText(float f)
	{
		if (!(f == f) || f > 1e9f || f < -1e9f)
			f = 0;
		char buf[32];
		snprintf(buf, sizeof(buf), "%.6g", f);
		std::string text = buf;
		if (text.find_first_of(".e") == std::string::npos)
			text += ".0";
		return text;
	}

	/// <summary>
	/// The disc's rigid body (position and velocity), or 0 if this build's layout isn't known or doesn't check out.
	/// </summary>
	static UINT64 DiscBody(const BYTE* netGame)
	{
		if (g_memory.gameState == 0 || netGame == nullptr)
			return 0;
		UINT64 image = (UINT64)GetModuleHandleA(NULL);
		UINT64 state = ReadPointer((UINT64)netGame + g_memory.gameState);
		UINT64 component = state ? ReadPointer(state + g_memory.discComponent) : 0;
		if (component == 0 || ReadPointer(component) != image + g_memory.discComponentVtable ||
			ReadPointer(component + 0x20) != g_memory.discComponentName)
			return 0;
		UINT64 owner = ReadPointer(component + g_memory.discBodyOwner);
		UINT64 body = owner ? ReadPointer(owner + g_memory.discBody) : 0;
		if (body == 0 || !Readable((const void*)(body + g_memory.discPosition), 12) ||
			!Readable((const void*)(body + g_memory.discVelocity), 12))
			return 0;
		return body;
	}

	static Value FloatVec3(UINT64 address)
	{
		const float* f = (const float*)address;
		double v[3];
		for (int i = 0; i < 3; i++)
			v[i] = (f[i] == f[i] && f[i] < 1e9f && f[i] > -1e9f) ? f[i] : 0.0;
		return Vec3d(v);
	}

	static Value Convert(const Value& in, const BYTE* netGame)
	{
		Value out = Obj();

		Value disc = Obj();
		const Value* inDisc = in.Get("disc");
		UINT64 body = inDisc ? 0 : DiscBody(netGame);
		disc.Set("position", body ? FloatVec3(body + g_memory.discPosition) :
			Or(inDisc ? inDisc->Get("position") : nullptr, Vec3("0.0", "0.0", "0.0")));
		disc.Set("forward", Or(inDisc ? inDisc->Get("forward") : nullptr, Vec3("0.0", "0.0", "1.0")));
		disc.Set("left", Or(inDisc ? inDisc->Get("left") : nullptr, Vec3("1.0", "0.0", "0.0")));
		disc.Set("up", Or(inDisc ? inDisc->Get("up") : nullptr, Vec3("0.0", "1.0", "0.0")));
		disc.Set("velocity", body ? FloatVec3(body + g_memory.discVelocity) :
			Or(inDisc ? inDisc->Get("velocity") : nullptr, Vec3("0.0", "0.0", "0.0")));
		disc.Set("bounce_count", Or(inDisc ? inDisc->Get("bounce_count") : nullptr, Num("0")));
		if (const Value* velocity = disc.Get("velocity"))
			if (velocity->items.size() == 3)
			{
				double v[3];
				for (int i = 0; i < 3; i++)
					v[i] = atof(velocity->items[i].text.c_str());
				NoteDiscSpeed(sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]));
			}
		out.Set("disc", disc);

		out.Set("sessionid", Or(in.Get("sessionid"), Str("")));
		out.Set("orange_team_restart_request", Or(in.Get("orange_team_restart_request"), Bool(false)));
		// the server's address: the game's own when it has one, else the host peer from the client's log
		CurrentLevel();
		std::string serverIp;
		{
			std::lock_guard<std::mutex> lock(g_levelLock);
			serverIp = g_serverIp;
		}
		out.Set("sessionip", Or(in.Get("sessionip"), Str(serverIp)));
		out.Set("game_status", Or(in.Get("game_status"), Str("")));
		out.Set("game_clock_display", Or(in.Get("game_clock_display"), Str("")));
		out.Set("game_clock", Or(in.Get("game_clock"), Num("0.0")));

		// the level: the game's own name when it has one, else from the log
		const Value* mapName = in.Get("map_name");
		const Value* matchType = in.Get("match_type");
		bool privateMatch = in.Get("private_match") && in.Get("private_match")->kind == Value::Bool && in.Get("private_match")->boolean;
		const LevelName* level = (IsInvalid(mapName) || IsInvalid(matchType)) ? CurrentLevel() : nullptr;
		std::string map = !IsInvalid(mapName) ? mapName->text : (level ? level->map : "INVALID LEVEL");
		std::string type = !IsInvalid(matchType) ? matchType->text : "INVALID GAMETYPE";
		if (IsInvalid(matchType) && level != nullptr)
		{
			if (strcmp(level->kind, "arena") == 0) type = privateMatch ? "Echo_Arena_Private" : "Echo_Arena";
			else if (strcmp(level->kind, "combat") == 0) type = privateMatch ? "Echo_Combat_Private" : "Echo_Combat";
			else type = "Social_2.0";
		}
		out.Set("match_type", Str(type));
		out.Set("map_name", Str(map));
		out.Set("private_match", Bool(privateMatch));

		// teams (blue, orange, spectators) and the points, from the teams' stats when the build doesn't send them
		Value teams; teams.kind = Value::Array;
		if (const Value* list = in.Get("teams"))
			for (const Value& t : list->items)
				if (t.kind == Value::Object)
					teams.items.push_back(Team(t));
		// later builds always send blue, orange and spectators; tools index teams[2] (these builds send two)
		static const char* TEAM_NAMES[] = { "BLUE TEAM", "ORANGE TEAM", "SPECTATORS" };
		while (teams.items.size() < 3)
		{
			Value empty = Obj();
			empty.Set("team", Str(TEAM_NAMES[teams.items.size()]));
			teams.items.push_back(Team(empty));
		}
		// Some builds (christmas 2018) report last_score but never team points or goals: count them from new scores.
		TrackScores(in);
		{
			std::lock_guard<std::mutex> lock(g_scoreLock);
			for (size_t i = 0; i < 2 && i < teams.items.size(); i++)
			{
				Value* stats = teams.items[i].Find("stats");
				if (stats == nullptr)
					continue;
				RaiseTo(stats, "points", g_teamPoints[i]);
				long long teamGoals = 0;
				if (Value* players = teams.items[i].Find("players"))
					for (Value& p : players->items)
					{
						const Value* name = p.Get("name");
						auto goals = name ? g_playerGoals.find(name->text) : g_playerGoals.end();
						if (goals != g_playerGoals.end())
						{
							if (Value* playerStats = p.Find("stats"))
							{
								RaiseTo(playerStats, "goals", goals->second);
								if (const Value* g = playerStats->Get("goals"))
									teamGoals += _atoi64(g->text.c_str());
							}
						}
					}
				RaiseTo(stats, "goals", teamGoals);
			}
		}
		auto teamPoints = [&](size_t i) -> Value {
			long long best = 0;
			if (i < teams.items.size())
				if (const Value* stats = teams.items[i].Get("stats"))
					if (const Value* points = stats->Get("points"))
						best = _atoi64(points->text.c_str());
			const Value* given = in.Get(i == 0 ? "blue_points" : "orange_points");
			if (given != nullptr && given->kind == Value::Number && _atoi64(given->text.c_str()) > best)
				return *given;
			return Num(best);
		};
		out.Set("orange_points", teamPoints(1));
		out.Set("total_round_count", Or(in.Get("total_round_count"), Num("1")));
		out.Set("blue_round_score", Or(in.Get("blue_round_score"), Num("0")));
		out.Set("orange_round_score", Or(in.Get("orange_round_score"), Num("0")));

		Value player = Obj();
		const Value* inPlayer = in.Get("player");
		player.Set("vr_left", Or(inPlayer ? inPlayer->Get("vr_left") : nullptr, Vec3("1.0", "0.0", "0.0")));
		player.Set("vr_position", Or(inPlayer ? inPlayer->Get("vr_position") : nullptr, Vec3("0.0", "0.0", "0.0")));
		player.Set("vr_forward", Or(inPlayer ? inPlayer->Get("vr_forward") : nullptr, Vec3("0.0", "0.0", "1.0")));
		player.Set("vr_up", Or(inPlayer ? inPlayer->Get("vr_up") : nullptr, Vec3("0.0", "1.0", "0.0")));
		out.Set("player", player);

		Value pause = Obj();
		const Value* inPause = in.Get("pause");
		pause.Set("paused_state", Or(inPause ? inPause->Get("paused_state") : nullptr, Str("unpaused")));
		pause.Set("unpaused_team", Or(inPause ? inPause->Get("unpaused_team") : nullptr, Str("none")));
		pause.Set("paused_requested_team", Or(inPause ? inPause->Get("paused_requested_team") : nullptr, Str("none")));
		pause.Set("unpaused_timer", Or(inPause ? inPause->Get("unpaused_timer") : nullptr, Num("0.0")));
		pause.Set("paused_timer", Or(inPause ? inPause->Get("paused_timer") : nullptr, Num("0.0")));
		out.Set("pause", pause);

		Value noPossession; noPossession.kind = Value::Array; noPossession.items = { Num("-1"), Num("-1") };
		out.Set("possession", Or(in.Get("possession"), noPossession));
		out.Set("tournament_match", Or(in.Get("tournament_match"), Bool(false)));
		out.Set("left_shoulder_pressed", Or(in.Get("left_shoulder_pressed"), Bool(false)));
		out.Set("right_shoulder_pressed", Or(in.Get("right_shoulder_pressed"), Bool(false)));
		out.Set("left_shoulder_pressed2", Or(in.Get("left_shoulder_pressed2"), Bool(false)));
		out.Set("right_shoulder_pressed2", Or(in.Get("right_shoulder_pressed2"), Bool(false)));
		out.Set("blue_team_restart_request", Or(in.Get("blue_team_restart_request"), Bool(false)));
		{
			std::lock_guard<std::mutex> lock(g_levelLock);
			out.Set("client_name", Or(in.Get("client_name"), Str(g_clientName)));
		}
		out.Set("blue_points", teamPoints(0));

		Value lastScore = Obj();
		const Value* inScore = in.Get("last_score");
		Value synthetic;
		if (inScore == nullptr && SynthesizeScore(teams, in, synthetic))
			inScore = &synthetic;
		lastScore.Set("disc_speed", Or(inScore ? inScore->Get("disc_speed") : nullptr, Num("0.0")));
		lastScore.Set("team", Or(inScore ? inScore->Get("team") : nullptr, Str("orange")));
		lastScore.Set("goal_type", Or(inScore ? inScore->Get("goal_type") : nullptr, Str("[NO GOAL]")));
		lastScore.Set("point_amount", Or(inScore ? inScore->Get("point_amount") : nullptr, Num("0")));
		lastScore.Set("distance_thrown", Or(inScore ? inScore->Get("distance_thrown") : nullptr, Num("0.0")));
		lastScore.Set("person_scored", Or(inScore ? inScore->Get("person_scored") : nullptr, Str("[INVALID]")));
		lastScore.Set("assist_scored", Or(inScore ? inScore->Get("assist_scored") : nullptr, Str("[INVALID]")));
		out.Set("last_score", lastScore);

		out.Set("teams", teams);

		Value lastThrow = Obj();
		const Value* inThrow = in.Get("last_throw");
		const float* throwValues = nullptr;
		UINT64 throwAddress = (UINT64)GetModuleHandleA(NULL) + g_memory.lastThrow;
		if (inThrow == nullptr && g_memory.lastThrow != 0 && Readable((const void*)throwAddress, sizeof(float) * 13))
			throwValues = (const float*)throwAddress;
		// in the layout's order
		for (const char* key : { "arm_speed", "total_speed", "off_axis_spin_deg", "wrist_throw_penalty", "rot_per_sec",
			"pot_speed_from_rot", "speed_from_arm", "speed_from_movement", "speed_from_wrist", "wrist_align_to_throw_deg",
			"throw_align_to_movement_deg", "off_axis_penalty", "throw_move_penalty" })
		{
			int index = -1;
			for (int i = 0; i < 13; i++)
				if (strcmp(LAST_THROW[i], key) == 0)
					index = i;
			lastThrow.Set(key, throwValues ? Num(FloatText(throwValues[index]).c_str()) :
				Or(inThrow ? inThrow->Get(key) : nullptr, Num("0.0")));
		}
		out.Set("last_throw", lastThrow);

		out.Set("rules_changed_by", Or(in.Get("rules_changed_by"), Str("[INVALID]")));
		out.Set("rules_changed_at", Or(in.Get("rules_changed_at"), Num("0")));
		out.Set("err_code", Or(in.Get("err_code"), Num("0")));

		// anything else the build sent that the layout above doesn't cover
		for (size_t i = 0; i < in.keys.size(); i++)
			if (out.Get(in.keys[i].c_str()) == nullptr)
				out.Set(in.keys[i], in.items[i]);
		return out;
	}

	/// <summary>
	/// Debugging: while <install>\_local\session_raw.enable exists, each raw /session body (as the game made it) is
	/// written to <install>\_local\session_raw.json.
	/// </summary>
	static void DumpRawIfEnabled(const char* data, size_t length)
	{
		static std::string localDir;
		static DWORD lastCheck = 0;
		static bool enabled = false;
		if (localDir.empty())
		{
			char path[MAX_PATH];
			GetModuleFileNameA(NULL, path, MAX_PATH);
			std::string dir = path;
			for (int i = 0; i < 3; i++)
				dir = dir.substr(0, dir.find_last_of('\\'));
			localDir = dir + "\\_local\\";
		}
		DWORD now = GetTickCount();
		if (now - lastCheck > 1000)
		{
			lastCheck = now;
			enabled = GetFileAttributesA((localDir + "session_raw.enable").c_str()) != INVALID_FILE_ATTRIBUTES;
		}
		if (!enabled)
			return;
		FILE* f = NULL;
		if (fopen_s(&f, (localDir + "session_raw.json").c_str(), "wb") == 0 && f != NULL)
		{
			fwrite(data, 1, length, f);
			fclose(f);
		}
	}

	/// <summary>
	/// Research: while <install>\_local\session_mem.enable exists, snapshots of the net game's pointer members' objects
	/// are appended to <install>\_local\session_mem.bin (at most every 50 ms): [double time][u64 net game][0x100 x (u64
	/// member, 0x200 bytes of what it points to, or zeros)], for locating game state the build doesn't report.
	/// </summary>
	static bool Readable(const void* p, size_t n)
	{
		MEMORY_BASIC_INFORMATION info;
		if (p == nullptr || VirtualQuery(p, &info, sizeof(info)) == 0 || info.State != MEM_COMMIT)
			return false;
		if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD))
			return false;
		if (!(info.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)))
			return false;
		return (const BYTE*)p + n <= (const BYTE*)info.BaseAddress + info.RegionSize;
	}

	static void CaptureIfEnabled(const BYTE* netGame)
	{
		static std::string localDir;
		static DWORD lastCheck = 0;
		static bool enabled = false;
		static double lastCapture = 0;
		if (localDir.empty())
		{
			char path[MAX_PATH];
			GetModuleFileNameA(NULL, path, MAX_PATH);
			std::string dir = path;
			for (int i = 0; i < 3; i++)
				dir = dir.substr(0, dir.find_last_of('\\'));
			localDir = dir + "\\_local\\";
		}
		DWORD now = GetTickCount();
		if (now - lastCheck > 1000)
		{
			lastCheck = now;
			enabled = GetFileAttributesA((localDir + "session_mem.enable").c_str()) != INVALID_FILE_ATTRIBUTES;
		}
		double t = Seconds();
		if (!enabled || netGame == nullptr || t - lastCapture < 0.05 || !Readable(netGame, 0x800))
			return;
		lastCapture = t;
		std::string record;
		record.append((const char*)&t, sizeof(t));
		UINT64 ng = (UINT64)netGame;
		record.append((const char*)&ng, sizeof(ng));
		static const char zeros[0x200] = { 0 };
		for (int i = 0; i < 0x100; i++)
		{
			UINT64 member = *(const UINT64*)(netGame + i * 8);
			record.append((const char*)&member, sizeof(member));
			if (Readable((const void*)member, 0x200))
				record.append((const char*)member, 0x200);
			else
				record.append(zeros, 0x200);
		}
		FILE* f = NULL;
		if (fopen_s(&f, (localDir + "session_mem.bin").c_str(), "ab") == 0 && f != NULL)
		{
			fwrite(record.data(), 1, record.size(), f);
			fclose(f);
		}
	}

	extern "C" const char* SessionApiConvert(const char* data, size_t len, size_t* outLen, const BYTE* netGame)
	{
		CaptureIfEnabled(netGame);
		static thread_local std::string result;
		if (data == nullptr || len == 0)
		{
			*outLen = 0;
			return data;
		}
		size_t length = len;
		while (length > 0 && data[length - 1] == '\0')
			length--;
		DumpRawIfEnabled(data, length);
		try
		{
			Parser parser{ data, data + length };
			Value in = parser.ParseValue();
			parser.Space();
			if (!parser.ok || in.kind != Value::Object || parser.p != parser.end)
			{
				*outLen = length;
				return data;
			}
			result.clear();
			Write(Convert(in, netGame), result);
			*outLen = result.size();
			return result.c_str();
		}
		catch (...)
		{
			*outLen = length;
			return data;
		}
	}
}
