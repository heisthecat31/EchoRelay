#pragma once
#include "pch.h"

/// <summary>
/// The lobby builds' local HTTP API (/session) in the layout of later Echo VR builds, so tools written for it (Spark,
/// EchoVRAPI) can parse it. The old builds send players with flat position/orientation and array hands, no pause,
/// last_throw or player objects, no round scores, and "INVALID LEVEL"/"INVALID GAMETYPE" (they can't name levels);
/// SessionApi::Convert rebuilds the document with every value the game sent, adds what's missing with the defaults later
/// builds send when idle, and names the level from the client's own log.
/// </summary>
namespace SessionApi
{
	/// <summary>
	/// Where a build keeps game state its /session doesn't report (all zero: not located on that build). Offsets are from
	/// the handler's net game; RVAs are in echovr.exe.
	/// </summary>
	struct GameMemory
	{
		UINT32 gameState;        // net game -> game state
		UINT32 discComponent;    // game state -> the disc's component
		UINT32 discComponentVtable; // that component's vtable (RVA), checked before following it
		UINT32 discBodyOwner;    // component -> the object pointing at the disc's rigid body
		UINT32 discBody;         // that object -> the rigid body
		UINT32 discPosition;     // rigid body -> position (3 floats)
		UINT32 discVelocity;     // rigid body -> velocity (3 floats)
		UINT32 lastThrow;        // RVA of the F10 menu's last throw (13 floats, in the order of SessionApi's LAST_THROW)
		UINT64 discComponentName; // the component's name symbol (at +20h), checked with the vtable
	};

	/// <summary>
	/// Sets where this build keeps the game state added to /session.
	/// </summary>
	VOID SetGameMemory(const GameMemory& memory);

	/// <summary>
	/// Sets the client name to use when the game's /session has none (the halloween build), e.g. the login display name.
	/// </summary>
	VOID SetClientName(const std::string& name);

	/// <summary>
	/// Converts a /session body (data, len; a trailing NUL is ignored). Returns the converted body (valid until the next
	/// call on the same thread) and its length. If the body doesn't parse, returns it unchanged without its NUL.
	/// </summary>
	extern "C" const char* SessionApiConvert(const char* data, size_t len, size_t* outLen, const BYTE* netGame);
}
