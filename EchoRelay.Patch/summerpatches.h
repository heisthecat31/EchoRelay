#pragma once
#include "pch.h"

/// <summary>
/// Patches for the Echo VR "summer lobby" build (rad15_summer, echovr.exe PE timestamp 0x5D388D3C).
///
/// The summer build loads dbgcore.dll from its own directory at the very start of WinMain, before it reads its command line
/// or initializes anything, so all patches here are simple in-memory byte patches applied from DllMain.
///
/// Command line flags (checked with a substring search, like the summer build does for its own flags, which it ignores otherwise):
///   -server     Run as a dedicated game server (server flags, client data package, no VR, no single-instance mutex).
///               Combine with the game's own -headless flag for a server without rendering.
///   -noovr      Skip Oculus/VR initialization (implied by -server).
///   -multi      Allow more than one instance of the game (implied by -server).
///   -oculusauth Keep pnsovr.dll's Oculus access token and entitlement checks (they are bypassed by default).
/// </summary>
namespace SummerPatches
{
	/// <summary>
	/// The PE header timestamp of the summer build's echovr.exe.
	/// </summary>
	const DWORD EXECUTABLE_TIMESTAMP = 0x5D388D3C;

	/// <summary>
	/// Checks whether the host process is the summer build.
	/// </summary>
	BOOL IsSummerBuild();

	/// <summary>
	/// Applies the summer build patches for the current command line.
	/// </summary>
	VOID Initialize();
}
