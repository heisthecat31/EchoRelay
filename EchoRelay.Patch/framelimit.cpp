#include "framelimit.h"
#include <cwchar>
#include <cstdlib>
#pragma comment(lib, "winmm.lib")

namespace FrameLimit
{
	typedef VOID(*TickFunc)(VOID*);
	static TickFunc g_tick = NULL;
	static LONGLONG g_frameTicks = 0;   // one frame, in QueryPerformanceCounter ticks
	static LONGLONG g_frequency = 0;
	static LONGLONG g_nextFrame = 0;
	static VOID(*g_log)(const CHAR* format, ...) = NULL;
	static LONGLONG g_statsStart = 0;
	static LONGLONG g_statsFrames = 0;
	static LONGLONG g_statsSlept = 0;

	/// <summary>
	/// Every 5 minutes (sooner for the first), logs the frame rate and how much of the time was slept.
	/// </summary>
	static VOID CountFrame(LONGLONG now, DWORD slept)
	{
		static LONGLONG interval = 0;
		if (g_statsStart == 0)
		{
			g_statsStart = now;
			interval = g_frequency * 30;
			return;
		}
		g_statsFrames++;
		g_statsSlept += slept;
		LONGLONG elapsed = now - g_statsStart;
		if (elapsed < interval)
			return;
		double seconds = (double)elapsed / g_frequency;
		g_log("Server frame rate: %.1f frames a second, sleeping %.0f%% of the time", g_statsFrames / seconds, g_statsSlept / (seconds * 10.0));
		g_statsStart = now;
		g_statsFrames = 0;
		g_statsSlept = 0;
		interval = g_frequency * 300;
	}

	static VOID LimitedTick(VOID* timer)
	{
		LARGE_INTEGER now;
		QueryPerformanceCounter(&now);
		DWORD slept = 0;
		if (g_nextFrame != 0 && now.QuadPart < g_nextFrame)
		{
			// Sleep the whole milliseconds left (timeBeginPeriod(1) makes that accurate); the last fraction isn't worth spinning for.
			DWORD milliseconds = (DWORD)((g_nextFrame - now.QuadPart) * 1000 / g_frequency);
			if (milliseconds > 0)
				Sleep(milliseconds);
			slept = milliseconds;
			g_nextFrame += g_frameTicks;
		}
		else
			g_nextFrame = now.QuadPart + g_frameTicks; // first frame, or running behind: don't try to catch up
		CountFrame(now.QuadPart, slept);
		g_tick(timer);
	}

	/// <summary>
	/// Reads and blanks "-tickrate N" from a command line. Returns N, or -1 if the flag isn't there.
	/// </summary>
	template <typename T>
	static INT TakeTickRate(T* commandLine, const T* flag, SIZE_T flagLength)
	{
		for (T* p = commandLine; *p; p++)
		{
			SIZE_T i = 0;
			while (i < flagLength && p[i] == flag[i])
				i++;
			if (i != flagLength || (p != commandLine && p[-1] != ' ') || (p[i] != ' ' && p[i] != 0))
				continue;
			T* value = p + i;
			while (*value == ' ')
				value++;
			INT rate = 0;
			T* end = value;
			while (*end >= '0' && *end <= '9')
				rate = rate * 10 + (*end++ - '0');
			if (end == value)
				rate = -1;
			for (T* blank = p; blank < end; blank++)
				*blank = ' ';
			return rate;
		}
		return -1;
	}

	VOID Install(VOID** frameTimerVtable, VOID(*log)(const CHAR* format, ...))
	{
		g_log = log;
		INT rate = TakeTickRate(GetCommandLineW(), L"-tickrate", wcslen(L"-tickrate"));
		TakeTickRate(GetCommandLineA(), "-tickrate", strlen("-tickrate"));
		if (rate < 0)
			rate = DEFAULT_TICK_RATE;
		if (rate == 0)
		{
			log("Server frame rate: uncapped (-tickrate 0)");
			return;
		}
		LARGE_INTEGER frequency;
		QueryPerformanceFrequency(&frequency);
		g_frequency = frequency.QuadPart;
		g_frameTicks = g_frequency / rate;
		timeBeginPeriod(1);

		VOID** slot = frameTimerVtable + 1;
		DWORD protect;
		if (!VirtualProtect(slot, sizeof(VOID*), PAGE_READWRITE, &protect))
		{
			log("FAILED to cap the server frame rate (vtable not writable)");
			return;
		}
		g_tick = (TickFunc)*slot;
		*slot = (VOID*)&LimitedTick;
		VirtualProtect(slot, sizeof(VOID*), protect, &protect);
		log("Patched: cap the server at %d frames a second (-tickrate N to change; 0 = uncapped), sleeping between frames", rate);
	}
}
