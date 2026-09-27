#pragma once
#include "pch.h"

/// <summary>
/// Parties and friends for the Echo VR summer lobby build.
///
/// The summer build does its tablet friends list and parties through the Oculus Platform SDK (pnsovr.dll calls into
/// LibOVRPlatform64_1.dll): the Oculus friends list, Oculus Rooms (a party is a private room with a key/value data store),
/// room invites, and room packets. None of that works without Oculus services. These hooks answer those SDK calls with
/// fake SDK objects backed by EchoRelay's social service (socialservice_host, or /social next to loginservice_host in
/// _local/config.json). All other SDK calls (login, voice, entitlement, ...) still go to the real SDK.
/// </summary>
namespace SummerSocial
{
	/// <summary>
	/// Hooks the Oculus Platform SDK exports of a just-loaded LibOVRPlatform*.dll module.
	/// </summary>
	/// <param name="module">The module's base address.</param>
	/// <param name="name">The module's file name, for logging.</param>
	VOID HookPlatformModule(HMODULE module, const WCHAR* name);

	/// <summary>
	/// Hooks a just-loaded pnsovr.dll so its SDK imports are redirected before it logs in.
	/// </summary>
	VOID HookPnsOvrModule(HMODULE module);

	/// <summary>
	/// Sets the logging function to use.
	/// </summary>
	VOID SetLogger(VOID(*log)(const CHAR* format, ...));

	/// <summary>
	/// Logs what the game asks the SDK emulation for and reads from it (launch with -socialtrace), for troubleshooting.
	/// </summary>
	VOID EnableTracing();
}
