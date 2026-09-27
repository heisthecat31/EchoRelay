#pragma once

#include "pch.h"

/// <summary>
/// Support for the Echo VR lobby builds: summer (rad15_summer, PE timestamp 0x5D388D3C) and halloween
/// (rad15_halloween, PE timestamp 0x5BC7B897), which share this IServerLib interface.
///
/// The summer build's IServerLib interface is older than the final build's (no UnkFunc0/UnkFunc1, and Update/RequestRegistration
/// take different arguments), and none of the final build's engine addresses (TCP broadcaster, JSON, logging) apply to it.
/// This implementation therefore talks to EchoRelay's ServerDB service over its own WinHTTP websocket, and only uses two
/// summer engine functions: the broadcaster's ReceiveLocalEvent (to hand ServerDB messages to the game) and the symbol hash
/// used for local event ids. It translates between EchoRelay's ServerDB messages and the summer build's lobby messages.
/// </summary>
namespace Summer
{
	/// <summary>
	/// Checks whether the host process is one of the lobby builds we support (summer or halloween).
	/// </summary>
	BOOL IsLobbyBuild();

	/// <summary>
	/// Obtains the summer build IServerLib implementation (created on first call).
	/// </summary>
	VOID* GetServerLib();

	/// <summary>
	/// Releases the summer build IServerLib implementation.
	/// </summary>
	VOID Shutdown();
}
