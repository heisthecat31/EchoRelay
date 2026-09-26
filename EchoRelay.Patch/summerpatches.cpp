#include "summerpatches.h"
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
		BYTE original[8];
		BYTE patched[8];
		SIZE_T size;
	};

	// Always applied.
	static const BytePatch WATCHDOG_PATCH = {
		"disable the deadlock watchdog (long level loads trip it and it deliberately crashes the game)",
		0x140C35D97, { 0x76, 0x2E }, { 0xEB, 0x2E }, 2 };

	// -server
	static const BytePatch SERVER_FLAGS_PATCH = {
		"set the dedicated server startup flags (or dword [rbx+6C40h], 1 -> 6)",
		0x14005578C, { 0x83, 0x8B, 0x40, 0x6C, 0x00, 0x00, 0x01 }, { 0x83, 0x8B, 0x40, 0x6C, 0x00, 0x00, 0x06 }, 7 };
	static const BytePatch SERVER_PACKAGE_PATCH = {
		"load the client data package (r14netclient) in server mode, since r14netserver is not shipped",
		0x140044F53, { 0x48, 0x8D, 0x15, 0x96, 0x91, 0x14, 0x01 }, { 0x48, 0x8D, 0x15, 0x86, 0x91, 0x14, 0x01 }, 7 };

	// -noovr (implied by -server)
	static const BytePatch NO_OVR_PATCH = {
		"skip Oculus/VR initialization",
		0x1407CF501, { 0x74, 0x22 }, { 0xEB, 0x22 }, 2 };
	static const BytePatch NO_OVR_STATUS_PATCH = {
		"silence 'Failed to get Oculus session status' (logged every frame without VR)",
		0x14049424D, { 0x74, 0x24 }, { 0xEB, 0x24 }, 2 };

	// -server or -headless (-headless clears the render/input flag bits but leaves audio enabled)
	static const BytePatch NO_AUDIO_PATCH = {
		"disable audio, as -noaudio would (always clear the audio flag bit while parsing the command line)",
		0x1404811B6, { 0x74, 0x07 }, { 0x90, 0x90 }, 2 };

	// -multi (implied by -server)
	static const BytePatch MULTI_INSTANCE_PATCH = {
		"skip the single-instance mutex check",
		0x1407CF406, { 0x74, 0x58 }, { 0xEB, 0x58 }, 2 };

	// pnsovr.dll (RVAs), unless -oculusauth
	static const DWORD PNSOVR_TIMESTAMP = 0x5D388D2C;
	static const BytePatch PNSOVR_PATCHES[] = {
		{ "send SNSLoginRequest without an Oculus access token", 0x1050D, { 0x74, 0x5B }, { 0x90, 0x90 }, 2 },
		{ "send SNSLoginRequest without an Oculus access token (retry path)", 0x131E2, { 0x0F, 0x84, 0x15, 0x01, 0x00, 0x00 }, { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 6 },
		{ "skip the Oculus entitlement check", 0x13AD4, { 0x75, 0x21 }, { 0xEB, 0x21 }, 2 },
	};

	static FILE* g_log = NULL;
	static BOOL g_patchPnsOvr = TRUE;
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

	BOOL IsSummerBuild()
	{
		return GetTimestamp((BYTE*)GetModuleHandleA(NULL)) == EXECUTABLE_TIMESTAMP;
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

	static BOOL ApplyGamePatch(const BytePatch& patch)
	{
		BYTE* base = (BYTE*)GetModuleHandleA(NULL);
		return ApplyPatch(base + (patch.address - 0x140000000), patch);
	}

	static VOID PatchPnsOvr(BYTE* base)
	{
		if (GetTimestamp(base) != PNSOVR_TIMESTAMP)
		{
			Log("pnsovr.dll is not the summer build's, not patching it");
			return;
		}
		for (const BytePatch& patch : PNSOVR_PATCHES)
			ApplyPatch(base + patch.address, patch);
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
			PatchPnsOvr((BYTE*)data->DllBase);
		}
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
		BYTE* image = (BYTE*)GetModuleHandleA(NULL);
		BYTE* hostCopy = AllocateNearImage(host.size() + 1);
		if (hostCopy == NULL)
		{
			Log("FAILED to allocate memory near the image for the status host");
			return;
		}
		memcpy(hostCopy, host.c_str(), host.size() + 1);

		for (UINT64 leaAddress : STATUS_HOST_LEAS)
		{
			BYTE* lea = image + (leaAddress - 0x140000000);
			INT32 displacement;
			memcpy(&displacement, lea + 3, 4);
			if (lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x15 || (UINT64)(lea + 7 + displacement) != (UINT64)image + (STATUS_HOST_STRING - 0x140000000))
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
		const WCHAR* commandLine = GetCommandLineW();
		BOOL isServer = HasFlag(commandLine, L"-server");
		BOOL noOvr = isServer || HasFlag(commandLine, L"-noovr");
		BOOL multi = isServer || HasFlag(commandLine, L"-multi");
		g_patchPnsOvr = !HasFlag(commandLine, L"-oculusauth");
		StripOwnFlags();
		Log("EchoRelay.Patch: summer build detected (server=%d, noovr=%d, multi=%d, pnsovr patches=%d, headless=%d)",
			isServer, noOvr, multi, g_patchPnsOvr, HasFlag(commandLine, L"-headless"));

		ApplyGamePatch(WATCHDOG_PATCH);
		if (isServer)
		{
			ApplyGamePatch(SERVER_FLAGS_PATCH);
			ApplyGamePatch(SERVER_PACKAGE_PATCH);
		}
		if (noOvr)
		{
			ApplyGamePatch(NO_OVR_PATCH);
			ApplyGamePatch(NO_OVR_STATUS_PATCH);
		}
		if (multi)
			ApplyGamePatch(MULTI_INSTANCE_PATCH);
		if (isServer || HasFlag(commandLine, L"-headless"))
			ApplyGamePatch(NO_AUDIO_PATCH);
		RedirectStatusHost();

		if (g_patchPnsOvr)
		{
			// Patch pnsovr.dll now if it is already loaded, and whenever it gets loaded.
			HMODULE pnsovr = GetModuleHandleA("pnsovr.dll");
			if (pnsovr != NULL)
				PatchPnsOvr((BYTE*)pnsovr);
			LdrRegisterDllNotificationFunc registerNotification = (LdrRegisterDllNotificationFunc)GetProcAddress(GetModuleHandleA("ntdll.dll"), "LdrRegisterDllNotification");
			if (registerNotification != NULL)
				registerNotification(0, OnDllNotification, NULL, &g_dllNotificationCookie);
		}
	}
}
