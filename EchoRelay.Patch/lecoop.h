#pragma once
#include <windows.h>

/// <summary>
/// Lone Echo shared-world co-op experiments (loneecho.exe, March 2019 build only). Single-player story mode: hooks the
/// game's per-frame update and, on hotkeys, tries the engine calls co-op needs. Addresses and layouts are in
/// J:\LE1store\coop_re_notes.md.
///   F8  log the game state and Jack's head and hands (world space)
///   F9  load the multiplayer player level (r14_glb_global_mp) next to the story level, for its remote-player bodies
///   F10 list what that level brought in (remote players, bodies, player navs)
///   F11 unload it again
/// Everything is logged to echorelay_patch.log.
/// </summary>
namespace LeCoop
{
	/// <summary>
	/// Installs the update hook. Call once, from the rad14 patch set, for the Lone Echo build when it isn't a server.
	/// </summary>
	VOID Install(BYTE* exe, VOID(*log)(const CHAR* format, ...));
}
