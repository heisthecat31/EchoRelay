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

	/// <summary>
	/// Gives a game running without Revive this install's own Oculus user id (_local\echorelay_id.txt, or
	/// echorelay_server_id.txt for a game server): pnsovr.dll's ovr_GetLoggedInUserID calls return it, and it is stored as the
	/// org-scoped id the provider logs in with. Used by the christmas 2017 build and, from the lobby build patches, by builds
	/// that are played without Revive.
	/// </summary>
	/// <param name="pnsOvr">pnsovr.dll's base address.</param>
	/// <param name="orgScopedIdRva">The RVA of pnsovr.dll's logged in user org-scoped id (UINT64).</param>
	/// <param name="server">Whether this is a game server.</param>
	VOID GiveInstallIdentity(BYTE* pnsOvr, DWORD orgScopedIdRva, BOOL server);

	/// <summary>
	/// The id GiveInstallIdentity gives this install: the player's display name account, else this install's own id (game
	/// servers keep an install id of their own).
	/// </summary>
	UINT64 PlayerUserId(BOOL server);

	/// <summary>
	/// Stops pnsovr.dll reading the player's Oculus access token, which it sends with every login (the original servers
	/// checked it with Oculus). An EchoRelay server has no use for it, and whoever runs one could take over the player's
	/// Oculus account with it. The token callback's success path copies an empty token instead.
	/// Found by pattern, so it works on each build's pnsovr.dll that asks for the token.
	/// </summary>
	/// <returns>Whether the token is protected (or this pnsovr.dll never asks for it).</returns>
	BOOL ProtectAccessToken(BYTE* pnsOvr);
}
