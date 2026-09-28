#pragma once
#include <windows.h>

/// <summary>
/// Caps a dedicated server's frame rate. The game's frame timer has a limiter, but it's off, and servers skip the
/// vsync'd Present that paces a client, so a server ran as many frames as it could: about 500 a second idle, and all of
/// a CPU core (and its worker threads) once a match was loaded. This wraps the frame timer's per-frame method (vtable
/// slot 1; the same class in every build) and sleeps away the rest of each frame.
/// </summary>
namespace FrameLimit
{
	/// <summary>
	/// The server frame rate when the command line doesn't give one (-tickrate N).
	/// </summary>
	static const UINT DEFAULT_TICK_RATE = 120;

	/// <summary>
	/// Caps the frame rate at -tickrate N (default <see cref="DEFAULT_TICK_RATE"/>; 0 leaves it uncapped). The flag is
	/// removed from the command line, since the game doesn't know it.
	/// </summary>
	/// <param name="frameTimerVtable">The frame timer's vtable (an address in the game's image).</param>
	/// <param name="log">Writes a line to the patch log.</param>
	VOID Install(VOID** frameTimerVtable, VOID(*log)(const CHAR* format, ...));
}
