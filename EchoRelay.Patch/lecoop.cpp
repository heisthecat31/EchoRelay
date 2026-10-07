#include "lecoop.h"
#include "lerender.h"
#include <cstring>
#include <detours.h>
#include <cmath>

namespace LeCoop
{
	// Static addresses in loneecho.exe (ImageBase 0x140000000); the code uses RVAs = VA - 0x140000000.
	static const DWORD GAME_VTABLE = 0xB8E4B8;         // NRadGame::CR14Game
	static const DWORD SLOT_UPDATE = 0x140;            // Update(game, phase); phase 1 runs the pause logic each frame
	static const DWORD SLOT_LOAD_LEVEL = 0x250;        // LoadLevel(game, desc, flags, float)
	static const DWORD SLOT_UNLOAD_LEVEL = 0x260;      // UnloadLevel(game, space, 0)
	static const DWORD FIND_SPACE = 0x2DDA70;          // FindGameSpace(game, levelHash)
	static const DWORD FIND_COMPONENTS = 0xAB380;      // GameComponentSystems(space, typeHash) -> { CS** items; ...; count @+0x28 }
	static const DWORD FIND_ENGINE_COMPONENT = 0xAB2A0; // EngineComponentSystem(space, typeHash) -> CS* (physics etc.)
	static const DWORD APPLY_NET_NAV_SETTINGS = 0x515BB0; // copies the net game's player-movement settings (NULL+0xA4 in single-player)
	static const DWORD REGISTER_NET_HANDLER = 0x2781C0; // RegisterMessageHandler(broker**, symbol, ...); broker is NULL without a net game
	static const DWORD NAV_HEAD = 0x510C50;            // PlayerNavHead(cs, TRS* out, index) (world)
	static const DWORD NAV_HAND = 0x510540;            // PlayerNavHand(cs, TRS* out, index, hand 0/1, 1) (world)
	static const DWORD NET_SETTINGS = 0x14C5538;       // BYTE*: the net game's settings; NULL in single-player
	static const DWORD SP_GLOBAL_LEVEL = 0x14A7088;    // UINT64: hash of r14_glb_global (Jack's level)
	static const DWORD MP_GLOBAL_LEVEL = 0x14A71B8;    // UINT64: hash of r14_glb_global_mp (set by a static initializer)
	static const DWORD LOAD_DESC_WORDS = 0x144BFE8;    // 8 bytes copied into the load descriptor at +0x14
	static const DWORD LOAD_DESC_DWORD = 0x144BFF0;    // 4 bytes copied into the load descriptor at +0x1c
	static const DWORD LOAD_FLOAT = 0xCF8E58;          // the float the game passes as LoadLevel's 4th argument

	static const UINT64 HASH_PLAYER_NAV = 0x559BE58A8EB1033CULL;     // R14PlayerNav
	static const UINT64 HASH_BODY = 0x2FC5DECB5C7B5393ULL;           // R14Body
	static const UINT64 HASH_REMOTE_PLAYER = 0x999D680B6B01B67EULL;  // R14RemotePlayer

	// Game offsets.
	static const DWORD GAME_FLAGS = 0x470;             // bit 12 = paused
	static const DWORD GAME_PLAYER_SPACE = 0x1B88;     // Jack's game space
	static const DWORD GAME_STATE = 0x1B9C;            // 3 while playing

	struct Trs { float rot[4]; float pos[3]; float scale[3]; BYTE pad[8]; };
	struct LoadDesc { UINT64 level; UINT64 minusOne; DWORD zero; BYTE words[8]; DWORD dword; };

	typedef UINT64(__fastcall* UpdateFn)(BYTE* game, UINT64 phase);
	typedef VOID(__fastcall* LoadLevelFn)(BYTE* game, LoadDesc* desc, INT flags, FLOAT f);
	typedef VOID(__fastcall* UnloadLevelFn)(BYTE* game, BYTE* space, INT zero);
	typedef BYTE* (__fastcall* FindSpaceFn)(BYTE* game, UINT64 levelHash);
	typedef BYTE* (__fastcall* FindComponentsFn)(BYTE* space, UINT64 typeHash);
	typedef BYTE* (__fastcall* FindEngineComponentFn)(BYTE* space, UINT64 typeHash);
	typedef UINT64(__fastcall* RegisterNetHandlerFn)(VOID* broker, UINT64 symbol, UINT64 a3, UINT64 a4, UINT64 a5);
	typedef Trs* (__fastcall* NavHeadFn)(BYTE* cs, Trs* out, UINT16 index);
	typedef Trs* (__fastcall* NavHandFn)(BYTE* cs, Trs* out, UINT16 index, INT hand, INT flag);

	static BYTE* g_exe = NULL;
	static VOID(*g_log)(const CHAR* format, ...) = NULL;
	static UpdateFn g_originalUpdate = NULL;
	static BYTE* g_game = NULL;
	static SHORT g_keyWasDown[256] = {};
	static RegisterNetHandlerFn g_originalRegisterNetHandler = NULL;
	typedef VOID(__fastcall* ApplyNavSettingsFn)(BYTE* settings);
	static ApplyNavSettingsFn g_originalApplyNavSettings = NULL;
	/// Stands in for the net game's settings while the multiplayer level is loaded in single-player: every field reads 0
	/// (the first qword is a flags word, the rest are numbers), so the net components' per-frame code doesn't crash.
	static __declspec(align(16)) BYTE g_standInSettings[0x1000] = {};

	/// Multiplayer-only functions that net components call on a net game sub-object that single-player never creates:
	/// with a NULL object (rcx) they return 0 instead of crashing. Each guard is its own function because Detours needs
	/// one detour per target.
	typedef UINT64(__fastcall* Fn4)(BYTE* object, UINT64 a2, UINT64 a3, UINT64 a4, UINT64 a5, UINT64 a6);
	template <int N> struct NullGuard
	{
		static Fn4 original;
		static const CHAR* name;
		static UINT64 __fastcall Detour(BYTE* object, UINT64 a2, UINT64 a3, UINT64 a4, UINT64 a5, UINT64 a6)
		{
			if ((UINT_PTR)object < 0x10000)
			{
				static LONG logged = 0;
				if (InterlockedIncrement(&logged) <= 5)
					g_log("[COOP] %s called without a net game object; returning 0", name);
				return 0;
			}
			return original(object, a2, a3, a4, a5, a6);
		}
	};
	template <int N> Fn4 NullGuard<N>::original = NULL;
	template <int N> const CHAR* NullGuard<N>::name = NULL;

	struct NullGuardSite { DWORD rva; const CHAR* name; Fn4* original; PVOID detour; const CHAR** nameSlot; };
	#define NULL_GUARD(n, rva, label) { rva, label, &NullGuard<n>::original, (PVOID)NullGuard<n>::Detour, &NullGuard<n>::name }
	static NullGuardSite NULL_GUARDS[] = {
		NULL_GUARD(0, 0x3AEA70, "net user lookup 0x1403aea70 (net game +0x3e8)"),
		NULL_GUARD(1, 0x3AEBD0, "net user lookup 0x1403aebd0 (net game +0x3e8)"),
		NULL_GUARD(2, 0x3AEC90, "net user lookup 0x1403aec90 (net game +0x3e8)"),
		// Methods of the net message broker (0x1403bac10 returns it; NULL without a net game).
		NULL_GUARD(3, 0x27F470, "net broker 0x14027f470"),
	};


	// ---- Puppet: the second player from r14_glb_global_mp, driven by poses we supply instead of this headset. ----
	// The player's per-frame code (nav and body jobs, the hand accessors) reads the headset and controllers through a
	// few getters. While that code runs for a component system of the puppet's game space, a per-thread flag is set and
	// the getters return the puppet's poses (for now: this player's, 1.5 m to the side) and no buttons pressed.
	static BYTE* volatile g_puppetSpace = NULL;
	static __declspec(thread) INT t_puppetDepth = 0;
	static FLOAT g_puppetOffset[3] = { 0.0f, 0.0f, 0.0f };     // tracking space; the body offset is applied to the root


	static BOOL IsPuppetSystem(BYTE* cs)
	{
		BYTE* space = g_puppetSpace;
		if (space == NULL || (UINT_PTR)cs < 0x10000)
			return FALSE;
		__try { return *(BYTE**)(cs + 0x80) == space; }
		__except (EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
	}

	typedef UINT64(__fastcall* Fn6)(BYTE* a1, UINT64 a2, UINT64 a3, UINT64 a4, UINT64 a5, UINT64 a6);
	template <int N> struct PuppetScope
	{
		static Fn6 original;
		static UINT64 __fastcall Detour(BYTE* a1, UINT64 a2, UINT64 a3, UINT64 a4, UINT64 a5, UINT64 a6)
		{
			if (!IsPuppetSystem(a1))
				return original(a1, a2, a3, a4, a5, a6);
			// The puppet's nav update and nav jobs would move it with its own climbing and grabbing (it grabbed with the
			// delayed hands and dragged itself around); its nav state is written each frame instead (WritePuppetNav).
			if (N <= 4)
				return 0;
			t_puppetDepth++;
			UINT64 result = original(a1, a2, a3, a4, a5, a6);
			t_puppetDepth--;
			return result;
		}
	};
	template <int N> Fn6 PuppetScope<N>::original = NULL;
	struct HookSite { DWORD rva; PVOID* original; PVOID detour; };
	#define PUPPET_SCOPE(n, rva) { rva, (PVOID*)&PuppetScope<n>::original, (PVOID)PuppetScope<n>::Detour }
	static HookSite PUPPET_SCOPES[] = {
		PUPPET_SCOPE(0, 0x518A50),  // CR14PlayerNavCS update (vtable +0x1d0)
		PUPPET_SCOPE(1, 0x51B000),  // nav jobs registered by the player init (0x1405368c0)
		PUPPET_SCOPE(2, 0x51BF40),  //   (writes the head pose)
		PUPPET_SCOPE(3, 0x51C300),
		PUPPET_SCOPE(4, 0x51D0B0),
		PUPPET_SCOPE(5, 0x564190),  // body jobs
		PUPPET_SCOPE(6, 0x568C90),
		PUPPET_SCOPE(7, 0x6B9E60),
		PUPPET_SCOPE(8, 0x6BA040),
		PUPPET_SCOPE(9, 0x6BD210),
		PUPPET_SCOPE(10, 0x6B69E0),
		PUPPET_SCOPE(11, 0x5105A0), // left / right hand world transform (cs, out, index, 1)
		PUPPET_SCOPE(12, 0x5108F0),
	};

	/// Every per-frame job of every component system goes through this task trampoline: data+8 points at the job entry,
	/// whose first field is the component system. Flag all of the puppet space's jobs (its grab, touch and hand systems
	/// read the controllers too), not just the nav and body jobs above.
	typedef UINT64(__fastcall* TaskFn)(BYTE* task, BYTE* data, UINT64 a3, UINT64 a4);
	static TaskFn g_originalRunJob = NULL;
	static UINT64 __fastcall ScopedRunJob(BYTE* task, BYTE* data, UINT64 a3, UINT64 a4)
	{
		BYTE* cs = NULL;
		__try { cs = **(BYTE***)(data + 8); }
		__except (EXCEPTION_EXECUTE_HANDLER) { cs = NULL; }
		if (!IsPuppetSystem(cs))
			return g_originalRunJob(task, data, a3, a4);
		t_puppetDepth++;
		UINT64 result = g_originalRunJob(task, data, a3, a4);
		t_puppetDepth--;
		return result;
	}

	// Buttons, triggers and grips: leaf getters that return a float.
	typedef FLOAT(__fastcall* FloatFn)(UINT64 a1, UINT64 a2);
	template <int N> struct NoButtons
	{
		static FloatFn original;
		static FLOAT __fastcall Detour(UINT64 a1, UINT64 a2) { return t_puppetDepth > 0 ? 0.0f : original(a1, a2); }
	};
	template <int N> FloatFn NoButtons<N>::original = NULL;
	#define NO_BUTTONS(n, rva) { rva, (PVOID*)&NoButtons<n>::original, (PVOID)NoButtons<n>::Detour }
	static HookSite BUTTON_GETTERS[] = {
		NO_BUTTONS(0, 0x2638F0), NO_BUTTONS(1, 0x263950), NO_BUTTONS(2, 0x263970), NO_BUTTONS(3, 0x263990),
		NO_BUTTONS(4, 0x2639B0), NO_BUTTONS(5, 0x2639D0), NO_BUTTONS(6, 0x2639F0), NO_BUTTONS(7, 0x263A10),
		NO_BUTTONS(8, 0x263A30), NO_BUTTONS(9, 0x264280), NO_BUTTONS(10, 0x2642A0), NO_BUTTONS(11, 0x2642C0),
		NO_BUTTONS(12, 0x2642E0), NO_BUTTONS(13, 0x2642F0), NO_BUTTONS(14, 0x2644A0), NO_BUTTONS(15, 0x2644B0),
		NO_BUTTONS(16, 0x2644D0), NO_BUTTONS(17, 0x2644F0), NO_BUTTONS(18, 0x264510), NO_BUTTONS(19, 0x264520),
		NO_BUTTONS(20, 0x2650E0), NO_BUTTONS(21, 0x265100), NO_BUTTONS(22, 0x265120), NO_BUTTONS(23, 0x265130),
		NO_BUTTONS(24, 0x265290), NO_BUTTONS(25, 0x2652A0), NO_BUTTONS(26, 0x2652C0), NO_BUTTONS(27, 0x2652E0),
		NO_BUTTONS(28, 0x265300), NO_BUTTONS(29, 0x265310),
	};

	// Poses (tracking space): out-parameter getters. For the puppet they return this player's poses from
	// PUPPET_DELAY seconds ago (a ring buffer filled once a frame), positions shifted by g_puppetOffset. This stands in
	// for the other player's poses until they come over the network.
	typedef FLOAT* (__fastcall* PoseFn)(FLOAT* out, UINT64 flag);
	enum PoseSlot { HEAD_POS, HEAD_ROT, LEFT_POS, LEFT_ROT, RIGHT_POS, RIGHT_ROT, POSE_SLOTS };
	// Player nav fields copied per sample: +0x410 player-to-world TRS, +0x440/+0x450 head (tracking space),
	// +0x470 tracking-to-player TRS.
	static const DWORD NAV_BLOCK = 0x410, NAV_BLOCK_SIZE = 0x90;
	struct PoseSample { LONGLONG time; FLOAT v[POSE_SLOTS][4]; BYTE nav[NAV_BLOCK_SIZE]; BOOL hasNav; };
	static const DOUBLE PUPPET_DELAY = 2.0;
	static PoseSample g_poseRing[1024] = {};
	static volatile LONG g_poseCount = 0;
	static PoseFn g_realPose[POSE_SLOTS] = {};

	static const PoseSample* DelayedSample()
	{
		LONG count = g_poseCount;
		if (count == 0)
			return NULL;
		LARGE_INTEGER now, freq;
		QueryPerformanceCounter(&now);
		QueryPerformanceFrequency(&freq);
		LONGLONG wanted = now.QuadPart - (LONGLONG)(PUPPET_DELAY * freq.QuadPart);
		LONG oldest = count > ARRAYSIZE(g_poseRing) ? count - (LONG)ARRAYSIZE(g_poseRing) : 0;
		for (LONG i = count - 1; i >= oldest; i--)
		{
			const PoseSample& sample = g_poseRing[i % ARRAYSIZE(g_poseRing)];
			if (sample.time <= wanted)
				return &sample;
		}
		return &g_poseRing[oldest % ARRAYSIZE(g_poseRing)];
	}

	/// The puppet is a full local player, camera included: its camera and this player's took turns rendering (every other
	/// frame showed r14_glb_global_mp's black void). Bit 22 of the player nav's dword at +0x314 is "camera disabled"
	/// (R14PlayerCameraEnabledExpression, 0x14050ef50); set it on every player in the puppet's space.
	static VOID DisablePuppetCamera(BYTE* space)
	{
		BYTE* nav = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
		if (nav == NULL)
			return;
		UINT16 count = *(UINT16*)(nav + 0xE4);
		BYTE* data = *(BYTE**)(nav + 0xE8);
		for (UINT16 i = 0; i < count; i++)
		{
			DWORD* flags = (DWORD*)(data + i * 0x988 + 0x314);
			if ((*flags & (1u << 22)) == 0)
			{
				*flags |= 1u << 22;
				g_log("[COOP] disabled the puppet's camera (player %u)", i);
			}
		}
	}

	/// The puppet can't die: its placement is written each frame, so the first jump from its spawn point to next to this
	/// player read as an impact at high velocity, it died, respawned at its spawn point and died again every 4 seconds,
	/// and each death ran the game's death handling for this player too. Kill(navCS, index, cause, source, ...).
	typedef UINT64(__fastcall* KillFn)(BYTE* cs, UINT64 index, UINT64 cause, UINT64 source, UINT64 a5, UINT64 a6, UINT64 a7, UINT64 a8);
	static KillFn g_originalKill = NULL;
	static UINT64 __fastcall GuardedKill(BYTE* cs, UINT64 index, UINT64 cause, UINT64 source, UINT64 a5, UINT64 a6, UINT64 a7, UINT64 a8)
	{
		if (IsPuppetSystem(cs))
		{
			static LONG logged = 0;
			if (InterlockedIncrement(&logged) <= 3)
				g_log("[COOP] the puppet would have died (cause %llu, source %llu); ignored", (unsigned long long)cause,
					(unsigned long long)source);
			return 0;
		}
		return g_originalKill(cs, index, cause, source, a5, a6, a7, a8);
	}

	/// Once a frame: the puppet's nav state (placement, head, tracking space) is this player's from PUPPET_DELAY ago,
	/// turned to face this player (g_pivot). Its nav update doesn't run (PuppetScope), so nothing else moves it.
	static const PoseSample* DelayedSample();

	static VOID QuatMul(const FLOAT* a, const FLOAT* b, FLOAT* out)
	{
		FLOAT r[4] = {
			a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
			a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
			a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
			a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2] };
		memcpy(out, r, sizeof(r));
	}
	static VOID QuatRotate(const FLOAT* q, const FLOAT* v, FLOAT* out)
	{
		FLOAT t[3] = { 2 * (q[1] * v[2] - q[2] * v[1]), 2 * (q[2] * v[0] - q[0] * v[2]), 2 * (q[0] * v[1] - q[1] * v[0]) };
		FLOAT r[3] = {
			v[0] + q[3] * t[0] + q[1] * t[2] - q[2] * t[1],
			v[1] + q[3] * t[1] + q[2] * t[0] - q[0] * t[2],
			v[2] + q[3] * t[2] + q[0] * t[1] - q[1] * t[0] };
		memcpy(out, r, sizeof(r));
	}
	/// TRS composition as the game does it (0x1408f5000): child expressed in parent's space.
	static VOID Compose(const FLOAT* child, const FLOAT* parent, FLOAT* out)
	{
		FLOAT rotated[3], rotation[4];
		QuatRotate(parent, child + 4, rotated);
		QuatMul(parent, child, rotation);
		FLOAT position[3];
		for (int i = 0; i < 3; i++)
			position[i] = rotated[i] * parent[7 + i] + parent[4 + i];
		memcpy(out, rotation, sizeof(rotation));
		memcpy(out + 4, position, sizeof(position));
		for (int i = 0; i < 3; i++)
			out[7 + i] = child[7 + i] * parent[7 + i];
	}

	// The puppet's movement is this player's, turned 180 degrees about a vertical axis through g_pivot, so it stands about
	// 1 m in front of where this player was standing (when F7 was pressed, or when it appeared) and faces them.
	static FLOAT g_pivot[3] = {};
	static BOOL g_hasPivot = FALSE;
	static FLOAT g_forwardSign = 1.0f;

	/// This player's head, world space, from a recorded nav block.
	static VOID HeadWorld(const BYTE* block, FLOAT* position, FLOAT* forward)
	{
		FLOAT local[10] = {};
		memcpy(local, block + 0x30, 4 * sizeof(FLOAT));       // +0x440 rotation
		memcpy(local + 4, block + 0x40, 3 * sizeof(FLOAT));   // +0x450 position
		local[7] = local[8] = local[9] = 1.0f;
		FLOAT player[10], world[10];
		Compose(local, (const FLOAT*)(block + 0x60), player);  // +0x470 tracking -> player
		Compose(player, (const FLOAT*)(block + 0x00), world);  // +0x410 player -> world
		memcpy(position, world + 4, 3 * sizeof(FLOAT));
		FLOAT ahead[3] = { 0, 0, g_forwardSign };
		QuatRotate(world, ahead, forward);
	}

	static VOID PlacePivot(const BYTE* block)
	{
		FLOAT head[3], forward[3];
		HeadWorld(block, head, forward);
		FLOAT length = sqrtf(forward[0] * forward[0] + forward[2] * forward[2]);
		if (length < 0.01f)
			length = 1.0f;
		// Halfway to where the puppet's head should be: turning 180 degrees about here puts it 1 m ahead, facing back.
		g_pivot[0] = head[0] + 0.5f * forward[0] / length;
		g_pivot[1] = head[1];
		g_pivot[2] = head[2] + 0.5f * forward[2] / length;
		g_hasPivot = TRUE;
		g_log("[COOP] puppet placed: head (%.2f %.2f %.2f), facing (%.2f %.2f %.2f), pivot (%.2f %.2f %.2f)", head[0], head[1],
			head[2], forward[0], forward[1], forward[2], g_pivot[0], g_pivot[1], g_pivot[2]);
	}

	static VOID WritePuppetNav(BYTE* space)
	{
		const PoseSample* sample = DelayedSample();
		if (sample == NULL || !sample->hasNav)
			return;
		BYTE* nav = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
		if (nav == NULL || *(UINT16*)(nav + 0xE4) == 0)
			return;
		if (!g_hasPivot)
		{
			const PoseSample& latest = g_poseRing[(g_poseCount - 1) % ARRAYSIZE(g_poseRing)];
			PlacePivot(latest.nav);
		}
		BYTE* entry = *(BYTE**)(nav + 0xE8);
		// Only the placement, the head and the tracking transform: the fields between (+0x45c..+0x46f) are per-hand grab
		// state, which made the puppet grab what this player grabbed.
		memcpy(entry + 0x410, sample->nav + 0x00, 0x4C);  // +0x410 TRS, +0x440 head rotation, +0x450 head position
		memcpy(entry + 0x470, sample->nav + 0x60, 0x30);  // +0x470 TRS
		// Turn the placement 180 degrees about the vertical axis through the pivot.
		static const FLOAT HALF_TURN[4] = { 0.0f, 1.0f, 0.0f, 0.0f };
		FLOAT* rotation = (FLOAT*)(entry + 0x410);
		FLOAT* position = (FLOAT*)(entry + 0x420);
		QuatMul(HALF_TURN, rotation, rotation);
		position[0] = 2 * g_pivot[0] - position[0];
		position[2] = 2 * g_pivot[2] - position[2];
	}

	/// Once a frame, outside any puppet scope: record this player's real poses.
	static VOID RecordPose()
	{
		PoseSample& sample = g_poseRing[g_poseCount % ARRAYSIZE(g_poseRing)];
		for (int slot = 0; slot < POSE_SLOTS; slot++)
		{
			FLOAT out[4] = { 0, 0, 0, 1 };
			if (g_realPose[slot] != NULL)
				g_realPose[slot](out, 1);
			memcpy(sample.v[slot], out, sizeof(out));
		}
		sample.hasNav = FALSE;
		if (g_game != NULL)
		{
			BYTE* space = ((FindSpaceFn)(g_exe + FIND_SPACE))(g_game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
			BYTE* nav = space == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
			if (nav != NULL && *(UINT16*)(nav + 0xE4) > 0)
			{
				memcpy(sample.nav, *(BYTE**)(nav + 0xE8) + NAV_BLOCK, NAV_BLOCK_SIZE);
				sample.hasNav = TRUE;
			}
		}
		LARGE_INTEGER now;
		QueryPerformanceCounter(&now);
		sample.time = now.QuadPart;
		InterlockedIncrement(&g_poseCount);
	}

	template <int SLOT> struct PuppetPose
	{
		static FLOAT* __fastcall Detour(FLOAT* out, UINT64 flag)
		{
			FLOAT* result = g_realPose[SLOT](out, flag);
			if (t_puppetDepth > 0)
			{
				const PoseSample* sample = DelayedSample();
				BOOL position = (SLOT % 2) == 0;
				if (sample != NULL)
					memcpy(out, sample->v[SLOT], position ? 3 * sizeof(FLOAT) : 4 * sizeof(FLOAT));
				if (position)
					for (int i = 0; i < 3; i++)
						out[i] += g_puppetOffset[i];
			}
			return result;
		}
	};
	#define PUPPET_POSE(slot, rva) { rva, (PVOID*)&g_realPose[slot], (PVOID)PuppetPose<slot>::Detour }
	static HookSite POSE_GETTERS[] = {
		PUPPET_POSE(HEAD_POS, 0x2647C0),
		PUPPET_POSE(HEAD_ROT, 0x264740),
		PUPPET_POSE(LEFT_POS, 0x264400),
		PUPPET_POSE(LEFT_ROT, 0x264390),
		PUPPET_POSE(RIGHT_POS, 0x265200),
		PUPPET_POSE(RIGHT_ROT, 0x265190),
	};

	/// Hooks each site in its own transaction, so one site Detours can't patch doesn't take the others down.
	static LONG AttachAll(BYTE* exe, HookSite* sites, SIZE_T count, const CHAR* what)
	{
		LONG failed = 0;
		for (SIZE_T i = 0; i < count; i++)
		{
			*sites[i].original = exe + sites[i].rva;
			DetourTransactionBegin();
			DetourUpdateThread(GetCurrentThread());
			LONG error = DetourAttach(sites[i].original, sites[i].detour);
			if (error == NO_ERROR)
				error = DetourTransactionCommit();
			else
				DetourTransactionAbort();
			if (error != NO_ERROR)
			{
				failed++;
				g_log("[COOP] couldn't hook %s at loneecho.exe+0x%lX (error %ld)", what, sites[i].rva, error);
			}
		}
		g_log("[COOP] %s: %u of %u hooked", what, (UINT)(count - failed), (UINT)count);
		return failed;
	}

	/// The net game's deferred-call queue (net game +0x470) is never given a buffer in single-player (capacity 0 at
	/// +0x1d0), and net script nodes from r14_glb_global_mp queue calls on it: the first one was fatal ("CDeferredMethodQueue
	/// buffer is out of space (size = 0)"). With no capacity, hand back a scratch slot: the call is dropped.
	typedef BYTE* (__fastcall* QueueAllocFn)(BYTE* queue, UINT64 a2);
	static QueueAllocFn g_originalNetQueueAlloc = NULL;
	static __declspec(align(16)) BYTE g_droppedCall[0x200] = {};
	static BYTE* __fastcall GuardedNetQueueAlloc(BYTE* queue, UINT64 a2)
	{
		if (*(INT64*)(queue + 0x1D0) == 0)
		{
			static LONG logged = 0;
			if (InterlockedIncrement(&logged) <= 3)
				g_log("[COOP] dropped a deferred call on the net game's queue (no net game)");
			return g_droppedCall;
		}
		return g_originalNetQueueAlloc(queue, a2);
	}

	/// The broker's handler lookup (0x140278a50): with no broker it does what its own early-out does, writing the
	/// "no handler" value to *out and returning out.
	typedef UINT64* (__fastcall* BrokerLookupFn)(BYTE* broker, UINT64* out, UINT64 a3, UINT64 a4);
	static BrokerLookupFn g_originalBrokerLookup = NULL;
	static UINT64* __fastcall GuardedBrokerLookup(BYTE* broker, UINT64* out, UINT64 a3, UINT64 a4)
	{
		if ((UINT_PTR)broker < 0x10000)
		{
			*out = *(UINT64*)(g_exe + 0x143AC30);
			return out;
		}
		return g_originalBrokerLookup(broker, out, a3, a4);
	}

	/// R14Net components apply the net game's movement settings (net settings + 0xA4) to the player nav when they start;
	/// in single-player the settings object is NULL. Keep the single-player values.
	static VOID __fastcall GuardedApplyNavSettings(BYTE* settings)
	{
		if ((UINT_PTR)settings < 0x10000 || (settings >= g_standInSettings && settings < g_standInSettings + sizeof(g_standInSettings)))
		{
			g_log("[COOP] skipped applying net game movement settings (no net game)");
			return;
		}
		g_originalApplyNavSettings(settings);
	}
	static LONG g_skippedNetHandlers = 0;

	/// Network components (R14NetPhysics, R14RemotePlayer...) register message handlers with the net game's broker when they
	/// start. In single-player there is no net game, so the broker is NULL and the game crashed (loading r14_glb_global_mp).
	static UINT64 __fastcall GuardedRegisterNetHandler(VOID* broker, UINT64 symbol, UINT64 a3, UINT64 a4, UINT64 a5)
	{
		if (broker == NULL)
		{
			if (InterlockedIncrement(&g_skippedNetHandlers) <= 20)
				g_log("[COOP] skipped a net message handler registration (no net game), symbol 0x%llX", (unsigned long long)symbol);
			return 0;
		}
		return g_originalRegisterNetHandler(broker, symbol, a3, a4, a5);
	}

	static BOOL Pressed(int key)
	{
		BOOL down = (GetAsyncKeyState(key) & 0x8000) != 0;
		BOOL edge = down && !g_keyWasDown[key];
		g_keyWasDown[key] = (SHORT)down;
		return edge;
	}

	static VOID** Vtable(BYTE* object) { return *(VOID***)object; }

	/// The first component system of a type in a game space, or NULL.
	static BYTE* ComponentSystem(BYTE* space, UINT64 typeHash, UINT64* listCount)
	{
		BYTE* list = ((FindComponentsFn)(g_exe + FIND_COMPONENTS))(space, typeHash);
		*listCount = list == NULL ? 0 : *(UINT64*)(list + 0x28);
		if (*listCount == 0)
			return NULL;
		return (*(BYTE***)list)[0];
	}

	static UINT16 ComponentCount(BYTE* cs) { return cs == NULL ? 0 : *(UINT16*)(cs + 0xE4); }

	static VOID LogPlayer(BYTE* game)
	{
		UINT64 flags = *(UINT64*)(game + GAME_FLAGS);
		g_log("[COOP] game %p state %d paused %d flags 0x%llX", game, *(INT*)(game + GAME_STATE), (INT)((flags >> 12) & 1),
			(unsigned long long)flags);
		BYTE* playerSpace = *(BYTE**)(game + GAME_PLAYER_SPACE);
		BYTE* globalSpace = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		g_log("[COOP] player space %p, r14_glb_global space %p", playerSpace, globalSpace);
		BYTE* space = globalSpace != NULL ? globalSpace : playerSpace;
		if (space == NULL)
			return;
		UINT64 lists = 0;
		BYTE* nav = ComponentSystem(space, HASH_PLAYER_NAV, &lists);
		BYTE* engineNav = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
		g_log("[COOP] engine-list lookup of R14PlayerNav: %p (%u players)", engineNav, ComponentCount(engineNav));
		if (nav == NULL)
			nav = engineNav;
		g_log("[COOP] player space %p, player nav systems %llu, first %p with %u players", space, (unsigned long long)lists, nav,
			ComponentCount(nav));
		if (nav == NULL || ComponentCount(nav) == 0)
			return;
		Trs head = {}, left = {}, right = {};
		((NavHeadFn)(g_exe + NAV_HEAD))(nav, &head, 0);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &left, 0, 0, 1);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &right, 0, 1, 1);
		g_log("[COOP] head (%.2f %.2f %.2f) left hand (%.2f %.2f %.2f) right hand (%.2f %.2f %.2f)",
			head.pos[0], head.pos[1], head.pos[2], left.pos[0], left.pos[1], left.pos[2], right.pos[0], right.pos[1], right.pos[2]);
	}

	static UINT64 MpLevelHash() { return *(UINT64*)(g_exe + MP_GLOBAL_LEVEL); }

	// The player's grab code (nav job 0x14051b000) looks bodies up in "the" physics scene: g_pEngine (0x1414a73b0)
	// +0xeec8 -> scene handle at +0x98 (24 bytes). If loading r14_glb_global_mp replaces it, this player's grabs look in
	// the wrong scene (grabbing stopped working, and a later grab crashed at 0x14051b971). Keep the story's handle.
	static BYTE g_storyScene[24] = {};
	static BOOL g_haveStoryScene = FALSE;
	static BYTE* PhysicsSceneHandle()
	{
		BYTE* engine = *(BYTE**)(g_exe + 0x14A73B0);
		BYTE* world = engine == NULL ? NULL : *(BYTE**)(engine + 0xEEC8);
		return world == NULL ? NULL : world + 0x98;
	}
	static VOID KeepStoryScene()
	{
		BYTE* handle = PhysicsSceneHandle();
		if (!g_haveStoryScene || handle == NULL || memcmp(handle, g_storyScene, sizeof(g_storyScene)) == 0)
			return;
		static LONG logged = 0;
		if (InterlockedIncrement(&logged) <= 3)
			g_log("[COOP] the physics scene handle changed (%016llX -> %016llX); putting the story's back",
				*(unsigned long long*)g_storyScene, *(unsigned long long*)handle);
		memcpy(handle, g_storyScene, sizeof(g_storyScene));
	}

	// r14_glb_global_mp brings its own environment, collision included, at the story level's position: its (invisible)
	// surfaces caught this player's grabs. Move the whole level away with its root's SetOffset (level root = space+0x60,
	// virtual +0x80, as the level loader does at 0x140991fdd).
	static const FLOAT MP_LEVEL_OFFSET[3] = { 5000.0f, 0.0f, 5000.0f };
	static BOOL g_mpLevelMoved = FALSE;
	static VOID MoveMpLevel(BYTE* space)
	{
		typedef VOID(__fastcall* SetOffsetFn)(BYTE* root, const FLOAT* position);
		BYTE* root = *(BYTE**)(space + 0x60);
		if (root == NULL)
			return;
		((SetOffsetFn)(*(VOID***)root)[0x80 / 8])(root, MP_LEVEL_OFFSET);
		g_mpLevelMoved = TRUE;
		g_log("[COOP] moved r14_glb_global_mp to (%.0f, %.0f, %.0f)", MP_LEVEL_OFFSET[0], MP_LEVEL_OFFSET[1], MP_LEVEL_OFFSET[2]);
	}

	// r14_glb_global_mp has its own environment, collision included (its bodies join the story's physics scene: 695 ->
	// 1211), at the same origin: this player's capsule got wedged in it (grabs held, but climbing didn't move them).
	// The level loader places each level with CLevel::SetOffset (0x1402ab570, called at 0x140991fdd while loading);
	// the load F9 starts gets MP_LEVEL_OFFSET added there, so the whole level, collision included, is built far away.
	typedef VOID(__fastcall* LevelSetOffsetFn)(BYTE* level, const FLOAT* offset);
	static LevelSetOffsetFn g_originalLevelSetOffset = NULL;
	static volatile LONG g_offsetNextLevel = 0;
	static VOID __fastcall OffsetMpLevel(BYTE* level, const FLOAT* offset)
	{
		if (InterlockedExchange(&g_offsetNextLevel, 0) == 0)
		{
			g_originalLevelSetOffset(level, offset);
			return;
		}
		FLOAT moved[3];
		for (int i = 0; i < 3; i++)
			moved[i] = offset[i] + MP_LEVEL_OFFSET[i];
		g_log("[COOP] building r14_glb_global_mp at (%.0f, %.0f, %.0f) instead of (%.1f, %.1f, %.1f)", moved[0], moved[1], moved[2],
			offset[0], offset[1], offset[2]);
		g_originalLevelSetOffset(level, moved);
	}

	// Events (grab, touch, press...) are posted to every game space in the world (0x1402d5160), so r14_glb_global_mp's
	// own hand/grab/touch systems reacted to this player's grabs too, with their (switched off) player at the origin:
	// grabbed objects and this player's arm were pulled to (0, 0, 0) and climbing stopped working. Freeze that space:
	// its per-frame jobs only run for the systems a remote player needs (body, model, animation, transform, remote
	// player). Every job goes through the task trampoline 0x1402d6a80 (job entry at data+8; its first field is the
	// component system, whose +0x80 is its space). Decided per system class (RTTI name), cached per vtable.
	static BYTE* volatile g_frozenSpace = NULL;
	static const CHAR* ClassName(BYTE* vtable)
	{
		__try
		{
			BYTE* col = *(BYTE**)(vtable - 8);
			DWORD typeRva = *(DWORD*)(col + 12);
			return (const CHAR*)(g_exe + typeRva + 16);
		}
		__except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
	}
	static BOOL AllowedInFrozenSpace(BYTE* vtable)
	{
		static BYTE* known[512]; static BOOL allowed[512]; static LONG count = 0;
		for (LONG i = 0; i < count; i++)
			if (known[i] == vtable)
				return allowed[i];
		const CHAR* name = ClassName(vtable);
		static const CHAR* KEEP[] = { "RemotePlayer", "Body", "Model", "Anim", "Transform", "Skin", "Skeleton", "Joint" };
		BOOL keep = FALSE;
		for (const CHAR* k : KEEP)
			if (name != NULL && strstr(name, k) != NULL)
				keep = TRUE;
		if (count < ARRAYSIZE(known))
		{
			known[count] = vtable; allowed[count] = keep; count++;
			g_log("[COOP] frozen MP space: %s %s", keep ? "running" : "stopped", name != NULL ? name : "(unknown class)");
		}
		return keep;
	}
	typedef UINT64(__fastcall* RunJobFn)(BYTE* task, BYTE* data, UINT64 a3, UINT64 a4);
	static RunJobFn g_originalJob = NULL;
	// While a robot is taken over (F7): in its space, skip the LOD systems (they switched it off when this player left
	// its zone, so it vanished) and the character animation (it kept playing its work loop). Whole systems, so other
	// characters in that level freeze too; per-actor control comes later.
	static BYTE* volatile g_takeOverSpace = NULL;
	static BOOL StoppedForTakeOver(BYTE* vtable)
	{
		static BYTE* known[256]; static BOOL stop[256]; static LONG count = 0;
		for (LONG i = 0; i < count; i++)
			if (known[i] == vtable)
				return stop[i];
		const CHAR* name = ClassName(vtable);
		// Character animation keeps running: it also updates the skinned model's bounds, and with it stopped the robot was
		// culled by its stale bounds (it vanished depending on where this player looked).
		BOOL s = name != NULL && (strstr(name, "CComponentLODCS@") != NULL || strstr(name, "CActorLODCS@") != NULL);
		if (count < ARRAYSIZE(known))
		{
			known[count] = vtable; stop[count] = s; count++;
			if (s)
				g_log("[COOP] take over: stopping %s in the robot's level", name);
		}
		return s;
	}

	static UINT64 __fastcall FreezeMpJobs(BYTE* task, BYTE* data, UINT64 a3, UINT64 a4)
	{
		BYTE* takeOver = g_takeOverSpace;
		if (takeOver != NULL)
		{
			BYTE* cs = NULL;
			__try { cs = **(BYTE***)(data + 8); if (*(BYTE**)(cs + 0x80) != takeOver) cs = NULL; }
			__except (EXCEPTION_EXECUTE_HANDLER) { cs = NULL; }
			if (cs != NULL && StoppedForTakeOver(*(BYTE**)cs))
				return 0;
		}
		BYTE* frozen = g_frozenSpace;
		if (frozen != NULL)
		{
			BYTE* cs = NULL;
			__try { cs = **(BYTE***)(data + 8); if (*(BYTE**)(cs + 0x80) != frozen) cs = NULL; }
			__except (EXCEPTION_EXECUTE_HANDLER) { cs = NULL; }
			if (cs != NULL && !AllowedInFrozenSpace(*(BYTE**)cs))
				return 0;
		}
		return g_originalJob(task, data, a3, a4);
	}

	/// The single-player guards for r14_glb_global_mp's net components, installed only when it is loaded (F9), so a
	/// -coop session is unchanged until then.
	static BOOL g_guardsInstalled = FALSE;
	static VOID InstallGuards()
	{
		if (g_guardsInstalled)
			return;
		g_guardsInstalled = TRUE;
		BYTE* exe = g_exe;
		VOID(*log)(const CHAR*, ...) = g_log;
		if (g_originalJob == NULL)
		{
			HookSite freeze = { 0x2D6A80, (PVOID*)&g_originalJob, (PVOID)FreezeMpJobs };
			AttachAll(exe, &freeze, 1, "freeze the MP level's jobs");
		}
		g_originalRegisterNetHandler = (RegisterNetHandlerFn)(exe + REGISTER_NET_HANDLER);
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach((PVOID*)&g_originalRegisterNetHandler, (PVOID)GuardedRegisterNetHandler);
		g_originalApplyNavSettings = (ApplyNavSettingsFn)(exe + APPLY_NET_NAV_SETTINGS);
		DetourAttach((PVOID*)&g_originalApplyNavSettings, (PVOID)GuardedApplyNavSettings);
		g_originalBrokerLookup = (BrokerLookupFn)(exe + 0x278A50);
		DetourAttach((PVOID*)&g_originalBrokerLookup, (PVOID)GuardedBrokerLookup);
		g_originalNetQueueAlloc = (QueueAllocFn)(exe + 0x76EEF0);
		DetourAttach((PVOID*)&g_originalNetQueueAlloc, (PVOID)GuardedNetQueueAlloc);
		for (NullGuardSite& site : NULL_GUARDS)
		{
			*site.original = (Fn4)(exe + site.rva);
			*site.nameSlot = site.name;
			DetourAttach((PVOID*)site.original, site.detour);
		}
		LONG error = DetourTransactionCommit();
		log("[COOP] single-player guards for net components: %s", error == NO_ERROR ? "installed" : "FAILED");
		// The input scoping (puppet scopes, button and pose getters, the job trampoline) is no longer installed: it used a
		// per-thread flag, and the game runs its jobs on fibers (CreateFiber/SwitchToFiber), so the flag could leak into
		// this player's own jobs (suspected cause of grabbing breaking). A spawned RemotePlayer doesn't read input.
		HookSite kill = { 0x5154D0, (PVOID*)&g_originalKill, (PVOID)GuardedKill };
		AttachAll(exe, &kill, 1, "puppet can't die");
	}

	static VOID LoadMpLevel(BYTE* game)
	{
		InstallGuards();
		BYTE* scene = PhysicsSceneHandle();
		if (scene != NULL)
		{
			memcpy(g_storyScene, scene, sizeof(g_storyScene));
			g_haveStoryScene = TRUE;
			g_log("[COOP] story physics scene handle %016llX %016llX", *(unsigned long long*)scene, *(unsigned long long*)(scene + 8));
		}
		LoadDesc desc = {};
		desc.level = MpLevelHash();
		desc.minusOne = ~0ULL;
		memcpy(desc.words, g_exe + LOAD_DESC_WORDS, 8);
		desc.dword = *(DWORD*)(g_exe + LOAD_DESC_DWORD);
		BYTE** settings = (BYTE**)(g_exe + NET_SETTINGS);
		if (*settings == NULL)
		{
			*settings = g_standInSettings;
			g_log("[COOP] no net game: net components get zeroed stand-in settings");
		}
		g_log("[COOP] loading r14_glb_global_mp (0x%llX)", (unsigned long long)desc.level);

		((LoadLevelFn)Vtable(game)[SLOT_LOAD_LEVEL / 8])(game, &desc, 1, *(FLOAT*)(g_exe + LOAD_FLOAT));
	}

	static BYTE* MpSpace(BYTE* game) { return ((FindSpaceFn)(g_exe + FIND_SPACE))(game, MpLevelHash()); }

	static VOID ListMpLevel(BYTE* game)
	{
		BYTE* space = MpSpace(game);
		g_log("[COOP] r14_glb_global_mp space: %p", space);
		if (space == NULL)
			return;
		static const struct { const CHAR* name; UINT64 hash; } TYPES[] = {
			{ "R14RemotePlayer", HASH_REMOTE_PLAYER }, { "R14Body", HASH_BODY }, { "R14PlayerNav", HASH_PLAYER_NAV } };
		for (const auto& type : TYPES)
		{
			UINT64 lists = 0;
			BYTE* cs = ComponentSystem(space, type.hash, &lists);
			BYTE* engineCs = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, type.hash);
			g_log("[COOP]   %s: %llu systems, first %p with %u components; engine list %p with %u", type.name,
				(unsigned long long)lists, cs, ComponentCount(cs), engineCs, ComponentCount(engineCs));
		}
	}

	// Echo Arena adds a remote user by activating an instance of the "RemotePlayer" actor pool in the space at
	// netgame+0x450 (0x1403b91e0 -> SpawnFromPool 0x1402c8f90(space, &handle, poolHash)). Actors are switched with
	// EnableActor 0x1402cc550(space, handle) / DisableActor 0x1402cba40(space, handle) (EnableActorNode).
	typedef BYTE* (__fastcall* SpawnFromPoolFn)(BYTE* space, BYTE* handle, UINT64 poolHash);
	typedef VOID(__fastcall* ActorFn)(BYTE* space, UINT64 handle);
	typedef BYTE* (__fastcall* ComponentActorFn)(BYTE* cs, BYTE* out, UINT16 index);
	static BYTE g_remoteHandle[0x20] = {};
	static BOOL g_mpLocalPlayerDisabled = FALSE;

	static VOID SpawnRemotePlayer(BYTE* game)
	{
		BYTE* space = MpSpace(game);
		if (space == NULL)
		{
			g_log("[COOP] load the MP player level first (F9)");
			return;
		}
		UINT64 pool = *(UINT64*)(g_exe + 0xBE0260);  // hash of "RemotePlayer"
		memset(g_remoteHandle, 0xFF, sizeof(g_remoteHandle));
		((SpawnFromPoolFn)(g_exe + 0x2C8F90))(space, g_remoteHandle, pool);
		BYTE* remote = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_REMOTE_PLAYER);
		g_log("[COOP] spawned from the RemotePlayer pool (0x%llX): handle %016llX %016llX; R14RemotePlayer components now %u",
			(unsigned long long)pool, *(unsigned long long*)g_remoteHandle, *(unsigned long long*)(g_remoteHandle + 8),
			ComponentCount(remote));
	}

	/// r14_glb_global_mp's collision (its bodies join the story's physics scene, at the same place) wedged this player's
	/// capsule: grabs held but climbing didn't move them. Switch off every actor in that space that owns a physics
	/// component (the level's own environment and props). Done once, after the level has loaded.
	static BOOL g_mpCollisionDisabled = FALSE;
	static VOID DisableMpCollision(BYTE* game)
	{
		BYTE* space = MpSpace(game);
		BYTE* physics = space == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, 0x5B8CC538E22AD937ULL);
		UINT16 count = ComponentCount(physics);
		if (count == 0)
			return;
		static UINT64 handles[4096];
		UINT16 n = 0;
		for (UINT16 i = 0; i < count && n < ARRAYSIZE(handles); i++)
		{
			BYTE out[0x20] = {};
			BYTE* actor = ((ComponentActorFn)(*(VOID***)physics)[0x170 / 8])(physics, out, i);
			UINT64 handle = actor == NULL ? ~0ULL : *(UINT64*)actor;
			BOOL seen = FALSE;
			for (UINT16 j = 0; j < n && !seen; j++)
				seen = handles[j] == handle;
			if (!seen && (UINT16)handle != 0xFFFF)
				handles[n++] = handle;
		}
		for (UINT16 i = 0; i < n; i++)
			((ActorFn)(g_exe + 0x2CBA40))(space, handles[i]);
		g_mpCollisionDisabled = TRUE;
		g_log("[COOP] switched off %u actors with physics in r14_glb_global_mp (%u physics components before); now %u",
			n, count, ComponentCount(physics));
	}

	/// The level's static collision belongs to its level graph (it isn't made of physics components: the space's
	/// physics system is empty), so move the whole level root far away, once, after it has loaded.
	static BOOL g_mpRootMoved = FALSE;
	static VOID MoveMpLevelRoot(BYTE* game)
	{
		BYTE* space = MpSpace(game);
		if (space == NULL)
			return;
		g_mpRootMoved = TRUE;
		// The level's static collision belongs to its level graph, not to actors: move the level's root far away too.
		// The level object (a CModel) was found 0x38780 into the space, with the level hash at +0xca0 and the CLevel
		// root at +0x60 (whose virtual +0x80, CLevel::SetOffset 0x1402ab570, adds to the root's position).
		BYTE* levelObject = space + 0x38780;
		BOOL valid = FALSE;
		__try { valid = *(UINT64*)(levelObject + 0xCA0) == MpLevelHash() && *(BYTE**)(levelObject + 0x60) != NULL; }
		__except (EXCEPTION_EXECUTE_HANDLER) { valid = FALSE; }
		if (valid)
		{
			BYTE* root = *(BYTE**)(levelObject + 0x60);
			((LevelSetOffsetFn)(*(VOID***)root)[0x80 / 8])(root, MP_LEVEL_OFFSET);
			g_log("[COOP] moved r14_glb_global_mp's level root %p by (%.0f, %.0f, %.0f)", root, MP_LEVEL_OFFSET[0],
				MP_LEVEL_OFFSET[1], MP_LEVEL_OFFSET[2]);
		}
		else
			g_log("[COOP] r14_glb_global_mp's level object isn't at space+0x38780 this time; its root wasn't moved");
	}

	/// Disables r14_glb_global_mp's own first-person player (the actor that owns its player nav): it took this player's
	/// grabbing and camera.
	static VOID DisableMpLocalPlayer(BYTE* game, BOOL quiet = FALSE)
	{
		BYTE* space = MpSpace(game);
		BYTE* nav = space == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
		if (nav == NULL || ComponentCount(nav) == 0)
		{
			if (!quiet)
				g_log("[COOP] no MP local player to disable");
			return;
		}
		BYTE out[0x20] = {};
		BYTE* actor = ((ComponentActorFn)(*(VOID***)nav)[0x170 / 8])(nav, out, 0);
		UINT64 handle = *(UINT64*)actor;
		g_log("[COOP] disabling the MP level's local player, actor handle %016llX", (unsigned long long)handle);
		g_mpLocalPlayerDisabled = TRUE;
		((ActorFn)(g_exe + 0x2CBA40))(space, handle);
		g_log("[COOP] MP local player disabled; player navs in that space now %u", ComponentCount(nav));
	}

	static VOID __declspec(noinline) UnloadMpLevel(BYTE* game)
	{
		BYTE* space = MpSpace(game);
		g_log("[COOP] unloading r14_glb_global_mp space %p", space);
		if (space != NULL)
			((UnloadLevelFn)Vtable(game)[SLOT_UNLOAD_LEVEL / 8])(game, space, 0);
	}

	/// F3: every loaded game space (the world's sorted list at game+0x630, 16-byte {level hash, space} entries, count at
	/// +0x658) with its husk, body, player nav and physics component counts. Read-only.
	static const UINT64 HASH_HUSK = 0;  // read at runtime: hash of "R14Husk" (qword before the symbol at 0x140bfb0f8)
	static VOID ListSpaces(BYTE* game)
	{
		UINT64 husk = *(UINT64*)(g_exe + 0xBFB0F0);
		UINT64 count = *(UINT64*)(game + 0x658);
		BYTE* entries = *(BYTE**)(game + 0x630);
		g_log("[COOP] %llu game spaces (R14Husk hash 0x%llX):", (unsigned long long)count, (unsigned long long)husk);
		for (UINT64 i = 0; i < count && i < 64; i++)
		{
			UINT64 level = *(UINT64*)(entries + i * 16);
			BYTE* space = *(BYTE**)(entries + i * 16 + 8);
			if (space == NULL)
				continue;
			UINT64 lists = 0;
			BYTE* huskCs = ComponentSystem(space, husk, &lists);
			BYTE* body = ComponentSystem(space, HASH_BODY, &lists);
			BYTE* nav = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
			BYTE* physics = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, 0x5B8CC538E22AD937ULL);
			g_log("[COOP]   level 0x%016llX space %p: husk system %p (%u), bodies %u, player navs %u, physics %u",
				(unsigned long long)level, space, huskCs, ComponentCount(huskCs), ComponentCount(body), ComponentCount(nav),
				ComponentCount(physics));
		}
	}

	// ---- Second Jack: r14_glb_global has two R14Body components but one player. Echo Arena's remote players drive an
	// R14Body from data with (UpdateRemoteBody 0x14072c140):
	//   0x14055e550(bodyCS, index, &bodyRotation(quat), &head(TRS), &handA(TRS), &handB(TRS), dt)
	//   0x1405640c0(bodyCS, index)
	//   0x140553170(bodyCS, index)
	// F2 toggles driving the body that isn't Jack's (Jack's is the one his nav points to, nav+0x938) with this player's
	// head and hands, moved 1.2 m sideways: a mirror test before the other player's data drives it.
	typedef VOID(__fastcall* BodyTargetsFn)(BYTE* cs, UINT64 index, const FLOAT* rotation, const Trs* head, const Trs* a, const Trs* b, FLOAT dt);
	typedef VOID(__fastcall* BodyIndexFn)(BYTE* cs, UINT64 index);
	static BOOL g_driveSecondBody = FALSE;
	static LONG g_secondBodyIndex = -1;
	static const FLOAT SECOND_BODY_OFFSET[3] = { 1.2f, 0.0f, 0.0f };

	// Targets for the second body, computed once a frame on the game thread; applied inside the engine's own per-body
	// update (0x140553170), right before it runs, so the copy-from-Jack the engine does each frame can't overwrite them.
	static BYTE* volatile g_bodiesCs = NULL;
	static Trs g_targetHead = {}, g_targetLeft = {}, g_targetRight = {};
	static FLOAT g_targetRotation[4] = { 0, 0, 0, 1 };
	static volatile LONG g_haveTargets = 0;
	static BodyIndexFn g_originalBodyUpdate = NULL;

	static VOID ApplySecondBodyTargets(BYTE* cs)
	{
		((BodyTargetsFn)(g_exe + 0x55E550))(cs, (UINT64)g_secondBodyIndex, g_targetRotation, &g_targetHead, &g_targetLeft,
			&g_targetRight, 1.0f / 90.0f);
		((BodyIndexFn)(g_exe + 0x5640C0))(cs, (UINT64)g_secondBodyIndex);
	}

	static VOID __fastcall HookedBodyUpdate(BYTE* cs, UINT64 index)
	{
		if (g_driveSecondBody && g_haveTargets && cs == g_bodiesCs && (LONG)(UINT16)index == g_secondBodyIndex)
		{
			__try { ApplySecondBodyTargets(cs); }
			__except (EXCEPTION_EXECUTE_HANDLER) {}
		}
		g_originalBodyUpdate(cs, index);
	}

	static VOID DriveSecondBody(BYTE* game)
	{
		BYTE* space = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		if (space == NULL)
			return;
		UINT64 lists = 0;
		BYTE* bodies = ComponentSystem(space, HASH_BODY, &lists);
		BYTE* nav = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, HASH_PLAYER_NAV);
		if (bodies == NULL || nav == NULL || ComponentCount(nav) == 0 || ComponentCount(bodies) < 2)
			return;
		if (g_originalBodyUpdate == NULL)
		{
			HookSite site = { 0x553170, (PVOID*)&g_originalBodyUpdate, (PVOID)HookedBodyUpdate };
			AttachAll(g_exe, &site, 1, "second Jack body update");
		}
		if (g_secondBodyIndex < 0)
		{
			g_secondBodyIndex = 1;  // body 0 is Jack's; body 1's skeleton was all zeros: its actor is switched off
			BYTE out[0x20] = {};
			BYTE* actor = ((ComponentActorFn)(*(VOID***)bodies)[0x170 / 8])(bodies, out, 1);
			UINT64 handle = actor == NULL ? ~0ULL : *(UINT64*)actor;
			g_log("[COOP] driving R14Body 1 of %u as the second Jack; switching its actor %016llX on", ComponentCount(bodies),
				(unsigned long long)handle);
			if ((UINT16)handle != 0xFFFF)
				((ActorFn)(g_exe + 0x2CC550))(space, handle);
		}
		g_bodiesCs = bodies;
		BYTE* navEntry = *(BYTE**)(nav + 0xE8);
		Trs head = {}, left = {}, right = {};
		((NavHeadFn)(g_exe + NAV_HEAD))(nav, &head, 0);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &left, 0, 0, 1);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &right, 0, 1, 1);
		for (int i = 0; i < 3; i++)
		{
			head.pos[i] += SECOND_BODY_OFFSET[i];
			left.pos[i] += SECOND_BODY_OFFSET[i];
			right.pos[i] += SECOND_BODY_OFFSET[i];
		}
		g_targetHead = head; g_targetLeft = left; g_targetRight = right;
		memcpy(g_targetRotation, navEntry + 0x410, sizeof(g_targetRotation));
		g_haveTargets = 1;
		// In case the engine doesn't update this body itself every frame.
		ApplySecondBodyTargets(bodies);
		g_originalBodyUpdate(bodies, (UINT64)g_secondBodyIndex);
	}

	// ---- Capture: find the actor under this player's right hand (nearest physics body within 1.5 m, any loaded space)
	// and log every component system that has a component for it. Component systems of a space: the engine list at
	// space+0x4e8 (count +0x510) and the game list at space+0x528 (count +0x550); entries are 16 bytes {type hash, x},
	// where x is the system (engine list) or a list whose first qword points at the systems (game list).
	typedef BYTE* (__fastcall* PhysicsHandleFn)(BYTE* cs, BYTE* out, UINT16 index);
	typedef BYTE* (__fastcall* ResolveBodyFn)(BYTE* handle);
	static UINT64 g_capturedActor = ~0ULL;
	static BYTE* g_capturedSpace = NULL;

	static UINT64 ComponentOwner(BYTE* cs, UINT16 index)
	{
		BYTE out[0x20] = {};
		BYTE* actor = ((ComponentActorFn)(*(VOID***)cs)[0x170 / 8])(cs, out, index);
		return actor == NULL ? ~0ULL : *(UINT64*)actor;
	}

	static VOID LogActorComponents(BYTE* space, UINT64 actor)
	{
		struct List { DWORD entries, count; BOOL game; } lists[] = { { 0x4E8, 0x510, FALSE }, { 0x528, 0x550, TRUE } };
		for (const List& list : lists)
		{
			BYTE* entries = *(BYTE**)(space + list.entries);
			UINT64 count = *(UINT64*)(space + list.count);
			for (UINT64 e = 0; e < count && e < 512; e++)
			{
				__try
				{
					BYTE* x = *(BYTE**)(entries + e * 16 + 8);
					BYTE* cs = x;
					if (list.game)
						cs = (x != NULL && *(UINT64*)(x + 0x28) > 0) ? (*(BYTE***)x)[0] : NULL;
					if (cs == NULL)
						continue;
					UINT16 n = ComponentCount(cs);
					for (UINT16 i = 0; i < n; i++)
						if (ComponentOwner(cs, i) == actor)
						{
							const CHAR* name = ClassName(*(BYTE**)cs);
							g_log("[COOP]     %s component %u (system %p)", name != NULL ? name : "?", i, cs);
							break;
						}
				}
				__except (EXCEPTION_EXECUTE_HANDLER) {}
			}
		}
	}

	typedef BYTE* (__fastcall* TransformWorldFn)(BYTE* cs, BYTE* out, UINT16 index);
	static const CHAR* SystemClass(BYTE* cs)
	{
		__try { return ClassName(*(BYTE**)cs); }
		__except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
	}
	/// Every component system of a space (engine and game lists), calling visit(cs).
	template <typename F> static VOID ForEachSystem(BYTE* space, F visit)
	{
		struct List { DWORD entries, count; BOOL game; } lists[] = { { 0x4E8, 0x510, FALSE }, { 0x528, 0x550, TRUE } };
		for (const List& list : lists)
		{
			BYTE* entries = *(BYTE**)(space + list.entries);
			UINT64 count = *(UINT64*)(space + list.count);
			for (UINT64 e = 0; e < count && e < 512; e++)
			{
				BYTE* cs = NULL;
				__try
				{
					BYTE* x = *(BYTE**)(entries + e * 16 + 8);
					cs = x;
					if (list.game)
						cs = (x != NULL && *(UINT64*)(x + 0x28) > 0) ? (*(BYTE***)x)[0] : NULL;
				}
				__except (EXCEPTION_EXECUTE_HANDLER) { cs = NULL; }
				if (cs != NULL)
					visit(cs);
			}
		}
	}
	/// World position of an actor through its space's CTransformCS (0x1402a7930 returns a TRS, position at +0x10).
	static BOOL ActorPosition(BYTE* transforms, UINT64 actor, FLOAT* position)
	{
		UINT16 n = ComponentCount(transforms);
		for (UINT16 i = 0; i < n; i++)
		{
			BOOL match = FALSE;
			__try { match = ComponentOwner(transforms, i) == actor; }
			__except (EXCEPTION_EXECUTE_HANDLER) { match = FALSE; }
			if (!match)
				continue;
			__try
			{
				BYTE out[0x40] = {};
				BYTE* trs = ((TransformWorldFn)(g_exe + 0x2A7930))(transforms, out, i);
				memcpy(position, trs + 0x10, 3 * sizeof(FLOAT));
				return TRUE;
			}
			__except (EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
		}
		return FALSE;
	}

	/// F6: the animated actor (it has an animation component) nearest this player's right hand, within 3 m.
	static VOID CaptureUnderHand(BYTE* game)
	{
		BYTE* globalSpace = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		BYTE* nav = globalSpace == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(globalSpace, HASH_PLAYER_NAV);
		if (nav == NULL || ComponentCount(nav) == 0)
			return;
		Trs hand = {};
		((NavHandFn)(g_exe + NAV_HAND))(nav, &hand, 0, 1, 1);
		g_log("[COOP] capture: right hand at (%.2f %.2f %.2f); animated actors within 3 m:", hand.pos[0], hand.pos[1], hand.pos[2]);
		FLOAT best = 3.0f; BYTE* bestSpace = NULL; UINT64 bestActor = ~0ULL;
		UINT64 spaces = *(UINT64*)(game + 0x658);
		BYTE* list = *(BYTE**)(game + 0x630);
		for (UINT64 s = 0; s < spaces && s < 64; s++)
		{
			BYTE* space = *(BYTE**)(list + s * 16 + 8);
			if (space == NULL || space == globalSpace)
				continue;
			BYTE* transforms = NULL;
			ForEachSystem(space, [&](BYTE* cs) {
				const CHAR* name = SystemClass(cs);
				if (name != NULL && strcmp(name, ".?AVCTransformCS@NRadEngine@@") == 0)
					transforms = cs;
			});
			if (transforms == NULL)
				continue;
			ForEachSystem(space, [&](BYTE* cs) {
				const CHAR* name = SystemClass(cs);
				if (name == NULL || strstr(name, "Anim") == NULL)
					return;
				UINT16 n = ComponentCount(cs);
				for (UINT16 i = 0; i < n && i < 2048; i++)
				{
					UINT64 actor = ~0ULL;
					__try { actor = ComponentOwner(cs, i); }
					__except (EXCEPTION_EXECUTE_HANDLER) { continue; }
					FLOAT p[3];
					if (!ActorPosition(transforms, actor, p))
						continue;
					FLOAT d = sqrtf((p[0] - hand.pos[0]) * (p[0] - hand.pos[0]) + (p[1] - hand.pos[1]) * (p[1] - hand.pos[1]) +
						(p[2] - hand.pos[2]) * (p[2] - hand.pos[2]));
					if (d < 3.0f)
						g_log("[COOP]   %s: actor %016llX at (%.2f %.2f %.2f), %.2f m", name, (unsigned long long)actor, p[0], p[1], p[2], d);
					if (d < best)
					{
						best = d; bestSpace = space; bestActor = actor;
					}
				}
			});
		}
		if (bestSpace == NULL)
		{
			g_log("[COOP] capture: no animated actor within 3 m of the right hand");
			return;
		}
		g_capturedActor = bestActor;
		g_capturedSpace = bestSpace;
		g_log("[COOP] capture: actor %016llX in level %016llX, %.2f m away; its components:", (unsigned long long)bestActor,
			*(unsigned long long*)(bestSpace + 0xD8), best);
		LogActorComponents(bestSpace, bestActor);
	}

	// ---- Take over the captured actor (F7): place its transform node every frame. A transform record (CTransformCS,
	// 0x80 bytes) holds its scene node at +8; a node's local TRS is [[node+0x70]+0x28] + [node+0x78]*0x30, its parent
	// is node+0x60, and 0x140211cb0(out, node) gives its world TRS (local composed with the parents').
	typedef BYTE* (__fastcall* NodeWorldFn)(Trs* out, BYTE* node);
	static BOOL g_takeOver = FALSE;
	static BYTE* g_capturedNode = NULL;
	static BYTE* g_capturedPhysics = NULL;  // the actor's physics system and component index (-1: none)
	static LONG g_capturedPhysicsIndex = -1;
	typedef VOID(__fastcall* SetBodyTransformFn)(BYTE* body, const FLOAT* position, const FLOAT* rotation);

	/// The robot's root node is a CPhysicsNode that follows its physics body: teleport the body (0x140181c80) and stop
	/// it, so the node, its cached world matrix and its bounds all follow.
	static VOID MoveCapturedBody(const FLOAT* rotation, const FLOAT* position)
	{
		if (g_capturedPhysics == NULL || g_capturedPhysicsIndex < 0)
			return;
		BYTE handle[0x40] = {};
		BYTE* h = ((PhysicsHandleFn)(g_exe + 0x2A0480))(g_capturedPhysics, handle, (UINT16)g_capturedPhysicsIndex);
		BYTE* body = h == NULL ? NULL : ((ResolveBodyFn)(g_exe + 0x157F20))(h);
		if (body == NULL)
			return;
		((SetBodyTransformFn)(g_exe + 0x181C80))(body, position, rotation);
		memset(body + 0x950, 0, 3 * sizeof(FLOAT));
	}

	static BYTE* CapturedNode()
	{
		if (g_capturedSpace == NULL)
			return NULL;
		BYTE* transforms = NULL;
		ForEachSystem(g_capturedSpace, [&](BYTE* cs) {
			const CHAR* name = SystemClass(cs);
			if (name != NULL && strcmp(name, ".?AVCTransformCS@NRadEngine@@") == 0)
				transforms = cs;
		});
		UINT16 n = ComponentCount(transforms);
		for (UINT16 i = 0; i < n; i++)
			if (ComponentOwner(transforms, i) == g_capturedActor)
			{
				UINT16 slot = *(UINT16*)(*(BYTE**)(transforms + 0xB8) + i * 4);
				return *(BYTE**)(*(BYTE**)(transforms + 0xE8) + slot * 0x80 + 8);
			}
		return NULL;
	}

	static FLOAT* NodeLocal(BYTE* node)
	{
		return (FLOAT*)(*(BYTE**)(*(BYTE**)(node + 0x70) + 0x28) + *(UINT16*)(node + 0x78) * 0x30);
	}

	/// Puts the node's world transform at (rotation, position) by writing its local transform.
	static VOID SetNodeWorld(BYTE* node, const FLOAT* rotation, const FLOAT* position)
	{
		FLOAT* local = NodeLocal(node);
		BYTE* parent = *(BYTE**)(node + 0x60);
		FLOAT parentRot[4] = { 0, 0, 0, 1 }, parentPos[3] = { 0, 0, 0 }, parentScale[3] = { 1, 1, 1 };
		if (parent != NULL)
		{
			Trs world = {};
			((NodeWorldFn)(g_exe + 0x211CB0))(&world, parent);
			memcpy(parentRot, world.rot, sizeof(parentRot));
			memcpy(parentPos, world.pos, sizeof(parentPos));
			memcpy(parentScale, world.scale, sizeof(parentScale));
		}
		FLOAT inverse[4] = { -parentRot[0], -parentRot[1], -parentRot[2], parentRot[3] };
		FLOAT delta[3] = { position[0] - parentPos[0], position[1] - parentPos[1], position[2] - parentPos[2] };
		FLOAT localPos[3], localRot[4];
		QuatRotate(inverse, delta, localPos);
		for (int i = 0; i < 3; i++)
			localPos[i] /= parentScale[i] != 0 ? parentScale[i] : 1.0f;
		QuatMul(inverse, rotation, localRot);
		// Through the node's own setters (CNode3D virtuals +0x68 position, +0x70 rotation): they also set its "moved"
		// bits (+0x40), which the renderer needs to update its world matrix and bounds. Writing the local transform
		// directly left the robot culled by its old bounds (it vanished depending on where this player looked).
		typedef VOID(__fastcall* NodeSetFn)(BYTE* node, const FLOAT* value);
		VOID** vtable = *(VOID***)node;
		((NodeSetFn)vtable[0x68 / 8])(node, localPos);
		((NodeSetFn)vtable[0x70 / 8])(node, localRot);
		(void)local;
	}

	// Lone Echo's scripts hide sub-levels (rooms) the player isn't near: space+0x9d0 is a hide counter (visible when 0;
	// 0x1407f8cc0 is the script node that changes it, and the renderer skips the space's models when it isn't 0). The
	// robot belongs to its home room, so it vanished whenever that room got hidden. While it's taken over, hold its
	// room's counter at 0 and keep what the scripts did to it, to put back on release.
	static INT64 g_hiddenOffset = 0;
	static VOID KeepCapturedRoomVisible()
	{
		INT64* counter = (INT64*)(g_capturedSpace + 0x9D0);
		if (*counter != 0)
		{
			g_hiddenOffset += *counter;
			*counter = 0;
		}
	}
	static VOID RestoreCapturedRoom(BYTE* space)
	{
		if (space == NULL || g_hiddenOffset == 0)
			return;
		*(INT64*)(space + 0x9D0) += g_hiddenOffset;
		g_log("[COOP] take over: gave the robot's room back its hide count (%lld)", (long long)g_hiddenOffset);
		g_hiddenOffset = 0;
	}

	// F7: the robot copies this player's body movement. When it's taken over, its place is fixed relative to this
	// player's body (nav+0x410, player-to-world): 1.5 m ahead of the head, turned to face them. Each frame it goes to
	// body * that offset, so boosting, climbing and turning carry it along the same way.
	static FLOAT g_offsetRot[4] = { 0, 0, 0, 1 }, g_offsetPos[3] = {};
	static BOOL g_haveOffset = FALSE;

	static BOOL PlayerBody(BYTE* game, FLOAT* rotation, FLOAT* position, Trs* head)
	{
		BYTE* globalSpace = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		BYTE* nav = globalSpace == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(globalSpace, HASH_PLAYER_NAV);
		if (nav == NULL || ComponentCount(nav) == 0)
			return FALSE;
		BYTE* entry = *(BYTE**)(nav + 0xE8);
		memcpy(rotation, entry + 0x410, 4 * sizeof(FLOAT));
		memcpy(position, entry + 0x420, 3 * sizeof(FLOAT));
		if (head != NULL)
			((NavHeadFn)(g_exe + NAV_HEAD))(nav, head, 0);
		return TRUE;
	}

	static VOID TakeOverFrame(BYTE* game)
	{
		if (g_capturedNode == NULL)
		{
			g_capturedNode = CapturedNode();
			g_capturedPhysics = NULL; g_capturedPhysicsIndex = -1;
			BYTE* physics = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(g_capturedSpace, 0x5B8CC538E22AD937ULL);
			UINT16 n = ComponentCount(physics);
			for (UINT16 i = 0; i < n; i++)
				if (ComponentOwner(physics, i) == g_capturedActor)
				{
					g_capturedPhysics = physics; g_capturedPhysicsIndex = i;
					break;
				}
			g_haveOffset = FALSE;
			g_log("[COOP] take over: captured actor %016llX node %p, physics component %ld", (unsigned long long)g_capturedActor,
				g_capturedNode, g_capturedPhysicsIndex);
			if (g_capturedNode == NULL)
			{
				g_takeOver = FALSE;
				return;
			}
		}
		FLOAT bodyRot[4], bodyPos[3];
		Trs head = {};
		if (!PlayerBody(game, bodyRot, bodyPos, &head))
			return;
		FLOAT inverse[4] = { -bodyRot[0], -bodyRot[1], -bodyRot[2], bodyRot[3] };
		if (!g_haveOffset)
		{
			// Start: 1.5 m ahead of the head, facing the player; kept from now on relative to the body.
			FLOAT ahead[3] = { 0, 0, g_forwardSign }, forward[3];
			QuatRotate(head.rot, ahead, forward);
			FLOAT start[3] = { head.pos[0] + 1.5f * forward[0], head.pos[1] + 1.5f * forward[1] - 0.6f, head.pos[2] + 1.5f * forward[2] };
			static const FLOAT HALF_TURN_Y[4] = { 0.0f, 1.0f, 0.0f, 0.0f };
			FLOAT startRot[4];
			QuatMul(HALF_TURN_Y, head.rot, startRot);
			FLOAT delta[3] = { start[0] - bodyPos[0], start[1] - bodyPos[1], start[2] - bodyPos[2] };
			QuatRotate(inverse, delta, g_offsetPos);
			QuatMul(inverse, startRot, g_offsetRot);
			g_haveOffset = TRUE;
		}
		FLOAT position[3], rotation[4], rotated[3];
		QuatRotate(bodyRot, g_offsetPos, rotated);
		for (int i = 0; i < 3; i++)
			position[i] = bodyPos[i] + rotated[i];
		QuatMul(bodyRot, g_offsetRot, rotation);
		KeepCapturedRoomVisible();
		MoveCapturedBody(rotation, position);
		SetNodeWorld(g_capturedNode, rotation, position);
	}

	/// F4: what the captured robot is drawn with. Every component in its space whose actor's transform node sits under
	/// the robot's node (its parts), with the component system's class.
	static VOID ListCapturedParts()
	{
		BYTE* root = g_capturedNode != NULL ? g_capturedNode : CapturedNode();
		if (root == NULL)
		{
			g_log("[COOP] parts: capture a robot with F6 first");
			return;
		}
		BYTE* transforms = NULL;
		ForEachSystem(g_capturedSpace, [&](BYTE* cs) {
			const CHAR* name = SystemClass(cs);
			if (name != NULL && strcmp(name, ".?AVCTransformCS@NRadEngine@@") == 0)
				transforms = cs;
		});
		// Actors whose node is the root or under it (parent chain at node+0x60).
		static UINT64 parts[512]; UINT32 count = 0;
		UINT16 n = ComponentCount(transforms);
		for (UINT16 i = 0; i < n && count < ARRAYSIZE(parts); i++)
		{
			__try
			{
				UINT16 slot = *(UINT16*)(*(BYTE**)(transforms + 0xB8) + i * 4);
				BYTE* node = *(BYTE**)(*(BYTE**)(transforms + 0xE8) + slot * 0x80 + 8);
				for (int depth = 0; node != NULL && depth < 16; depth++, node = *(BYTE**)(node + 0x60))
					if (node == root)
					{
						parts[count++] = ComponentOwner(transforms, i);
						break;
					}
			}
			__except (EXCEPTION_EXECUTE_HANDLER) {}
		}
		g_log("[COOP] parts: %u actors under the robot's node %p", count, root);
		ForEachSystem(g_capturedSpace, [&](BYTE* cs) {
			const CHAR* name = SystemClass(cs);
			UINT16 m = 0;
			__try { m = ComponentCount(cs); } __except (EXCEPTION_EXECUTE_HANDLER) { m = 0; }
			UINT32 hits = 0;
			for (UINT16 i = 0; i < m; i++)
			{
				UINT64 owner = ~0ULL;
				__try { owner = ComponentOwner(cs, i); } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
				for (UINT32 j = 0; j < count; j++)
					if (parts[j] == owner)
					{
						hits++;
						break;
					}
			}
			if (hits > 0)
				g_log("[COOP]   %s: %u components on the robot's parts (system %p)", name != NULL ? name : "?", hits, cs);
		});
	}

	// ---- Stand-in avatar: three loose objects from the level are the other player's head and hands. They're moved
	// through their physics bodies (teleport 0x140181c80 + zero velocity), the normal path for loose objects, so they
	// stay drawn and touch nothing else. F6/F7/F8 tag the object under the right hand as head / left hand / right hand;
	// F2 turns it on. Until the network carries the other player, it plays this player's own head and hands back
	// AVATAR_DELAY seconds late, AVATAR_SHIFT metres ahead of where the head was when F2 was pressed.
	struct Tagged { BYTE* physics; LONG index; };
	static Tagged g_avatar[3] = { { NULL, -1 }, { NULL, -1 }, { NULL, -1 } };  // head, left hand, right hand
	static const CHAR* AVATAR_PART[3] = { "head", "left hand", "right hand" };
	static BOOL g_avatarOn = FALSE;
	static const DOUBLE AVATAR_DELAY = 2.0;
	static const FLOAT AVATAR_SHIFT = 1.5f;
	struct AvatarSample { LONGLONG time; Trs part[3]; };
	static AvatarSample g_avatarRing[1024] = {};
	static LONG g_avatarCount = 0;
	static FLOAT g_avatarShift[3] = {};

	static BYTE* BodyOf(BYTE* physics, LONG index)
	{
		BYTE handle[0x40] = {};
		BYTE* h = ((PhysicsHandleFn)(g_exe + 0x2A0480))(physics, handle, (UINT16)index);
		return h == NULL ? NULL : ((ResolveBodyFn)(g_exe + 0x157F20))(h);
	}

	/// The loose object (any physics body outside Jack's own level) nearest the right hand, within 0.6 m.
	static VOID TagUnderHand(BYTE* game, int part)
	{
		BYTE* globalSpace = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		BYTE* nav = globalSpace == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(globalSpace, HASH_PLAYER_NAV);
		if (nav == NULL || ComponentCount(nav) == 0)
			return;
		Trs hand = {};
		((NavHandFn)(g_exe + NAV_HAND))(nav, &hand, 0, 1, 1);
		FLOAT best = 0.6f; Tagged found = { NULL, -1 };
		UINT64 spaces = *(UINT64*)(game + 0x658);
		BYTE* list = *(BYTE**)(game + 0x630);
		for (UINT64 s = 0; s < spaces && s < 64; s++)
		{
			BYTE* space = *(BYTE**)(list + s * 16 + 8);
			if (space == NULL || space == globalSpace)
				continue;
			BYTE* physics = ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(space, 0x5B8CC538E22AD937ULL);
			UINT16 n = ComponentCount(physics);
			for (UINT16 i = 0; i < n; i++)
			{
				__try
				{
					BYTE* body = BodyOf(physics, i);
					if (body == NULL)
						continue;
					FLOAT* p = (FLOAT*)(body + 0x938);
					FLOAT d = sqrtf((p[0] - hand.pos[0]) * (p[0] - hand.pos[0]) + (p[1] - hand.pos[1]) * (p[1] - hand.pos[1]) +
						(p[2] - hand.pos[2]) * (p[2] - hand.pos[2]));
					if (d < best)
					{
						best = d; found.physics = physics; found.index = i;
					}
				}
				__except (EXCEPTION_EXECUTE_HANDLER) {}
			}
		}
		if (found.physics == NULL)
		{
			g_log("[COOP] avatar: no loose object within 0.6 m of the right hand (for the %s)", AVATAR_PART[part]);
			return;
		}
		g_avatar[part] = found;
		g_log("[COOP] avatar: the %s is physics component %ld (%.2f m from the right hand)", AVATAR_PART[part], found.index, best);
	}

	/// Once a frame: record this player's head and hands; when the avatar is on, put the tagged objects where they
	/// were AVATAR_DELAY seconds ago, shifted ahead.
	static VOID AvatarFrame(BYTE* game)
	{
		BYTE* globalSpace = ((FindSpaceFn)(g_exe + FIND_SPACE))(game, *(UINT64*)(g_exe + SP_GLOBAL_LEVEL));
		BYTE* nav = globalSpace == NULL ? NULL : ((FindEngineComponentFn)(g_exe + FIND_ENGINE_COMPONENT))(globalSpace, HASH_PLAYER_NAV);
		if (nav == NULL || ComponentCount(nav) == 0)
			return;
		AvatarSample& now = g_avatarRing[g_avatarCount % ARRAYSIZE(g_avatarRing)];
		((NavHeadFn)(g_exe + NAV_HEAD))(nav, &now.part[0], 0);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &now.part[1], 0, 0, 1);
		((NavHandFn)(g_exe + NAV_HAND))(nav, &now.part[2], 0, 1, 1);
		LARGE_INTEGER t, f;
		QueryPerformanceCounter(&t);
		QueryPerformanceFrequency(&f);
		now.time = t.QuadPart;
		g_avatarCount++;
		if (!g_avatarOn)
			return;
		LONGLONG wanted = t.QuadPart - (LONGLONG)(AVATAR_DELAY * f.QuadPart);
		LONG oldest = g_avatarCount > (LONG)ARRAYSIZE(g_avatarRing) ? g_avatarCount - (LONG)ARRAYSIZE(g_avatarRing) : 0;
		const AvatarSample* then = &g_avatarRing[oldest % ARRAYSIZE(g_avatarRing)];
		for (LONG i = g_avatarCount - 1; i >= oldest; i--)
			if (g_avatarRing[i % ARRAYSIZE(g_avatarRing)].time <= wanted)
			{
				then = &g_avatarRing[i % ARRAYSIZE(g_avatarRing)];
				break;
			}
		for (int part = 0; part < 3; part++)
		{
			if (g_avatar[part].physics == NULL)
				continue;
			__try
			{
				BYTE* body = BodyOf(g_avatar[part].physics, g_avatar[part].index);
				if (body == NULL)
					continue;
				FLOAT position[3];
				for (int i = 0; i < 3; i++)
					position[i] = then->part[part].pos[i] + g_avatarShift[i];
				((SetBodyTransformFn)(g_exe + 0x181C80))(body, position, then->part[part].rot);
				memset(body + 0x950, 0, 3 * sizeof(FLOAT));
			}
			__except (EXCEPTION_EXECUTE_HANDLER) {}
		}
	}

	static VOID ToggleAvatar()
	{
		g_avatarOn = !g_avatarOn;
		if (g_avatarOn && g_avatarCount > 0)
		{
			// Shift: AVATAR_SHIFT metres ahead of the head, level.
			const Trs& head = g_avatarRing[(g_avatarCount - 1) % ARRAYSIZE(g_avatarRing)].part[0];
			FLOAT ahead[3] = { 0, 0, g_forwardSign }, forward[3];
			QuatRotate(head.rot, ahead, forward);
			FLOAT length = sqrtf(forward[0] * forward[0] + forward[2] * forward[2]);
			if (length < 0.01f) length = 1.0f;
			g_avatarShift[0] = AVATAR_SHIFT * forward[0] / length;
			g_avatarShift[1] = 0.0f;
			g_avatarShift[2] = AVATAR_SHIFT * forward[2] / length;
		}
		g_log("[COOP] avatar %s (head %ld, left hand %ld, right hand %ld)", g_avatarOn ? "on" : "off", g_avatar[0].index,
			g_avatar[1].index, g_avatar[2].index);
	}

	/// Runs the hotkeys. Separate from the hook so an exception in an experiment is logged instead of crashing the game.
	static VOID Frame(BYTE* game)
	{
		__try
		{
			static ULONGLONG lastAvatarKey = 0;
			BOOL debounced = GetTickCount64() - lastAvatarKey > 400;
			if (Pressed(VK_F6) && debounced) { lastAvatarKey = GetTickCount64(); TagUnderHand(game, 0); }
			if (Pressed(VK_F7) && debounced) { lastAvatarKey = GetTickCount64(); TagUnderHand(game, 1); }
			if (Pressed(VK_F8) && debounced) { lastAvatarKey = GetTickCount64(); TagUnderHand(game, 2); }
			if (Pressed(VK_F3)) ListSpaces(game);
			if (Pressed(VK_F12)) LeRender::RequestDrawLog();
			static ULONGLONG lastF2 = 0;
			if (Pressed(VK_F2) && GetTickCount64() - lastF2 > 500)
			{
				lastF2 = GetTickCount64();
				ToggleAvatar();
			}
			AvatarFrame(game);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			g_log("[COOP] exception 0x%08X in a hotkey action", GetExceptionCode());
		}
	}

	static UINT64 __fastcall HookedUpdate(BYTE* game, UINT64 phase)
	{
		if (g_game != game)
		{
			g_game = game;
			g_log("[COOP] game object %p (avatar hotkeys: F6/F7/F8 tag head/left/right hand under your right hand, F2 avatar on/off)", game);
		}
		if (phase == 1)
		{
			Frame(game);
			KeepStoryScene();
			__try
			{
				g_puppetSpace = MpSpace(game);
				g_frozenSpace = g_puppetSpace;
				if (g_puppetSpace != NULL && !g_mpLocalPlayerDisabled)
					DisableMpLocalPlayer(game, TRUE);
				if (g_puppetSpace != NULL && g_mpLocalPlayerDisabled && !g_mpRootMoved)
					MoveMpLevelRoot(game);
				if (g_puppetSpace != NULL && g_mpLocalPlayerDisabled && !g_mpCollisionDisabled)
					DisableMpCollision(game);
				if (g_puppetSpace != NULL && !g_mpLocalPlayerDisabled)
				{
					DisablePuppetCamera(g_puppetSpace);
					WritePuppetNav(g_puppetSpace);
				}
			}
			__except (EXCEPTION_EXECUTE_HANDLER) { g_puppetSpace = NULL; }
		}
		return g_originalUpdate(game, phase);
	}

	VOID Install(BYTE* exe, VOID(*log)(const CHAR* format, ...))
	{
		g_exe = exe;
		g_log = log;
		VOID** slot = (VOID**)(exe + GAME_VTABLE + SLOT_UPDATE);
		DWORD oldProtect;
		if (!VirtualProtect(slot, sizeof(VOID*), PAGE_READWRITE, &oldProtect))
		{
			log("[COOP] couldn't hook the game update (VirtualProtect error %lu)", GetLastError());
			return;
		}
		g_originalUpdate = (UpdateFn)*slot;
		*slot = (VOID*)HookedUpdate;
		VirtualProtect(slot, sizeof(VOID*), oldProtect, &oldProtect);
		log("[COOP] hooked the game update (original %p); co-op experiment hotkeys active", g_originalUpdate);
		LeRender::Install(log);

	}
}
