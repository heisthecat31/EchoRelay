#define _CRT_RAND_S
#include <stdlib.h>
// First: it brings in winsock2.h, which must precede xmaspatches.h's windows.h (and its winsock.h).
#include "summersocial.h"
#include "xmaspatches.h"
#include "voiplog.h"
#include <winternl.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>
#include <share.h>
#include <string>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")

namespace XmasPatches
{
	/// <summary>
	/// A byte patch at an RVA of a module.
	/// </summary>
	struct BytePatch
	{
		const CHAR* description;
		DWORD rva;
		BYTE original[16];
		BYTE patched[16];
		SIZE_T size;
	};

	// ----------------------------------------------------------------------------------------------------------------
	// EchoArena.exe (image base 0x140000000; RVAs)
	// ----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// -server: dedicated server mode. The -mpmnu branch of the command line parser sets bit 59 of the option flags ("boot
	/// into multiplayer") and start-up flag 1 ("stop at the multiplayer menu"). Replacing 1 with 2 boots straight into
	/// multiplayer (the start-up code starts it when bit 59 is set and flag 1 isn't) as a dedicated server (flag 2 makes
	/// NetGame load pnsradgameserver). Unlike the lobby builds (6), flag 4 must stay clear: in this build it selects the
	/// demo net service provider (pnsdemo.dll, not shipped) instead of pnsovr.
	/// </summary>
	static const BytePatch SERVER_FLAGS = {
		"set the dedicated server start-up flags (or dword [rbx+4540h], 1 -> 6)", 0x9A667,
		{ 0x83, 0x8B, 0x40, 0x45, 0x00, 0x00, 0x01 }, { 0x83, 0x8B, 0x40, 0x45, 0x00, 0x00, 0x06 }, 7 };

	/// <summary>
	/// -server: start-up flag 4 makes NetGame create the server lobby instead of the client lobby, but it also makes the game
	/// load the pnsdemo platform provider (not shipped) instead of pnsovr. Servers use only the RAD provider (EchoRelay), like
	/// the halloween build's: pnsovr needs the Oculus platform runtime, which a plain server machine doesn't have. The
	/// dedicated branch's pnsdemo load becomes 'xor esi, esi; xor r15d, r15d; jmp' to the pnsrad load.
	/// </summary>
	static const BytePatch SERVER_RAD_ONLY = {
		"create only the RAD net provider in dedicated mode (no pnsdemo.dll, which is not shipped, and no Oculus)", 0x97FBB,
		{ 0x45, 0x33, 0xC0, 0x48, 0x8D, 0x15, 0x43, 0x44, 0xAC, 0x00 }, { 0x31, 0xF6, 0x45, 0x31, 0xFF, 0xE9, 0x31, 0x00, 0x00, 0x00 }, 10 };

	/// <summary>
	/// -server: NetGame takes the platform (r15) and social (rsi) providers besides the RAD one (r13); give it the RAD provider
	/// for all three (mov r9, rsi; mov r8, r15 -> mov r9, r13; mov r8, r13). rsi is cleared right after, r15 isn't used again.
	/// </summary>
	static const BytePatch SERVER_NETGAME_RAD = {
		"give the dedicated server's NetGame the RAD provider in place of the Oculus/demo ones (r8, r9 <- r13)", 0x98097,
		{ 0x4C, 0x8B, 0xCE, 0x4D, 0x8B, 0xC7 }, { 0x4D, 0x8B, 0xCD, 0x4D, 0x8B, 0xC5 }, 6 };

	/// <summary>
	/// -server: never start an Oculus VR session (as -novr does): a server machine has no headset or Oculus runtime, and the
	/// session failure (-3001) is fatal. Always take the branch that sets the -novr option flag.
	/// </summary>
	static const BytePatch SERVER_NO_VR = {
		"skip Oculus/VR initialization, as -novr does (je -> nop)", 0x9A356, { 0x74, 0x11 }, { 0x90, 0x90 }, 2 };

	/// <summary>
	/// -server: the start-up code also sets start-up flag 1 when it picks multiplayer (Echo Arena rather than Lone Echo);
	/// clear it here too, or the server stops at the multiplayer menu instead of starting multiplayer.
	/// </summary>
	static const BytePatch SERVER_NO_MENU = {
		"don't stop the server at the multiplayer menu (or dword [rcx+4540h], 1 -> 0)", 0x9CA81,
		{ 0x83, 0x89, 0x40, 0x45, 0x00, 0x00, 0x01 }, { 0x83, 0x89, 0x40, 0x45, 0x00, 0x00, 0x00 }, 7 };

	/// <summary>
	/// -server / -headless: disable audio, as -noaudio would (always take the branch that sets option flag 0x8000).
	/// </summary>
	static const BytePatch NO_AUDIO = {
		"disable audio, as -noaudio would (je -> nop)", 0x2E8D12,
		{ 0x74, 0x0B }, { 0x90, 0x90 }, 2 };

	/// <summary>
	/// -server: the r14netserver data package isn't shipped; servers load r14netclient (16 bytes before it) instead.
	/// </summary>
	static const BytePatch SERVER_PACKAGE = {
		"load the client data package (r14netclient) in server mode, since r14netserver is not shipped", 0x9501C,
		{ 0x48, 0x8D, 0x15, 0xCD, 0x71, 0xAC, 0x00 }, { 0x48, 0x8D, 0x15, 0xBD, 0x71, 0xAC, 0x00 }, 7 };

	/// <summary>
	/// -server: the renderer's adapter list skips the Microsoft Basic Render Driver (vendor 0x1414, device 0x8C), so on a
	/// machine without a GPU it finds none ("No valid DXGI adapters found!"). Keep it: a device on it is WARP, Windows'
	/// software renderer, which is plenty for a server that draws nothing. A machine with a GPU still lists that first.
	/// </summary>
	static const BytePatch SERVER_BASIC_RENDER_ADAPTER = {
		"keep the Microsoft Basic Render Driver adapter, so a server without a GPU renders with WARP (je -> nop)", 0x10D107,
		{ 0x74, 0x40 }, { 0x90, 0x90 }, 2 };

	/// <summary>
	/// The renderer's D3D11CreateDevice import thunk (jmp qword ptr [rip+x]); its only caller creates the game's device.
	/// </summary>
	static const DWORD D3D11_CREATE_DEVICE_THUNK = 0x8C6224;

	// ----------------------------------------------------------------------------------------------------------------
	// pnsovr.dll (the Oculus platform provider)
	// ----------------------------------------------------------------------------------------------------------------
	static const DWORD PNSOVR_TIMESTAMP = 0x5A394933;

	/// <summary>
	/// Without the Oculus store launch, the entitlement check fails and the provider (and the game) refuse to start. The
	/// provider has a "skipentitlement" option read from packaged config; this makes it always take that branch.
	/// </summary>
	static const BytePatch PNSOVR_SKIP_ENTITLEMENT = {
		"skip the Oculus entitlement check", 0x12F4E, { 0x75, 0x4F }, { 0xEB, 0x4F }, 2 };

	/// <summary>
	/// The provider's failure handlers for ovr_User_GetOrgScopedID reset the logged in user's org-scoped id to -1; the
	/// Oculus request fails for launches outside the Oculus store, and must not clear the id we provide.
	/// </summary>
	static const BytePatch PNSOVR_KEEP_ORG_ID[] = {
		{ "keep the org-scoped id when ovr_User_GetOrgScopedID fails (login)", 0xB20C,
			{ 0x48, 0xC7, 0x05, 0x21, 0xC7, 0x06, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
			{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 11 },
		{ "keep the org-scoped id when ovr_User_GetOrgScopedID fails (refresh)", 0xC95C,
			{ 0x48, 0xC7, 0x05, 0xD1, 0xAF, 0x06, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
			{ 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 11 },
	};

	/// <summary>
	/// Logging in first asks Oculus for a user proof (a signed nonce), which fails outside the Oculus store; the provider then
	/// reports a login failure without ever connecting. These take the success path, with an empty nonce.
	/// </summary>
	static const BytePatch PNSOVR_SKIP_USER_PROOF[] = {
		{ "log in even though ovr_User_GetUserProof failed", 0xCD54,
			{ 0x0F, 0x84, 0x9D, 0x00, 0x00, 0x00 }, { 0xE9, 0x9E, 0x00, 0x00, 0x00, 0x90 }, 6 },
		// ovr_Message_GetUserProof + ovr_UserProof_GetNonce -> lea rax, [empty string at RVA 0x52618]
		{ "log in with an empty nonce", 0xCE5E,
			{ 0xFF, 0x15, 0xB4, 0x25, 0x04, 0x00, 0x48, 0x8B, 0xC8, 0xFF, 0x15, 0x03, 0x26, 0x04, 0x00 },
			{ 0x48, 0x8D, 0x05, 0xB3, 0x57, 0x04, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 15 },
	};

	/// <summary>
	/// The provider's logged in user org-scoped id (0 or -1 until known), used for logging in.
	/// </summary>
	static const DWORD PNSOVR_ORG_SCOPED_ID = 0x77938;

	/// <summary>
	/// The shared id Revive (LibRevive64 / Gammon) reports, never used as an install's id.
	/// </summary>
	static const UINT64 REVIVE_SHARED_ID = 0xB400B0C9;

	static FILE* g_log = NULL;
	static UINT64 g_userId = 0;
	static PVOID g_dllNotificationCookie = NULL;
	static BOOL g_echoRelaySocial = FALSE;

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

	BOOL IsXmasBuild()
	{
		return GetTimestamp((BYTE*)GetModuleHandleA(NULL)) == EXECUTABLE_TIMESTAMP;
	}

	static BOOL WriteCode(BYTE* target, const BYTE* bytes, SIZE_T size)
	{
		DWORD oldProtect;
		if (!VirtualProtect(target, size, PAGE_EXECUTE_READWRITE, &oldProtect))
			return FALSE;
		memcpy(target, bytes, size);
		VirtualProtect(target, size, oldProtect, &oldProtect);
		FlushInstructionCache(GetCurrentProcess(), target, size);
		return TRUE;
	}

	/// <summary>
	/// Applies a byte patch if the target holds the original bytes (or already holds the patched bytes).
	/// </summary>
	static BOOL ApplyPatch(BYTE* base, const BytePatch& patch)
	{
		BYTE* target = base + patch.rva;
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
		if (!WriteCode(target, patch.patched, patch.size))
		{
			Log("FAILED (VirtualProtect error %lu): %s", GetLastError(), patch.description);
			return FALSE;
		}
		Log("Patched: %s", patch.description);
		return TRUE;
	}

	/// <summary>
	/// Finds the import address table slot of an imported function in a loaded module.
	/// </summary>
	static BYTE* FindImportSlot(BYTE* base, const CHAR* dllName, const CHAR* functionName)
	{
		IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
		IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
		for (IMAGE_IMPORT_DESCRIPTOR* desc = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); desc->Name != 0; desc++)
		{
			if (_stricmp((const CHAR*)(base + desc->Name), dllName) != 0)
				continue;
			IMAGE_THUNK_DATA64* names = (IMAGE_THUNK_DATA64*)(base + (desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk));
			for (SIZE_T i = 0; names[i].u1.AddressOfData != 0; i++)
			{
				if (IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal))
					continue;
				IMAGE_IMPORT_BY_NAME* byName = (IMAGE_IMPORT_BY_NAME*)(base + names[i].u1.AddressOfData);
				if (strcmp((const CHAR*)byName->Name, functionName) == 0)
					return base + desc->FirstThunk + i * sizeof(IMAGE_THUNK_DATA64);
			}
		}
		return NULL;
	}

	/// <summary>
	/// Redirects every "call qword ptr [rip+x]" through an imported function's IAT slot to a stub (call rel32 + nop), so it
	/// works however many call sites there are and whether or not the imports are bound yet.
	/// </summary>
	static INT RedirectImportCalls(BYTE* base, const CHAR* dllName, const CHAR* functionName, BYTE* stub)
	{
		BYTE* slot = FindImportSlot(base, dllName, functionName);
		if (slot == NULL)
			return 0;
		IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
		IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(nt);
		INT count = 0;
		for (WORD s = 0; s < nt->FileHeader.NumberOfSections; s++, section++)
		{
			if (!(section->Characteristics & IMAGE_SCN_MEM_EXECUTE))
				continue;
			BYTE* start = base + section->VirtualAddress;
			BYTE* end = start + section->Misc.VirtualSize - 6;
			for (BYTE* p = start; p < end; p++)
			{
				if (p[0] != 0xFF || p[1] != 0x15)
					continue;
				INT32 disp;
				memcpy(&disp, p + 2, 4);
				if (p + 6 + disp != slot)
					continue;
				BYTE patch[6] = { 0xE8, 0, 0, 0, 0, 0x90 };
				INT32 rel = (INT32)((INT64)stub - (INT64)(p + 5));
				memcpy(patch + 1, &rel, 4);
				if (WriteCode(p, patch, sizeof(patch)))
					count++;
			}
		}
		return count;
	}

	/// <summary>
	/// Allocates executable memory within rel32 reach of a module, for call stubs.
	/// </summary>
	static BYTE* AllocateNear(BYTE* moduleBase, SIZE_T size)
	{
		for (UINT64 offset = 0x10000; offset < 0x70000000; offset += 0x10000)
		{
			BYTE* p = (BYTE*)VirtualAlloc((LPVOID)((UINT64)moduleBase - offset), size, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
			if (p != NULL)
				return p;
		}
		return NULL;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// API host
	// ----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The instructions that load the hardcoded API host, "https://api.readyatdawn.com" (lea rdx, [rip+x]; 7 bytes). The
	/// first is the dedicated server's status check ({api}/status/serverdb?env=...&projectid=rad14), which must
	/// report available before a server logs in.
	/// </summary>
	static const DWORD API_HOST_LEAS[] = { 0x3CC2EA, 0x3D5C1D };
	static const DWORD API_HOST_STRING = 0xBB2790;

	static std::string GetGameRoot();

	/// <summary>
	/// Reads a string value from _local\config.json (flat keys only; enough for the *_host keys).
	/// </summary>
	static std::string GetConfigString(const CHAR* key)
	{
		std::string path = GetGameRoot() + "\\_local\\config.json";
		FILE* f = NULL;
		if (fopen_s(&f, path.c_str(), "rb") != 0 || f == NULL)
			return "";
		std::string json;
		CHAR buffer[4096];
		SIZE_T read;
		while ((read = fread(buffer, 1, sizeof(buffer), f)) > 0)
			json.append(buffer, read);
		fclose(f);

		size_t at = json.find(std::string("\"") + key + "\"");
		if (at == std::string::npos)
			return "";
		at = json.find(':', at);
		if (at == std::string::npos)
			return "";
		at = json.find('"', at);
		if (at == std::string::npos)
			return "";
		size_t end = json.find('"', at + 1);
		return end == std::string::npos ? "" : json.substr(at + 1, end - at - 1);
	}

	/// <summary>
	/// Points the game's API requests at apiservice_host (or api_host) from the config, if set.
	/// </summary>
	static VOID RedirectApiHost(BYTE* exe)
	{
		std::string host = GetConfigString("apiservice_host");
		if (host.empty())
			host = GetConfigString("api_host");
		if (host.empty())
			return;
		// The game requests "http://" + host + "/" + path, and only strips an "https://" prefix itself (which also makes
		// the request secure). So a plain http host goes in without its scheme or a trailing slash: "127.0.0.1:777/api".
		if (host.rfind("http://", 0) == 0)
			host = host.substr(7);
		while (!host.empty() && host.back() == '/')
			host.pop_back();
		if (host.empty())
			return;

		BYTE* copy = AllocateNear(exe, host.size() + 1);
		if (copy == NULL)
		{
			Log("Could not allocate the API host string");
			return;
		}
		memcpy(copy, host.c_str(), host.size() + 1);
		DWORD oldProtect;
		VirtualProtect(copy, host.size() + 1, PAGE_READONLY, &oldProtect);

		INT redirected = 0;
		for (DWORD rva : API_HOST_LEAS)
		{
			BYTE* lea = exe + rva;
			if (lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x15 || lea + 7 + *(INT32*)(lea + 3) != exe + API_HOST_STRING)
			{
				Log("Unexpected code at API host reference 0x%X; not redirected", rva);
				continue;
			}
			INT64 displacement = copy - (lea + 7);
			if (displacement != (INT32)displacement)
				continue;
			INT32 disp32 = (INT32)displacement;
			BYTE code[7] = { 0x48, 0x8D, 0x15 };
			memcpy(code + 3, &disp32, 4);
			if (WriteCode(lea, code, 7))
				redirected++;
		}
		Log("API host: %s (%d references)", host.c_str(), redirected);
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Per-install Oculus user id
	// ----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The game's root folder (two directories above bin\win7\EchoArena.exe).
	/// </summary>
	static std::string GetGameRoot()
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
		return root;
	}

	/// <summary>
	/// Reads this install's user id from _local\echorelay_id.txt, creating a random one on first run. Without Revive the
	/// Oculus platform reports no user for launches outside the Oculus store, so each install needs an id of its own
	/// (EchoRelay keys accounts by it, protected by the password in the login URL). It is kept below 2^32, since the
	/// game may truncate ids to 32 bits.
	/// </summary>
	static UINT64 GetInstallUserId(BOOL server)
	{
		// Game servers get an id of their own, so a server and a client run from the same install are separate users.
		std::string path = GetGameRoot() + (server ? "\\_local\\echorelay_server_id.txt" : "\\_local\\echorelay_id.txt");
		FILE* f = NULL;
		if (fopen_s(&f, path.c_str(), "rb") == 0 && f != NULL)
		{
			unsigned long long id = 0;
			int read = fscanf_s(f, "%llu", &id);
			fclose(f);
			if (read == 1 && id != 0 && id != REVIVE_SHARED_ID)
				return id;
		}

		UINT64 id = 0;
		while (id < 1000000000ULL || id == REVIVE_SHARED_ID)
		{
			unsigned int random = 0;
			rand_s(&random);
			id = random;
		}
		if (fopen_s(&f, path.c_str(), "wb") == 0 && f != NULL)
		{
			fprintf(f, "%llu\n", (unsigned long long)id);
			fclose(f);
			Log("Created this install's user id %llu in %s", (unsigned long long)id, path.c_str());
		}
		else
		{
			Log("Could not save the user id to %s; using %llu for this run", path.c_str(), (unsigned long long)id);
		}
		return id;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// pnsovr.dll
	// ----------------------------------------------------------------------------------------------------------------
	static VOID PatchPnsOvr(BYTE* base)
	{
		if (GetTimestamp(base) != PNSOVR_TIMESTAMP)
		{
			Log("pnsovr.dll is not the christmas build's, not patching it");
			return;
		}
		ApplyPatch(base, PNSOVR_SKIP_ENTITLEMENT);
		for (const BytePatch& patch : PNSOVR_KEEP_ORG_ID)
			ApplyPatch(base, patch);
		for (const BytePatch& patch : PNSOVR_SKIP_USER_PROOF)
			ApplyPatch(base, patch);

		// ovr_GetLoggedInUserID() -> our id: redirect each call qword ptr [rip+x] (6 bytes) to call stub (5 bytes) + nop.
		BYTE* stub = AllocateNear(base, 16);
		if (stub == NULL)
		{
			Log("FAILED to allocate the user id stub near pnsovr.dll");
			return;
		}
		stub[0] = 0x48; stub[1] = 0xB8; // mov rax, imm64
		memcpy(stub + 2, &g_userId, 8);
		stub[10] = 0xC3;                // ret
		INT redirected = RedirectImportCalls(base, "LibOVRPlatform64_1.dll", "ovr_GetLoggedInUserID", stub);
		Log("Patched: %d ovr_GetLoggedInUserID calls return %llu", redirected, (unsigned long long)g_userId);

		// The org-scoped id the provider logs in with (normally filled in by ovr_User_GetOrgScopedID).
		*(UINT64*)(base + PNSOVR_ORG_SCOPED_ID) = g_userId;
		Log("Set the logged in user org-scoped id to %llu", (unsigned long long)g_userId);
	}

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
			PatchPnsOvr((BYTE*)data->DllBase);
			if (g_echoRelaySocial)
				SummerSocial::HookPnsOvrModule((HMODULE)data->DllBase);
		}
		// The Oculus Platform SDK pnsovr.dll imports: its parties (rooms) and friends calls are answered by EchoRelay.
		else if (g_echoRelaySocial && name->Length == wcslen(L"LibOVRPlatform64_1.dll") * sizeof(WCHAR) &&
			_wcsnicmp(name->Buffer, L"LibOVRPlatform64_1.dll", name->Length / sizeof(WCHAR)) == 0)
		{
			SummerSocial::HookPlatformModule((HMODULE)data->DllBase, L"LibOVRPlatform64_1.dll");
		}
	}

	/// <summary>
	/// Checks for a flag as a whole argument and, if found, overwrites it in place (the replacement must not be longer;
	/// the rest is filled with spaces). This build parses its command line after this library is loaded.
	/// </summary>
	template <typename CharT>
	static BOOL ReplaceFlag(CharT* commandLine, const CharT* flag, const CharT* replacement)
	{
		SIZE_T length = 0, replacementLength = 0;
		while (flag[length]) length++;
		while (replacement[replacementLength]) replacementLength++;
		BOOL found = FALSE;
		for (CharT* p = commandLine; *p != 0; p++)
		{
			BOOL startOk = p == commandLine || p[-1] == ' ' || p[-1] == '	' || p[-1] == '"';
			if (!startOk)
				continue;
			SIZE_T i = 0;
			while (i < length && p[i] == flag[i])
				i++;
			if (i == length && (p[length] == 0 || p[length] == ' ' || p[length] == '	' || p[length] == '"'))
			{
				for (i = 0; i < length; i++)
					p[i] = i < replacementLength ? replacement[i] : ' ';
				found = TRUE;
			}
		}
		return found;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// -headless
	// ----------------------------------------------------------------------------------------------------------------

	// ----------------------------------------------------------------------------------------------------------------
	// Software rendering (WARP) for servers without a GPU
	// ----------------------------------------------------------------------------------------------------------------

	typedef HRESULT(WINAPI* D3D11CreateDeviceFunc)(VOID* adapter, INT driverType, HMODULE software, UINT flags,
		const INT* featureLevels, UINT featureLevelCount, UINT sdkVersion, VOID** device, INT* featureLevel, VOID** context);
	static const INT D3D_DRIVER_TYPE_WARP_ = 5;
	static const UINT D3D11_CREATE_DEVICE_DEBUG_ = 0x2;
	static BOOL g_forceWarp = FALSE;

	static VOID STDMETHODCALLTYPE SkipRenderCommand() {}

	/// <summary>
	/// ID3D11DeviceContext::GetData: every query is done at once. The first dword is 1 and the rest 0 (event: TRUE,
	/// timestamp: 1, disjoint: frequency 1 and not disjoint, occlusion: 1 sample), so nothing waits on the device.
	/// </summary>
	static HRESULT STDMETHODCALLTYPE SkipGetData(VOID*, VOID*, VOID* data, UINT dataSize, UINT)
	{
		if (data != NULL && dataSize > 0)
		{
			memset(data, 0, dataSize);
			if (dataSize >= 4)
				*(UINT32*)data = 1;
			else
				*(BYTE*)data = 1;
		}
		return S_OK;
	}

	/// <summary>
	/// A server draws nothing anyone sees, but the build renders every frame anyway, and in software (WARP) that takes most
	/// of each tick. Turn the immediate context's drawing, clearing and copying into no-ops (ID3D11DeviceContext vtable
	/// indices; all return void). State setting, Map and queries still work, so the game runs as before.
	/// </summary>
	static VOID SkipRendering(VOID* context)
	{
		static const INT SKIPPED[] = {
			12, 13, 20, 21, 38, 39, 40, // DrawIndexed, Draw, DrawIndexedInstanced, DrawInstanced, DrawAuto, Draw*Indirect
			41, 42,                     // Dispatch, DispatchIndirect
			46, 47, 48, 57,             // CopySubresourceRegion, CopyResource, UpdateSubresource, ResolveSubresource
			50, 51, 52, 53, 54,         // Clear RTV/UAV uint/UAV float/DSV, GenerateMips
			58,                         // ExecuteCommandList (deferred contexts' recorded work)
			27, 28, 111,                // Begin, End (queries), Flush
		};
		VOID** vtable = *(VOID***)context;
		auto replace = [vtable](INT index, VOID* function)
		{
			DWORD oldProtect;
			if (VirtualProtect(&vtable[index], sizeof(VOID*), PAGE_READWRITE, &oldProtect))
			{
				vtable[index] = function;
				VirtualProtect(&vtable[index], sizeof(VOID*), oldProtect, &oldProtect);
			}
		};
		for (INT index : SKIPPED)
			replace(index, (VOID*)&SkipRenderCommand);
		replace(29, (VOID*)&SkipGetData); // GetData
		Log("Server: skipping all drawing (the game still runs; nothing is rendered)");
	}

	static HRESULT STDMETHODCALLTYPE SkipPresent(VOID*, UINT, UINT) { return S_OK; }
	static HRESULT STDMETHODCALLTYPE SkipPresent1(VOID*, UINT, UINT, const VOID*) { return S_OK; }

	/// <summary>
	/// Presenting copies the whole back buffer to the (hidden) window every frame, which WARP does on the CPU with a pool of
	/// worker threads. Make IDXGISwapChain::Present / IDXGISwapChain1::Present1 no-ops. All of dxgi's swap chains share one
	/// vtable, so it is taken from a throwaway swap chain on a hidden window, made from the game's own device.
	/// </summary>
	static VOID SkipPresenting(IUnknown* device)
	{
		IDXGIDevice* dxgiDevice = NULL;
		IDXGIAdapter* adapter = NULL;
		IDXGIFactory* factory = NULL;
		IDXGISwapChain* swapChain = NULL;
		HWND window = CreateWindowExA(0, "STATIC", "", WS_POPUP, 0, 0, 64, 64, NULL, NULL, NULL, NULL);
		if (window != NULL && SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), (VOID**)&dxgiDevice))
			&& SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetParent(__uuidof(IDXGIFactory), (VOID**)&factory)))
		{
			DXGI_SWAP_CHAIN_DESC desc = {};
			desc.BufferDesc.Width = 64;
			desc.BufferDesc.Height = 64;
			desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
			desc.SampleDesc.Count = 1;
			desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
			desc.BufferCount = 1;
			desc.OutputWindow = window;
			desc.Windowed = TRUE;
			if (SUCCEEDED(factory->CreateSwapChain(device, &desc, &swapChain)))
			{
				VOID** vtable = *(VOID***)swapChain;
				DWORD oldProtect;
				if (VirtualProtect(&vtable[8], sizeof(VOID*), PAGE_READWRITE, &oldProtect)) // Present
				{
					vtable[8] = (VOID*)&SkipPresent;
					VirtualProtect(&vtable[8], sizeof(VOID*), oldProtect, &oldProtect);
				}
				if (VirtualProtect(&vtable[22], sizeof(VOID*), PAGE_READWRITE, &oldProtect)) // Present1
				{
					vtable[22] = (VOID*)&SkipPresent1;
					VirtualProtect(&vtable[22], sizeof(VOID*), oldProtect, &oldProtect);
				}
				Log("Server: skipping Present (nothing is copied to the window)");
			}
			else
				Log("FAILED (no swap chain to take Present from): skip presenting");
		}
		if (swapChain) swapChain->Release();
		if (factory) factory->Release();
		if (adapter) adapter->Release();
		if (dxgiDevice) dxgiDevice->Release();
		if (window) DestroyWindow(window);
	}

	/// <summary>
	/// The game's D3D11CreateDevice. Tries the game's own request first (unless -warp), then WARP, which needs no GPU.
	/// </summary>
	static HRESULT WINAPI D3D11CreateDeviceHook(VOID* adapter, INT driverType, HMODULE software, UINT flags,
		const INT* featureLevels, UINT featureLevelCount, UINT sdkVersion, VOID** device, INT* featureLevel, VOID** context)
	{
		D3D11CreateDeviceFunc create = (D3D11CreateDeviceFunc)GetProcAddress(LoadLibraryA("d3d11.dll"), "D3D11CreateDevice");
		if (create == NULL)
			return E_FAIL;
		HRESULT result = E_FAIL;
		if (!g_forceWarp)
		{
			result = create(adapter, driverType, software, flags, featureLevels, featureLevelCount, sdkVersion, device, featureLevel, context);
			if (FAILED(result))
				Log("D3D11CreateDevice failed (0x%08lX), retrying with WARP (software rendering)", (unsigned long)result);
		}
		if (g_forceWarp || FAILED(result))
		{
			result = create(NULL, D3D_DRIVER_TYPE_WARP_, NULL, flags & ~D3D11_CREATE_DEVICE_DEBUG_, featureLevels, featureLevelCount,
				sdkVersion, device, featureLevel, context);
			Log("D3D11CreateDevice with WARP (software rendering): 0x%08lX", (unsigned long)result);
		}
		if (SUCCEEDED(result) && context != NULL && *context != NULL)
			SkipRendering(*context);
		if (SUCCEEDED(result) && device != NULL && *device != NULL)
			SkipPresenting((IUnknown*)*device);
		return result;
	}

	/// <summary>
	/// Sends the renderer's D3D11CreateDevice thunk to D3D11CreateDeviceHook (jmp rel32 to an absolute jump near the exe).
	/// </summary>
	static VOID HookCreateDevice(BYTE* exe)
	{
		BYTE* thunk = exe + D3D11_CREATE_DEVICE_THUNK;
		if (thunk[0] != 0xFF || thunk[1] != 0x25)
		{
			Log("SKIPPED (unexpected bytes at %p): fall back to WARP when Direct3D 11 device creation fails", thunk);
			return;
		}
		BYTE* stub = AllocateNear(exe, 16);
		if (stub == NULL)
		{
			Log("FAILED (no memory near the exe): fall back to WARP when Direct3D 11 device creation fails");
			return;
		}
		stub[0] = 0xFF; stub[1] = 0x25; memset(stub + 2, 0, 4); // jmp qword ptr [rip+0]
		UINT64 target = (UINT64)&D3D11CreateDeviceHook;
		memcpy(stub + 6, &target, 8);
		BYTE jump[6] = { 0xE9, 0, 0, 0, 0, 0x90 };
		INT32 rel = (INT32)((INT64)stub - (INT64)(thunk + 5));
		memcpy(jump + 1, &rel, 4);
		if (WriteCode(thunk, jump, sizeof(jump)))
			Log("Patched: %s Direct3D 11 device", g_forceWarp ? "use WARP (software rendering, -warp) for the" : "fall back to WARP (software rendering) if the GPU can't create the");
	}

	/// <summary>
	/// Keeps this process's windows hidden. The christmas build has no -headless (it always creates its renderer and
	/// window), so -headless runs it as -novr with its window hidden and audio off.
	/// </summary>
	static DWORD WINAPI HideWindowsThread(LPVOID)
	{
		for (;;)
		{
			EnumWindows([](HWND window, LPARAM) -> BOOL
			{
				DWORD processId = 0;
				GetWindowThreadProcessId(window, &processId);
				if (processId == GetCurrentProcessId() && IsWindowVisible(window))
					ShowWindow(window, SW_HIDE);
				return TRUE;
			}, 0);
			Sleep(250);
		}
		return 0;
	}

	/// <summary>
	/// The player's id derived from the display name in _local\config.json (loginservice_host, or christmas 2017's
	/// login_host), exactly as EchoRelay derives the account of a player without their own Oculus id
	/// (LoginService.GetSummerAccountId): SHA-256 of "echorelay-summer-account:" + the trimmed, lower-cased name. 0 if the
	/// config has no display name.
	/// </summary>
	static UINT64 GetDisplayNameUserId()
	{
		std::string path = GetGameRoot() + "\\_local\\config.json";
		FILE* f = NULL;
		if (fopen_s(&f, path.c_str(), "rb") != 0 || f == NULL)
			return 0;
		std::string json;
		CHAR buffer[4096];
		SIZE_T read;
		while ((read = fread(buffer, 1, sizeof(buffer), f)) > 0)
			json.append(buffer, read);
		fclose(f);

		std::string name;
		for (const CHAR* key : { "\"loginservice_host\"", "\"login_host\"" })
		{
			size_t at = json.find(key);
			if (at == std::string::npos)
				continue;
			size_t open = json.find('"', json.find(':', at));
			size_t close = open == std::string::npos ? std::string::npos : json.find('"', open + 1);
			if (close == std::string::npos)
				continue;
			std::string url = json.substr(open + 1, close - open - 1);
			size_t param = url.find("displayname=");
			if (param == std::string::npos)
				continue;
			std::string encoded = url.substr(param + 12, url.find('&', param) == std::string::npos ? std::string::npos : url.find('&', param) - param - 12);
			for (size_t i = 0; i < encoded.size(); i++)
			{
				if (encoded[i] == '%' && i + 2 < encoded.size())
				{
					name += (CHAR)strtol(encoded.substr(i + 1, 2).c_str(), NULL, 16);
					i += 2;
				}
				else
					name += encoded[i] == '+' ? ' ' : encoded[i];
			}
			break;
		}
		size_t first = name.find_first_not_of(" \t\r\n"), last = name.find_last_not_of(" \t\r\n");
		name = first == std::string::npos ? "" : name.substr(first, last - first + 1);
		if (name.empty())
			return 0;
		std::string keyText = "echorelay-summer-account:";
		for (CHAR c : name)
			keyText += (c >= 'A' && c <= 'Z') ? (CHAR)(c - 'A' + 'a') : c;

		BYTE hash[32] = {};
		BCRYPT_ALG_HANDLE algorithm = NULL;
		if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0)
			return 0;
		BCryptHash(algorithm, NULL, 0, (PUCHAR)keyText.data(), (ULONG)keyText.size(), hash, sizeof(hash));
		BCryptCloseAlgorithmProvider(algorithm, 0);
		UINT64 id;
		memcpy(&id, hash, 8);
		return (id & 0x3FFFFFFFFFFFFFFFULL) | 0x4000000000000000ULL;
	}

	/// <summary>
	/// The player's own id: from their display name (one account across every build), else this install's own id. Game
	/// servers keep an install id of their own.
	/// </summary>
	static UINT64 GetPlayerUserId(BOOL server)
	{
		UINT64 id = server ? 0 : GetDisplayNameUserId();
		if (id != 0)
		{
			Log("This player's id %llu (from their display name)", (unsigned long long)id);
			return id;
		}
		return GetInstallUserId(server);
	}

	VOID GiveInstallIdentity(BYTE* pnsOvr, DWORD orgScopedIdRva, BOOL server)
	{
		UINT64 userId = GetPlayerUserId(server);
		BYTE* stub = AllocateNear(pnsOvr, 16);
		if (stub == NULL)
		{
			Log("FAILED to allocate the user id stub near pnsovr.dll");
			return;
		}
		stub[0] = 0x48; stub[1] = 0xB8; // mov rax, imm64
		memcpy(stub + 2, &userId, 8);
		stub[10] = 0xC3;                // ret
		INT redirected = RedirectImportCalls(pnsOvr, "LibOVRPlatform64_1.dll", "ovr_GetLoggedInUserID", stub);
		Log("Patched: %d ovr_GetLoggedInUserID calls return this install's id %llu", redirected, (unsigned long long)userId);
		*(UINT64*)(pnsOvr + orgScopedIdRva) = userId;
		Log("Set the logged in user org-scoped id to %llu", (unsigned long long)userId);
	}

	VOID Initialize()
	{
		Log("EchoRelay.Patch: christmas 2017 build (EchoArena.exe) detected");
		// Voice chat diagnostics (see voiplog.h).
		VoipLog::Install(Log);

		// -server: EchoArena.exe doesn't know it; it becomes -mpmnu (whose parser branch we patch into server mode).
		BOOL server = ReplaceFlag(GetCommandLineW(), L"-server", L"-mpmnu");
		ReplaceFlag(GetCommandLineA(), "-server", "-mpmnu");
		// -headless: EchoArena.exe doesn't know it (it becomes -novr), so it's emulated: no audio and a hidden window.
		BOOL headless = ReplaceFlag(GetCommandLineW(), L"-headless", L"-novr");
		ReplaceFlag(GetCommandLineA(), "-headless", "-novr");
		if (headless || server)
			ApplyPatch((BYTE*)GetModuleHandleA(NULL), NO_AUDIO);
		if (headless)
		{
			Log("Headless mode (-headless): running as -novr with no audio and the window hidden");
			CreateThread(NULL, 0, HideWindowsThread, NULL, 0, NULL);
		}
		if (server)
		{
			BYTE* exe = (BYTE*)GetModuleHandleA(NULL);
			Log("Dedicated server mode (-server)");
			ApplyPatch(exe, SERVER_FLAGS);
			ApplyPatch(exe, SERVER_NO_MENU);
			ApplyPatch(exe, SERVER_RAD_ONLY);
			ApplyPatch(exe, SERVER_NETGAME_RAD);
			ApplyPatch(exe, SERVER_NO_VR);
			ApplyPatch(exe, SERVER_PACKAGE);
			// Servers often have no GPU. -warp: always render in software.
			g_forceWarp = ReplaceFlag(GetCommandLineW(), L"-warp", L"     ");
			ReplaceFlag(GetCommandLineA(), "-warp", "     ");
			ApplyPatch(exe, SERVER_BASIC_RENDER_ADAPTER);
			HookCreateDevice(exe);
		}
		RedirectApiHost((BYTE*)GetModuleHandleA(NULL));
		g_userId = GetPlayerUserId(server);
		Log("This install's user id: %llu", (unsigned long long)g_userId);

		// Parties through EchoRelay instead of Oculus rooms (unless -oculussocial). Every player starts in a party of one
		// (a private room it owns); with no Oculus services that room was never created, so players weren't the leader of
		// their own party and got "reserved for party leader". Dedicated servers have no parties.
		g_echoRelaySocial = !server && !ReplaceFlag(GetCommandLineW(), L"-oculussocial", L"             ");
		ReplaceFlag(GetCommandLineA(), "-oculussocial", "             ");
		if (g_echoRelaySocial)
		{
			SummerSocial::SetLogger(Log);
			SummerSocial::SetLocalUserId(g_userId);
			HMODULE platform = GetModuleHandleA("LibOVRPlatform64_1.dll");
			if (platform != NULL)
				SummerSocial::HookPlatformModule(platform, L"LibOVRPlatform64_1.dll");
		}

		// pnsovr.dll is loaded later, by the game's net service provider loader.
		HMODULE pnsovr = GetModuleHandleA("pnsovr.dll");
		if (pnsovr != NULL)
		{
			PatchPnsOvr((BYTE*)pnsovr);
			if (g_echoRelaySocial)
				SummerSocial::HookPnsOvrModule(pnsovr);
		}
		LdrRegisterDllNotificationFunc registerNotification = (LdrRegisterDllNotificationFunc)GetProcAddress(GetModuleHandleA("ntdll.dll"), "LdrRegisterDllNotification");
		if (registerNotification != NULL)
			registerNotification(0, OnDllNotification, NULL, &g_dllNotificationCookie);
	}
}
