#pragma once
#include <windows.h>

/// <summary>
/// Lone Echo co-op, renderer side (loneecho.exe, -coop only): hooks the Direct3D 11 device the game creates and its
/// immediate context's draw calls. First step: on request, log one stretch of draw calls (shaders, buffers, resources)
/// to le_draws.log next to the exe, to find Jack's skinned body draws and their bone data, so a second Jack can be drawn
/// with another player's pose.
/// </summary>
namespace LeRender
{
	/// Hooks D3D11CreateDevice. Call once at start-up, before the game creates its device.
	VOID Install(VOID(*log)(const CHAR* format, ...));

	/// Logs the next stretch of draw calls (about 4000) to le_draws.log.
	VOID RequestDrawLog();
}
