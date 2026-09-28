#include "summerpatches.h"
#include "summersocial.h"
#include "xmaspatches.h"
#include "voiplog.h"
#include "framelimit.h"
#include <winternl.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>
#include <share.h>
#include <string>

namespace SummerPatches
{
	/// <summary>
	/// A byte patch at a virtual address (image base 0x140000000 for echovr.exe) or an RVA (for DLLs).
	/// </summary>
	struct BytePatch
	{
		const CHAR* description;
		UINT64 address;
		BYTE original[24];
		BYTE patched[24];
		SIZE_T size;
	};

	/// <summary>
	/// A site we have not located on a given build. Patches marked this way are skipped, with a log line.
	/// </summary>
	#define NO_PATCH { NULL, 0, { 0 }, { 0 }, 0 }

	/// <summary>
	/// Everything that differs between the lobby builds we support. Game addresses are virtual addresses
	/// (image base 0x140000000 for echovr.exe); pnsovr.dll addresses are RVAs.
	/// </summary>
	struct BuildProfile
	{
		const CHAR* name;
		DWORD executableTimestamp;
		DWORD pnsOvrTimestamp;
		BytePatch watchdog;
		BytePatch serverFlagsGate;  // makes the block serverFlags patches run, on builds where it is behind a flag
		BytePatch serverFlags;
		BytePatch serverPackage;
		BytePatch serverProviders[2]; // a dedicated server's net providers, on builds that would load pnsdemo.dll
		BytePatch noOvr;
		BytePatch noOvrStatus;
		BytePatch noAudio;
		BytePatch headless;        // emulates -headless on builds without it (applied instead of noAudio; it clears audio too)
		BytePatch multiInstance;
		BytePatch pnsOvr[8];
		UINT64 statusHostString;   // 0 when the status/news redirect has not been located on this build
		UINT64 statusHostLeas[2];
		BOOL socialSupported;      // parties/friends through EchoRelay (summersocial)
		BOOL supportsHeadless;     // the game itself knows -headless (unknown flags make these builds exit; see headless)
		BOOL serverSkipsOvr;       // -server implies -noovr. Not on builds whose renderer needs an OVR swap chain.
		DWORD pnsOvrOrgScopedId;   // builds played without Revive: pnsovr.dll's org-scoped id, set to this install's own id (0: not used)
		UINT64 frameTimerVtable;   // the frame timer's vtable, whose per-frame method servers are rate-limited through (see framelimit.h)
		BytePatch ownPurchases;    // builds where Echo Combat is an in-app purchase: the "owns this item" check says yes
	};

	static const BuildProfile BUILDS[] = {
		{
			"summer (rad15_summer, goldmaster 340872)", 0x5D388D3C, 0x5D388D2C,
			{ "disable the deadlock watchdog (long level loads trip it and it deliberately crashes the game)",
				0x140C35D97, { 0x76, 0x2E }, { 0xEB, 0x2E }, 2 },
			NO_PATCH, // the startup flags block runs by default on this build
			{ "set the dedicated server startup flags (or dword [rbx+6C40h], 1 -> 6)",
				0x14005578C, { 0x83, 0x8B, 0x40, 0x6C, 0x00, 0x00, 0x01 }, { 0x83, 0x8B, 0x40, 0x6C, 0x00, 0x00, 0x06 }, 7 },
			{ "load the client data package (r14netclient) in server mode, since r14netserver is not shipped",
				0x140044F53, { 0x48, 0x8D, 0x15, 0x96, 0x91, 0x14, 0x01 }, { 0x48, 0x8D, 0x15, 0x86, 0x91, 0x14, 0x01 }, 7 },
			{ NO_PATCH, NO_PATCH }, // dedicated servers run without net providers on this build
			{ "skip Oculus/VR initialization", 0x1407CF501, { 0x74, 0x22 }, { 0xEB, 0x22 }, 2 },
			{ "silence 'Failed to get Oculus session status' (logged every frame without VR)",
				0x14049424D, { 0x74, 0x24 }, { 0xEB, 0x24 }, 2 },
			{ "disable audio, as -noaudio would (always clear the audio flag bit while parsing the command line)",
				0x1404811B6, { 0x74, 0x07 }, { 0x90, 0x90 }, 2 },
			NO_PATCH, // the game handles -headless itself
			{ "skip the single-instance mutex check", 0x1407CF406, { 0x74, 0x58 }, { 0xEB, 0x58 }, 2 },
			{
				{ "send SNSLoginRequest without an Oculus access token", 0x1050D, { 0x74, 0x5B }, { 0x90, 0x90 }, 2 },
				{ "send SNSLoginRequest without an Oculus access token (retry path)", 0x131E2, { 0x0F, 0x84, 0x15, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 },
				{ "skip the Oculus entitlement check", 0x13AD4, { 0x75, 0x21 }, { 0xEB, 0x21 }, 2 },
				// Played without Revive: the Oculus user proof fails and the provider posts a login failure without connecting;
				// take the success path with an empty nonce, and keep the org-scoped id (set to the player's own id) when
				// ovr_User_GetOrgScopedID fails.
				{ "log in even though ovr_User_GetUserProof failed", 0xB9C3,
					{ 0x0F, 0x84, 0xB3, 0x00, 0x00, 0x00 }, { 0xE9, 0xB4, 0x00, 0x00, 0x00, 0x90 }, 6 },
				{ "log in with an empty nonce (lea rax, [\"\"] in place of ovr_UserProof_GetNonce(ovr_Message_GetUserProof(msg)))", 0xBBA2,
					{ 0x48, 0x8B, 0xCB, 0xFF, 0x15, 0xB5, 0x1A, 0x07, 0x00, 0x48, 0x8B, 0xC8, 0xFF, 0x15, 0x14, 0x18, 0x07, 0x00 },
					{ 0x48, 0x8D, 0x05, 0x2F, 0x60, 0x07, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 18 },
				{ "keep the org-scoped id when ovr_User_GetOrgScopedID fails", 0xA533,
					{ 0x48, 0xC7, 0x05, 0xBA, 0xA5, 0x14, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
					{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 11 },
			},
			0x141248C28,                  // "https://api.readyatdawn.com"
			{ 0x1405F0E4A, 0x1405F0F2B }, // lea rdx, [host] for status/services and status/news
			TRUE,
			TRUE,
			TRUE,
			0x154AF8,                     // no Revive: the player's own id (from their display name)
			0x141186958,                  // frame timer vtable
		},
		{
			// The 2018 halloween lobby build. Sites were located against the summer build by their anchors (strings,
			// imports, instruction shapes); most share the summer build's original bytes. Social is not ported yet.
			"halloween (rad15_halloween, goldmaster 253636)", 0x5BC7B897, 0x5BC7B836,
			{ "disable the deadlock watchdog (long level loads trip it and it deliberately crashes the game)",
				0x140D0BC37, { 0x76, 0x2E }, { 0xEB, 0x2E }, 2 },
			NO_PATCH, // the lobby flags setter below runs by default on this build
			// Unlike summer, this build sets its startup flags in a small setter (also reached by -mpmnu) rather than
			// in the command line block. Flag 1 would take the local/offline path instead of starting the dedicated server.
			{ "set the dedicated server startup flags (or dword [rcx+58B8h], 1 -> 6)",
				0x1400B915B, { 0x83, 0x89, 0xB8, 0x58, 0x00, 0x00, 0x01 }, { 0x83, 0x89, 0xB8, 0x58, 0x00, 0x00, 0x06 }, 7 },
			{ "load the client data package (r14netclient) in server mode, since r14netserver is not shipped",
				0x1400B1826, { 0x48, 0x8D, 0x15, 0x13, 0x48, 0xEF, 0x00 }, { 0x48, 0x8D, 0x15, 0x03, 0x48, 0xEF, 0x00 }, 7 },
			// Dedicated mode (flag 4) loads pnsdemo.dll here, which is not shipped, then the RAD provider (EchoRelay). Skip
			// straight to the RAD provider (xor r14d, r14d; jmp 0x1400B4796), so a server needs neither pnsdemo.dll nor the
			// Oculus platform (pnsovr.dll). Unlike summer's, this build's NetGame can't run with an empty provider slot
			// (ending a session fails with "Net service provider not initialized"), so it gets the RAD provider in both.
			{
				{ "create only the RAD net provider in dedicated mode (no pnsdemo.dll, which is not shipped, and no Oculus)",
					0x1400B4721, { 0x4C, 0x8D, 0x05, 0x48, 0x1B, 0xEF, 0x00, 0x33 }, { 0x45, 0x33, 0xF6, 0xE9, 0x6D, 0x00, 0x00, 0x00 }, 8 },
				{ "give the dedicated server's NetGame the RAD provider in place of the demo one (mov r8, r14 -> mov r8, r15)",
					0x1400B4846, { 0x4D, 0x8B, 0xC6 }, { 0x4D, 0x8B, 0xC7 }, 3 },
			},
			{ "skip Oculus/VR initialization", 0x140D32111, { 0x74, 0x20 }, { 0xEB, 0x20 }, 2 },
			{ "silence 'Failed to get Oculus session status' (logged every frame without VR)",
				0x1402EDD0D, { 0x74, 0x1E }, { 0xEB, 0x1E }, 2 },
			{ "disable audio, as -noaudio would (always clear the audio flag bit while parsing the command line)",
				0x1402F60B6, { 0x74, 0x07 }, { 0x90, 0x90 }, 2 },
			// This build has no -headless, but still skips the renderer when the graphics bits summer's -headless clears
			// (0x10101 of the same settings dword) are off. Replace the -noaudio test and its 'and dword [rbx+1D4h], ~2'
			// with an unconditional 'and dword [rbx+1D4h], ~10103h' (audio and graphics off); ecx is reloaded after.
			{ "run with no graphics or audio, as -headless does on later builds (and dword [rbx+1D4h], 0FFFEFEFCh)",
				0x1402F60AA,
				{ 0x41, 0x8B, 0xCF, 0x48, 0x83, 0xF8, 0xFF, 0x0F, 0x95, 0xC1, 0x85, 0xC9, 0x74, 0x07, 0x83, 0xA3, 0xD4, 0x01, 0x00, 0x00, 0xFD },
				{ 0x81, 0xA3, 0xD4, 0x01, 0x00, 0x00, 0xFC, 0xFE, 0xFE, 0xFF, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 21 },
			{ "skip the single-instance mutex check", 0x140D32015, { 0x74, 0x58 }, { 0xEB, 0x58 }, 2 },
			{
				{ "send SNSLoginRequest without an Oculus access token", 0xDC22, { 0x74, 0x5E }, { 0x90, 0x90 }, 2 },
				{ "send SNSLoginRequest without an Oculus access token (retry path)", 0x1031F, { 0x0F, 0x84, 0x15, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 },
				{ "skip the Oculus entitlement check", 0x10AC4, { 0x75, 0x21 }, { 0xEB, 0x21 }, 2 },
				// Played without Revive: the Oculus user proof fails and the provider posts a login failure without connecting;
				// take the success path with an empty nonce, and keep the org-scoped id (set to the player's own id) when
				// ovr_User_GetOrgScopedID fails.
				{ "log in even though ovr_User_GetUserProof failed", 0xA201,
					{ 0x0F, 0x84, 0xB1, 0x00, 0x00, 0x00 }, { 0xE9, 0xB2, 0x00, 0x00, 0x00, 0x90 }, 6 },
				{ "log in with an empty nonce (lea rax, [\"\"] in place of ovr_UserProof_GetNonce(ovr_Message_GetUserProof(msg)))", 0xA3BE,
					{ 0x48, 0x8B, 0xCB, 0xFF, 0x15, 0x81, 0x51, 0x04, 0x00, 0x48, 0x8B, 0xC8, 0xFF, 0x15, 0xD0, 0x51, 0x04, 0x00 },
					{ 0x48, 0x8D, 0x05, 0x53, 0x94, 0x04, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 18 },
				{ "keep the org-scoped id when ovr_User_GetOrgScopedID fails", 0x92FC,
					{ 0x48, 0xC7, 0x05, 0xB9, 0xEA, 0x0E, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
					{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 11 },
			},
			0x1411027F8,                  // "https://api.readyatdawn.com"
			{ 0x1408E553A, 0x1408E561A }, // lea rdx, [host] for status/services and status/news
			TRUE,  // parties, friends and unique player ids (shared Revive id / no Oculus user) through EchoRelay, as on summer
			// No -headless in this build, and its renderer (even under -spectatorstream) creates an OVR swap chain, which
			// fails fatally ("Failed to create OVR D3D swap chain (-1004)") if Oculus initialization was skipped.
			FALSE,
			FALSE,
			0xF7DC0,                      // no Revive: the player's own id (from their display name)
			0x140FE47E0,                  // frame timer vtable
		},
		{
			// The 2018 christmas ("winter") lobby build, two months after halloween and the same generation. Sites were
			// located against the halloween build: by masked signatures (watchdog, data package, status/audio/headless,
			// the provider hand-off), by shape (mutex, VR init) and by strings (-mpmnu setter, pnsdemo.dll, API host).
			"christmas 2018 (rad15_winter, goldmaster 268902)", 0x5C17F6B9, 0x5C17F5CD,
			{ "disable the deadlock watchdog (long level loads trip it and it deliberately crashes the game)",
				0x140D700E7, { 0x76, 0x2E }, { 0xEB, 0x2E }, 2 },
			NO_PATCH, // the lobby flags setter below runs by default on this build
			{ "set the dedicated server startup flags (or dword [rcx+58C0h], 1 -> 6)",
				0x1400CAFAB, { 0x83, 0x89, 0xC0, 0x58, 0x00, 0x00, 0x01 }, { 0x83, 0x89, 0xC0, 0x58, 0x00, 0x00, 0x06 }, 7 },
			{ "load the client data package (r14netclient) in server mode, since r14netserver is not shipped",
				0x1400C3386, { 0x48, 0x8D, 0x15, 0x33, 0x6F, 0xF3, 0x00 }, { 0x48, 0x8D, 0x15, 0x23, 0x6F, 0xF3, 0x00 }, 7 },
			{
				// As on halloween: skip pnsdemo.dll straight to the RAD provider (xor r14d, r14d; jmp 0x1400C6AEE).
				{ "create only the RAD net provider in dedicated mode (no pnsdemo.dll, which is not shipped, and no Oculus)",
					0x1400C6A78, { 0x4C, 0x8D, 0x05, 0x91, 0x3F, 0xF3, 0x00, 0x33 }, { 0x45, 0x33, 0xF6, 0xE9, 0x6E, 0x00, 0x00, 0x00 }, 8 },
				{ "give the dedicated server's NetGame the RAD provider in place of the demo one (mov r8, r14 -> mov r8, r15)",
					0x1400C6BA3, { 0x4D, 0x8B, 0xC6 }, { 0x4D, 0x8B, 0xC7 }, 3 },
			},
			{ "skip Oculus/VR initialization", 0x140D917D1, { 0x74, 0x20 }, { 0xEB, 0x20 }, 2 },
			{ "silence 'Failed to get Oculus session status' (logged every frame without VR)",
				0x1402FE9FD, { 0x74, 0x1E }, { 0xEB, 0x1E }, 2 },
			{ "disable audio, as -noaudio would (always clear the audio flag bit while parsing the command line)",
				0x140306D96, { 0x74, 0x07 }, { 0x90, 0x90 }, 2 },
			{ "run with no graphics or audio, as -headless does on later builds (and dword [rbx+1D4h], 0FFFEFEFCh)",
				0x140306D8A,
				{ 0x41, 0x8B, 0xCF, 0x48, 0x83, 0xF8, 0xFF, 0x0F, 0x95, 0xC1, 0x85, 0xC9, 0x74, 0x07, 0x83, 0xA3, 0xD4, 0x01, 0x00, 0x00, 0xFD },
				{ 0x81, 0xA3, 0xD4, 0x01, 0x00, 0x00, 0xFC, 0xFE, 0xFE, 0xFF, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 21 },
			{ "skip the single-instance mutex check", 0x140D916D5, { 0x74, 0x58 }, { 0xEB, 0x58 }, 2 },
			{
				// Unlike halloween, this build also requires a second value (the Oculus user proof's nonce) before logging in,
				// on both the first and the retry path.
				{ "send SNSLoginRequest without an Oculus user proof", 0xF741, { 0x74, 0x64 }, { 0x90, 0x90 }, 2 },
				{ "send SNSLoginRequest without an Oculus access token", 0xF74A, { 0x74, 0x5B }, { 0x90, 0x90 }, 2 },
				{ "send SNSLoginRequest without an Oculus user proof (retry path)", 0x11F55, { 0x0F, 0x84, 0x22, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 },
				{ "send SNSLoginRequest without an Oculus access token (retry path)", 0x11F62, { 0x0F, 0x84, 0x15, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 },
				{ "skip the Oculus entitlement check", 0x12874, { 0x75, 0x21 }, { 0xEB, 0x21 }, 2 },
				// Played without Revive (like the christmas 2017 build), the Oculus user proof fails and the provider posts a
				// login failure ("Unknown login error") without connecting; take the success path, with an empty nonce.
				{ "log in even though ovr_User_GetUserProof failed", 0xB8E3,
					{ 0x0F, 0x84, 0xB3, 0x00, 0x00, 0x00 }, { 0xE9, 0xB4, 0x00, 0x00, 0x00, 0x90 }, 6 },
				{ "log in with an empty nonce (lea rax, [\"\"] in place of ovr_UserProof_GetNonce(ovr_Message_GetUserProof(msg)))", 0xBAA4,
					{ 0x48, 0x8B, 0xCB, 0xFF, 0x15, 0x0B, 0x0B, 0x05, 0x00, 0x48, 0x8B, 0xC8, 0xFF, 0x15, 0x6A, 0x0B, 0x05, 0x00 },
					{ 0x48, 0x8D, 0x05, 0xAD, 0x47, 0x05, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 18 },
				{ "keep the org-scoped id when ovr_User_GetOrgScopedID fails", 0xA51C,
					{ 0x48, 0xC7, 0x05, 0x91, 0x11, 0x10, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
					{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 11 },
			},
			0x141156AD0,                  // "https://api.readyatdawn.com"
			{ 0x1408FFA6A, 0x1408FFB4B }, // lea rdx, [host] for status/services and status/news
			TRUE,  // parties and friends through EchoRelay, as on halloween
			FALSE,
			FALSE,
			0x10B6B8,                     // no Revive: the player's own id (from their display name)
			0x141038870,                  // frame timer vtable
			// Echo Combat was a paid unlock (sku unlock_echo_combat) in this build. The "owns item" check asks the Oculus
			// store (not available) unless the client settings say isdev; players got a purchase prompt that fails
			// ("iap_failure") and no Combat. Combat, the only in-app purchase, is owned by everyone instead.
			{ "treat in-app purchases (Echo Combat) as owned", 0x1408FE6B0,
				{ 0x48, 0x89, 0x54, 0x24, 0x10, 0x53 },
				{ 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }, 6 }, // mov eax, 1; ret
		},
	};

	/// <summary>
	/// The build we are running in, or NULL if this is not a supported lobby build.
	/// </summary>
	static const BuildProfile* g_build = NULL;

	static FILE* g_log = NULL;
	static BOOL g_patchPnsOvr = TRUE;
	static BOOL g_echoRelaySocial = TRUE;
	static PVOID g_dllNotificationCookie = NULL;

	static VOID Log(const CHAR* format, ...)
	{
		if (g_log == NULL)
		{
			CHAR path[MAX_PATH];
			GetModuleFileNameA(NULL, path, MAX_PATH);
			CHAR* slash = strrchr(path, '\\');
			if (slash != NULL)
				*(slash + 1) = 0;
			strcat_s(path, "echorelay_patch.log");
			g_log = _fsopen(path, "a", _SH_DENYNO);
			if (g_log == NULL)
				return;
		}
		SYSTEMTIME t;
		GetLocalTime(&t);
		fprintf(g_log, "[%02d:%02d:%02d.%03d] [pid %lu] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
		va_list args;
		va_start(args, format);
		vfprintf(g_log, format, args);
		va_end(args);
		fputc('\n', g_log);
		fflush(g_log);
	}

	static DWORD GetTimestamp(BYTE* base)
	{
		IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
		IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
		return nt->FileHeader.TimeDateStamp;
	}

	/// <summary>
	/// Finds the profile for the build hosting us, or NULL if it is not a lobby build we support.
	/// </summary>
	static const BuildProfile* DetectBuild()
	{
		DWORD timestamp = GetTimestamp((BYTE*)GetModuleHandleA(NULL));
		for (const BuildProfile& build : BUILDS)
		{
			if (build.executableTimestamp == timestamp)
				return &build;
		}
		return NULL;
	}

	BOOL IsLobbyBuild()
	{
		return DetectBuild() != NULL;
	}

	/// <summary>
	/// Applies a byte patch if the target holds the original bytes (or already holds the patched bytes, e.g. a pre-patched file).
	/// </summary>
	static BOOL ApplyPatch(BYTE* target, const BytePatch& patch)
	{
		if (memcmp(target, patch.patched, patch.size) == 0)
		{
			Log("Already patched: %s", patch.description);
			return TRUE;
		}
		if (memcmp(target, patch.original, patch.size) != 0)
		{
			Log("SKIPPED (unexpected bytes at %p): %s", target, patch.description);
			return FALSE;
		}
		DWORD oldProtect;
		if (!VirtualProtect(target, patch.size, PAGE_EXECUTE_READWRITE, &oldProtect))
		{
			Log("FAILED (VirtualProtect error %lu): %s", GetLastError(), patch.description);
			return FALSE;
		}
		memcpy(target, patch.patched, patch.size);
		VirtualProtect(target, patch.size, oldProtect, &oldProtect);
		FlushInstructionCache(GetCurrentProcess(), target, patch.size);
		Log("Patched: %s", patch.description);
		return TRUE;
	}

	static BOOL ApplyGamePatch(const BytePatch& patch, const CHAR* what)
	{
		if (patch.size == 0)
		{
			Log("NOT SUPPORTED on the %s build, skipped: %s", g_build->name, what);
			return FALSE;
		}
		BYTE* base = (BYTE*)GetModuleHandleA(NULL);
		return ApplyPatch(base + (patch.address - 0x140000000), patch);
	}

	static BOOL HasFlag(const WCHAR* commandLine, const WCHAR* flag);

	static VOID PatchPnsOvr(BYTE* base)
	{
		if (GetTimestamp(base) != g_build->pnsOvrTimestamp)
		{
			Log("pnsovr.dll is not the %s build's, not patching it", g_build->name);
			return;
		}
		for (const BytePatch& patch : g_build->pnsOvr)
		{
			if (patch.size != 0)
				ApplyPatch(base + patch.address, patch);
		}
		if (g_build->pnsOvrOrgScopedId != 0)
			XmasPatches::GiveInstallIdentity(base, g_build->pnsOvrOrgScopedId, HasFlag(GetCommandLineW(), L"-server"));
	}

	// --------------------------------------------------------------------------------------------------------
	// DLL load notifications (pnsovr.dll is loaded later, by the game's plugin loader).
	// --------------------------------------------------------------------------------------------------------
	typedef struct _LDR_DLL_LOADED_NOTIFICATION_DATA {
		ULONG Flags;
		PCUNICODE_STRING FullDllName;
		PCUNICODE_STRING BaseDllName;
		PVOID DllBase;
		ULONG SizeOfImage;
	} LDR_DLL_LOADED_NOTIFICATION_DATA;
	typedef VOID(CALLBACK* LdrDllNotificationFunc)(ULONG reason, const LDR_DLL_LOADED_NOTIFICATION_DATA* data, PVOID context);
	typedef NTSTATUS(NTAPI* LdrRegisterDllNotificationFunc)(ULONG flags, LdrDllNotificationFunc callback, PVOID context, PVOID* cookie);
	const ULONG LDR_DLL_NOTIFICATION_REASON_LOADED = 1;

	static VOID CALLBACK OnDllNotification(ULONG reason, const LDR_DLL_LOADED_NOTIFICATION_DATA* data, PVOID context)
	{
		if (reason != LDR_DLL_NOTIFICATION_REASON_LOADED || data == NULL || data->BaseDllName == NULL)
			return;
		const UNICODE_STRING* name = data->BaseDllName;
		if (name->Length == wcslen(L"pnsovr.dll") * sizeof(WCHAR) && _wcsnicmp(name->Buffer, L"pnsovr.dll", name->Length / sizeof(WCHAR)) == 0)
		{
			Log("pnsovr.dll loaded at %p", data->DllBase);
			// Always, even with -oculusauth: no EchoRelay server should ever get a player's Oculus access token.
			XmasPatches::ProtectAccessToken((BYTE*)data->DllBase);
			if (g_patchPnsOvr)
				PatchPnsOvr((BYTE*)data->DllBase);
			if (g_echoRelaySocial)
				SummerSocial::HookPnsOvrModule((HMODULE)data->DllBase);
		}
		// The Oculus Platform SDK loader pnsovr.dll imports: its parties/friends calls are answered by EchoRelay.
		if (g_echoRelaySocial && name->Length == wcslen(L"LibOVRPlatform64_1.dll") * sizeof(WCHAR) && _wcsnicmp(name->Buffer, L"LibOVRPlatform64_1.dll", name->Length / sizeof(WCHAR)) == 0)
			SummerSocial::HookPlatformModule((HMODULE)data->DllBase, L"LibOVRPlatform64_1.dll");
	}

	// --------------------------------------------------------------------------------------------------------
	// Service status / news host redirect.
	// The summer build polls https://api.readyatdawn.com/status/services and /status/news (hard coded, long dead)
	// for the menu's service status and the lobby news board. We point both requests at apiservice_host instead.
	// --------------------------------------------------------------------------------------------------------
	static const UINT64 STATUS_HOST_STRING = 0x141248C28; // "https://api.readyatdawn.com"
	static const UINT64 STATUS_HOST_LEAS[] = {
		0x1405F0E4A, // lea rdx, [host] for status/services?env=%s&projectid=rad14
		0x1405F0F2B, // lea rdx, [host] for status/news?env=%s&projectid=rad14
	};

	/// <summary>
	/// Extracts a string value for a key from a flat JSON document (enough for _local/config.json).
	/// </summary>
	static std::string JsonGetString(const std::string& json, const std::string& key)
	{
		size_t pos = json.find("\"" + key + "\"");
		if (pos == std::string::npos || (pos = json.find(':', pos)) == std::string::npos || (pos = json.find('"', pos)) == std::string::npos)
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

	/// <summary>
	/// Determines the HTTP API host from _local/config.json (two directories above bin\win7): apiservice_host, or
	/// failing that http://&lt;loginservice host&gt;/api.
	/// </summary>
	static std::string GetApiHost()
	{
		CHAR path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
		{
			size_t slash = root.find_last_of('\\');
			if (slash != std::string::npos)
				root = root.substr(0, slash);
		}
		std::string config;
		FILE* f = NULL;
		if (fopen_s(&f, (root + "\\_local\\config.json").c_str(), "rb") == 0 && f != NULL)
		{
			CHAR buf[4096];
			size_t n;
			while ((n = fread(buf, 1, sizeof(buf), f)) > 0)
				config.append(buf, n);
			fclose(f);
		}
		std::string host = JsonGetString(config, "apiservice_host");
		if (host.empty())
		{
			std::string login = JsonGetString(config, "loginservice_host");
			size_t scheme = login.find("://");
			if (scheme != std::string::npos)
			{
				size_t pathStart = login.find('/', scheme + 3);
				std::string authority = login.substr(scheme + 3, pathStart == std::string::npos ? std::string::npos : pathStart - scheme - 3);
				host = (login.rfind("wss", 0) == 0 ? "https://" : "http://") + authority + "/api";
			}
		}
		while (!host.empty() && host.back() == '/')
			host.pop_back();
		// The game prepends "HTTP://" itself (always plain HTTP), so pass the host without a scheme.
		size_t scheme = host.find("://");
		if (scheme != std::string::npos)
			host = host.substr(scheme + 3);
		return host;
	}

	/// <summary>
	/// Allocates memory within rel32 reach of the game image, so rip-relative instructions can point at it.
	/// </summary>
	static BYTE* AllocateNearImage(SIZE_T size)
	{
		UINT64 base = (UINT64)GetModuleHandleA(NULL);
		for (UINT64 offset = 0x10000; offset < 0x70000000; offset += 0x10000)
		{
			BYTE* p = (BYTE*)VirtualAlloc((LPVOID)(base - offset), size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
			if (p != NULL)
				return p;
		}
		return NULL;
	}

	static VOID RedirectStatusHost()
	{
		std::string host = GetApiHost();
		if (host.empty())
		{
			Log("No apiservice_host/loginservice_host in _local\\config.json, service status and news stay on api.readyatdawn.com");
			return;
		}
		if (g_build->statusHostString == 0)
		{
			Log("NOT SUPPORTED on the %s build, skipped: service status and news host redirect", g_build->name);
			return;
		}
		BYTE* image = (BYTE*)GetModuleHandleA(NULL);
		BYTE* hostCopy = AllocateNearImage(host.size() + 1);
		if (hostCopy == NULL)
		{
			Log("FAILED to allocate memory near the image for the status host");
			return;
		}
		memcpy(hostCopy, host.c_str(), host.size() + 1);

		for (UINT64 leaAddress : g_build->statusHostLeas)
		{
			BYTE* lea = image + (leaAddress - 0x140000000);
			INT32 displacement;
			memcpy(&displacement, lea + 3, 4);
			if (lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x15 || (UINT64)(lea + 7 + displacement) != (UINT64)image + (g_build->statusHostString - 0x140000000))
			{
				Log("SKIPPED (unexpected instruction at %p): redirect service status/news host", lea);
				continue;
			}
			INT32 newDisplacement = (INT32)((INT64)hostCopy - (INT64)(lea + 7));
			DWORD oldProtect;
			if (VirtualProtect(lea + 3, 4, PAGE_EXECUTE_READWRITE, &oldProtect))
			{
				memcpy(lea + 3, &newDisplacement, 4);
				VirtualProtect(lea + 3, 4, oldProtect, &oldProtect);
				FlushInstructionCache(GetCurrentProcess(), lea, 7);
			}
		}
		Log("Patched: service status and news host -> %s", host.c_str());
	}

	static BOOL HasFlag(const WCHAR* commandLine, const WCHAR* flag)
	{
		// Match the flag as a whole argument (followed by whitespace or the end of the command line).
		SIZE_T length = wcslen(flag);
		for (const WCHAR* p = commandLine; (p = wcsstr(p, flag)) != NULL; p += length)
		{
			WCHAR next = p[length];
			BOOL startOk = p == commandLine || p[-1] == L' ' || p[-1] == L'\t' || p[-1] == L'"';
			if (startOk && (next == 0 || next == L' ' || next == L'\t' || next == L'"'))
				return TRUE;
		}
		return FALSE;
	}

	/// <summary>
	/// Blanks every whole-argument occurrence of a flag in a command line buffer (in place, with spaces).
	/// </summary>
	template <typename CharT>
	static VOID BlankFlag(CharT* commandLine, const CharT* flag, SIZE_T length)
	{
		for (CharT* p = commandLine; *p != 0; p++)
		{
			BOOL startOk = p == commandLine || p[-1] == ' ' || p[-1] == '\t' || p[-1] == '"';
			if (!startOk)
				continue;
			SIZE_T i = 0;
			while (i < length && p[i] == flag[i])
				i++;
			if (i == length && (p[length] == 0 || p[length] == ' ' || p[length] == '\t' || p[length] == '"'))
			{
				for (i = 0; i < length; i++)
					p[i] = ' ';
			}
		}
	}

	/// <summary>
	/// The summer exe exits (code 0, before logging starts) on any command line flag it does not know, so our flags
	/// are removed from the process command line before the game parses it. WinMain loads dbgcore.dll first thing,
	/// and its lpCmdLine points into the same buffer GetCommandLineA returns.
	/// </summary>
	static VOID StripOwnFlags()
	{
		const CHAR* flagsA[] = { "-server", "-noovr", "-multi", "-oculusauth" };
		const WCHAR* flagsW[] = { L"-server", L"-noovr", L"-multi", L"-oculusauth" };
		CHAR* commandLineA = GetCommandLineA();
		WCHAR* commandLineW = GetCommandLineW();
		for (SIZE_T i = 0; i < ARRAYSIZE(flagsA); i++)
		{
			BlankFlag(commandLineA, flagsA[i], strlen(flagsA[i]));
			BlankFlag(commandLineW, flagsW[i], wcslen(flagsW[i]));
		}
	}

	VOID Initialize()
	{
		g_build = DetectBuild();
		if (g_build == NULL)
			return;
		// Voice chat diagnostics (see voiplog.h).
		VoipLog::Install(Log);

		// Remember -headless, then hide it from builds that would exit on it (they get it emulated, where located).
		BOOL headless = HasFlag(GetCommandLineW(), L"-headless");
		BOOL emulatedHeadless = headless && !g_build->supportsHeadless && g_build->headless.size != 0;
		if (headless && !g_build->supportsHeadless)
		{
			BlankFlag(GetCommandLineA(), "-headless", strlen("-headless"));
			BlankFlag(GetCommandLineW(), L"-headless", wcslen(L"-headless"));
		}

		const WCHAR* commandLine = GetCommandLineW();
		BOOL isServer = HasFlag(commandLine, L"-server");
		// With no renderer there is no swap chain to need an Oculus session, so an emulated -headless skips Oculus too.
		BOOL noOvr = (isServer && g_build->serverSkipsOvr) || emulatedHeadless || HasFlag(commandLine, L"-noovr");
		BOOL multi = isServer || HasFlag(commandLine, L"-multi");
		g_patchPnsOvr = !HasFlag(commandLine, L"-oculusauth");
		StripOwnFlags();
		Log("EchoRelay.Patch: %s build detected (server=%d, noovr=%d, multi=%d, pnsovr patches=%d, headless=%d)",
			g_build->name, isServer, noOvr, multi, g_patchPnsOvr, headless);

		if (headless && !g_build->supportsHeadless && !emulatedHeadless)
			Log("NOT SUPPORTED on the %s build: -headless (removed from the command line so the game doesn't exit; running windowed)", g_build->name);
		if (isServer && !noOvr)
			Log("The %s build's renderer needs an Oculus session, so this server initializes Oculus (the runtime must be installed)", g_build->name);

		ApplyGamePatch(g_build->watchdog, "disable the deadlock watchdog");
		if (isServer)
		{
			if (g_build->serverFlagsGate.size != 0)
				ApplyGamePatch(g_build->serverFlagsGate, "run the startup flags block");
			ApplyGamePatch(g_build->serverFlags, "dedicated server startup flags");
			ApplyGamePatch(g_build->serverPackage, "load the client data package in server mode");
			for (const BytePatch& providers : g_build->serverProviders)
				if (providers.size != 0)
					ApplyGamePatch(providers, "dedicated server net providers");
		}
		if (noOvr)
		{
			ApplyGamePatch(g_build->noOvr, "skip Oculus/VR initialization");
			ApplyGamePatch(g_build->noOvrStatus, "silence 'Failed to get Oculus session status'");
		}
		if (multi)
			ApplyGamePatch(g_build->multiInstance, "skip the single-instance mutex check");
		if (emulatedHeadless)
			ApplyGamePatch(g_build->headless, "run with no graphics or audio");
		else if (isServer || headless)
			ApplyGamePatch(g_build->noAudio, "disable audio");
		if (isServer && g_build->frameTimerVtable != 0)
			FrameLimit::Install((VOID**)((BYTE*)GetModuleHandleA(NULL) + (g_build->frameTimerVtable - 0x140000000)), Log);
		RedirectStatusHost();
		if (g_build->ownPurchases.size != 0)
			ApplyGamePatch(g_build->ownPurchases, "own the Echo Combat unlock");

		// Parties and friends through EchoRelay instead of Oculus (unless -oculussocial). Not needed on dedicated servers.
		g_echoRelaySocial = g_build->socialSupported && !isServer && !HasFlag(commandLine, L"-oculussocial");
		if (!g_build->socialSupported)
			Log("NOT SUPPORTED on the %s build, skipped: parties and friends through EchoRelay", g_build->name);
		SummerSocial::SetLogger(Log);
		if (HasFlag(commandLine, L"-socialtrace"))
			SummerSocial::EnableTracing();
		if (g_echoRelaySocial)
		{
			// Builds played without Revive log in with the player's display name account (see GiveInstallIdentity). Parties
			// must use that id too: with the Oculus app signed in, the platform's own user id (the real Oculus one) went to
			// the party service instead, so the game never saw itself as its party's owner and left and re-created its
			// party forever ("transitioning").
			if (g_build->pnsOvrOrgScopedId != 0 && g_patchPnsOvr)
			{
				UINT64 userId = XmasPatches::PlayerUserId(FALSE);
				SummerSocial::SetLocalUserId(userId);
				Log("Parties use this player's id %llu (the same as their login)", (unsigned long long)userId);
			}
			HMODULE platform = GetModuleHandleA("LibOVRPlatform64_1.dll");
			if (platform != NULL)
				SummerSocial::HookPlatformModule(platform, L"LibOVRPlatform64_1.dll");
		}

		{
			// Patch pnsovr.dll now if it is already loaded, and whenever it gets loaded (its access token protection always).
			HMODULE pnsovr = GetModuleHandleA("pnsovr.dll");
			if (pnsovr != NULL)
				XmasPatches::ProtectAccessToken((BYTE*)pnsovr);
			if (pnsovr != NULL && g_patchPnsOvr)
				PatchPnsOvr((BYTE*)pnsovr);
			LdrRegisterDllNotificationFunc registerNotification = (LdrRegisterDllNotificationFunc)GetProcAddress(GetModuleHandleA("ntdll.dll"), "LdrRegisterDllNotification");
			if (registerNotification != NULL)
				registerNotification(0, OnDllNotification, NULL, &g_dllNotificationCookie);
		}
	}
}
