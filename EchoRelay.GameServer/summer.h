#pragma once

#include "pch.h"

/// <summary>
/// Support for the Echo VR "summer lobby" build (rad15_summer, echovr.exe PE timestamp 0x5D388D3C).
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
	/// The PE header timestamp of the summer build's echovr.exe.
	/// </summary>
	const DWORD EXECUTABLE_TIMESTAMP = 0x5D388D3C;

	/// <summary>
	/// Checks whether the host process is the summer build.
	/// </summary>
	BOOL IsSummerBuild();

	/// <summary>
	/// Obtains the summer build IServerLib implementation (created on first call).
	/// </summary>
	VOID* GetServerLib();

	/// <summary>
	/// Releases the summer build IServerLib implementation.
	/// </summary>
	VOID Shutdown();
}
