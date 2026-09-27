#define _CRT_RAND_S
#include <stdlib.h>
#include "xmaspatches.h"
#include <winternl.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>
#include <share.h>
#include <string>

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
	/// use the pnsdemo platform provider instead of pnsovr (which crashes). Keep pnsovr, which logs in through EchoRelay.
	/// </summary>
	static const BytePatch SERVER_KEEP_PNSOVR = {
		"keep the Oculus platform provider on the server (je -> nop)", 0x97E54,
		{ 0x0F, 0x84, 0x61, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 };

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

	VOID Initialize()
	{
		Log("EchoRelay.Patch: christmas 2017 build (EchoArena.exe) detected");

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
			ApplyPatch(exe, SERVER_KEEP_PNSOVR);
			ApplyPatch(exe, SERVER_PACKAGE);
		}
		RedirectApiHost((BYTE*)GetModuleHandleA(NULL));
		g_userId = GetInstallUserId(server);
		Log("This install's user id: %llu", (unsigned long long)g_userId);

		// pnsovr.dll is loaded later, by the game's net service provider loader.
		HMODULE pnsovr = GetModuleHandleA("pnsovr.dll");
		if (pnsovr != NULL)
			PatchPnsOvr((BYTE*)pnsovr);
		LdrRegisterDllNotificationFunc registerNotification = (LdrRegisterDllNotificationFunc)GetProcAddress(GetModuleHandleA("ntdll.dll"), "LdrRegisterDllNotification");
		if (registerNotification != NULL)
			registerNotification(0, OnDllNotification, NULL, &g_dllNotificationCookie);
	}
}
