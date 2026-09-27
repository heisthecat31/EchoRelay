#pragma once
#include <windows.h>

/// <summary>
/// Patches for the Echo Arena christmas 2017 build (rad14, ea_rel6_0, EchoArena.exe built 2017-12-19). This build is a
/// generation older than the lobby builds: its executable is EchoArena.exe, it loads this library as dbghelp.dll (the real
/// one is renamed dbghelp_orig.dll; exports.def forwards to it), and it runs without Revive, so the patches also give every
/// install its own Oculus user id.
/// </summary>
namespace XmasPatches
{
	/// <summary>
	/// The PE header timestamp of the christmas 2017 build's EchoArena.exe.
	/// </summary>
	const DWORD EXECUTABLE_TIMESTAMP = 0x5A39494F;

	/// <summary>
	/// Checks whether the running executable is the christmas 2017 build.
	/// </summary>
	BOOL IsXmasBuild();

	/// <summary>
	/// Applies the christmas 2017 build patches (called from DllMain).
	/// </summary>
	VOID Initialize();
}
