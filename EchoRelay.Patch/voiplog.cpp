#include "voiplog.h"
#include <detours.h>
#include <atomic>
#include <string>
#include <cstring>
#include <cctype>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#pragma comment(lib, "ole32.lib")

namespace VoipLog
{
	typedef UINT64(*Function)(UINT64, UINT64, UINT64, UINT64, UINT64, UINT64);

	/// <summary>
	/// A provider voice function the game uses. The wrappers pass six integer/pointer arguments through unchanged (the x64
	/// calling convention's four registers and two stack slots), which covers these functions: none takes floats by value.
	/// </summary>
	struct Watched
	{
		const CHAR* name;
		Function original;
		std::atomic<UINT64> calls;
		std::atomic<UINT64> nonZero;
		std::atomic<UINT64> last;
		UINT64 reportedCalls;
	};

	static Watched g_watched[] = {
		{ "MicAvailable" }, { "MicDetected" }, { "MicCreate" }, { "MicStart" }, { "MicPcmFloat32" },
		{ "VoipCreateEncoder" }, { "VoipEncodePcmFloat32" }, { "VoipCreateDecoder" }, { "VoipDecode" }, { "VoipDecodedPcmInt16" },
	};
	static const int WATCHED = sizeof(g_watched) / sizeof(g_watched[0]);

	static VOID(*g_log)(const CHAR* format, ...) = NULL;
	static FARPROC(WINAPI* Real_GetProcAddress)(HMODULE, LPCSTR) = GetProcAddress;
	static std::string g_provider;
	static BOOL g_trace = FALSE;
	// UseVoiceProvider: voice functions asked of g_voiceFrom come from g_voiceTo instead.
	static std::string g_voiceFrom, g_voiceTo;
	static std::atomic<BOOL> g_redirectLogged(FALSE);

	static std::string ModuleName(HMODULE module)
	{
		CHAR path[MAX_PATH] = {};
		GetModuleFileNameA(module, path, MAX_PATH);
		std::string file = path;
		return file.substr(file.find_last_of("\\/") + 1);
	}

	template <int I>
	static UINT64 Wrapper(UINT64 a, UINT64 b, UINT64 c, UINT64 d, UINT64 e, UINT64 f)
	{
		UINT64 result = g_watched[I].original(a, b, c, d, e, f);
		UINT64 calls = ++g_watched[I].calls;
		if (result != 0)
			g_watched[I].nonZero++;
		g_watched[I].last = result;
		// The one-off calls (availability, creation, start) are logged as they happen.
		if (calls <= 3 && I != 4 && I != 6 && I != 8 && I != 9 && g_log)
			g_log("[VOICE] %s -> %llu (0x%llX)", g_watched[I].name, (unsigned long long)result, (unsigned long long)result);
		if (I == 2 && calls == 1 && result != 0 && g_log)
			g_log("[VOICE] FAILED to open the microphone (Windows' default recording device): this player can't be heard");
		return result;
	}

	static const PVOID WRAPPERS[] = {
		(PVOID)Wrapper<0>, (PVOID)Wrapper<1>, (PVOID)Wrapper<2>, (PVOID)Wrapper<3>, (PVOID)Wrapper<4>,
		(PVOID)Wrapper<5>, (PVOID)Wrapper<6>, (PVOID)Wrapper<7>, (PVOID)Wrapper<8>, (PVOID)Wrapper<9>,
	};
	static_assert(sizeof(WRAPPERS) / sizeof(WRAPPERS[0]) == sizeof(g_watched) / sizeof(g_watched[0]), "one wrapper per watched function");


	static FARPROC WINAPI Hook_GetProcAddress(HMODULE module, LPCSTR name)
	{
		FARPROC function = Real_GetProcAddress(module, name);
		if (function == NULL || name == NULL || !HIWORD((ULONG_PTR)name))
			return function;
		if (!g_voiceFrom.empty() && (strncmp(name, "Mic", 3) == 0 || strncmp(name, "Voip", 4) == 0)
			&& _stricmp(ModuleName(module).c_str(), g_voiceFrom.c_str()) == 0)
		{
			HMODULE provider = GetModuleHandleA(g_voiceTo.c_str());
			FARPROC replacement = provider != NULL ? Real_GetProcAddress(provider, name) : NULL;
			if (replacement != NULL)
			{
				module = provider;
				function = replacement;
				if (!g_redirectLogged.exchange(TRUE) && g_log)
					g_log("[VOICE] voice and microphone come from %s instead of %s", g_voiceTo.c_str(), g_voiceFrom.c_str());
			}
			else if (!g_redirectLogged.exchange(TRUE) && g_log)
				g_log("[VOICE] FAILED to take voice from %s (not loaded): using %s", g_voiceTo.c_str(), g_voiceFrom.c_str());
		}
		if (!g_trace)
			return function;
		for (int i = 0; i < WATCHED; i++)
		{
			if (strcmp(name, g_watched[i].name) != 0)
				continue;
			// Only the net provider's exports (the plugins export RadPluginInit), and only the first provider asked.
			if (Real_GetProcAddress(module, "RadPluginInit") == NULL)
				return function;
			CHAR path[MAX_PATH] = {};
			GetModuleFileNameA(module, path, MAX_PATH);
			std::string file = path;
			file = file.substr(file.find_last_of("\\/") + 1);
			if (g_watched[i].original != NULL && g_watched[i].original != (Function)function)
				return function;
			if (g_provider.empty())
			{
				g_provider = file;
				if (g_log)
					g_log("[VOICE] voice and microphone come from %s", file.c_str());
			}
			g_watched[i].original = (Function)function;
			return (FARPROC)WRAPPERS[i];
		}
		return function;
	}

	/// <summary>
	/// The name of Windows' default audio device for recording (eCapture) or playback (eRender), which the game's voice uses.
	/// </summary>
	static std::string DefaultAudioDevice(EDataFlow flow)
	{
		std::string name = "(none)";
		IMMDeviceEnumerator* enumerator = NULL;
		if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), NULL, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&enumerator)))
			return name;
		IMMDevice* device = NULL;
		if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &device)))
		{
			IPropertyStore* properties = NULL;
			if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &properties)))
			{
				PROPVARIANT value;
				PropVariantInit(&value);
				if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR)
				{
					CHAR utf8[512] = {};
					WideCharToMultiByte(CP_UTF8, 0, value.pwszVal, -1, utf8, sizeof(utf8) - 1, NULL, NULL);
					name = utf8;
				}
				PropVariantClear(&value);
				properties->Release();
			}
			device->Release();
		}
		enumerator->Release();
		return name;
	}

	/// <summary>
	/// Logs a summary every 10 seconds while voice functions are being called (voice_trace only).
	/// </summary>
	static DWORD WINAPI ReportThread(LPVOID)
	{
		for (;;)
		{
			Sleep(10000);
			bool active = false;
			std::string line = "[VOICE] last 10s:";
			for (int i = 0; i < WATCHED; i++)
			{
				UINT64 calls = g_watched[i].calls;
				if (g_watched[i].original == NULL)
					continue;
				UINT64 recent = calls - g_watched[i].reportedCalls;
				g_watched[i].reportedCalls = calls;
				if (recent != 0)
					active = true;
				CHAR part[160];
				sprintf_s(part, " %s %llu (total %llu, non-zero %llu, last %llu) |", g_watched[i].name, (unsigned long long)recent,
					(unsigned long long)calls, (unsigned long long)g_watched[i].nonZero.load(), (unsigned long long)g_watched[i].last.load());
				line += part;
			}
			if (active && g_log)
				g_log("%s", line.c_str());
		}
		return 0;
	}

	/// <summary>
	/// Whether _local\config.json turns on the voice function counters ("voice_trace": true). They wrap the game's live
	/// voice functions, so they're opt-in; the default microphone / playback device report is always on.
	/// </summary>
	static bool TraceEnabled()
	{
		CHAR path[MAX_PATH] = {};
		GetModuleFileNameA(NULL, path, MAX_PATH);
		std::string root = path;
		for (int i = 0; i < 3; i++)
			root = root.substr(0, root.find_last_of("\\/"));
		HANDLE file = CreateFileA((root + "\\_local\\config.json").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
		if (file == INVALID_HANDLE_VALUE)
			return false;
		std::string text;
		CHAR buffer[4096];
		DWORD read = 0;
		while (ReadFile(file, buffer, sizeof(buffer), &read, NULL) && read > 0)
			text.append(buffer, read);
		CloseHandle(file);
		std::string compact;
		for (CHAR c : text)
			if (!isspace((unsigned char)c))
				compact += c;
		return compact.find("\"voice_trace\":true") != std::string::npos;
	}

	static DWORD WINAPI DeviceReportThread(LPVOID)
	{
		if (SUCCEEDED(CoInitializeEx(NULL, COINIT_MULTITHREADED)))
		{
			std::string microphone = DefaultAudioDevice(eCapture), speakers = DefaultAudioDevice(eRender);
			if (g_log)
				g_log("[VOICE] Windows' default microphone: %s | default playback device: %s (voice chat records from and plays on these; on Link / Air Link they should be the headset's)",
					microphone.c_str(), speakers.c_str());
			CoUninitialize();
		}
		return 0;
	}

	VOID UseVoiceProvider(const CHAR* replaced, const CHAR* provider)
	{
		g_voiceFrom = replaced;
		g_voiceTo = provider;
	}

	VOID Install(VOID(*log)(const CHAR* format, ...))
	{
		g_log = log;
		CreateThread(NULL, 0, DeviceReportThread, NULL, 0, NULL);
		g_trace = TraceEnabled();
		if (!g_trace && g_voiceFrom.empty())
			return;
		if (g_trace && g_log)
			g_log("[VOICE] voice_trace is on: counting the game's voice function calls");
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach(&(PVOID&)Real_GetProcAddress, Hook_GetProcAddress);
		LONG error = DetourTransactionCommit();
		if (error != NO_ERROR)
		{
			if (g_log)
				g_log("[VOICE] couldn't watch the voice functions (error %ld)", error);
			return;
		}
		if (g_trace)
			CreateThread(NULL, 0, ReportThread, NULL, 0, NULL);
	}
}
