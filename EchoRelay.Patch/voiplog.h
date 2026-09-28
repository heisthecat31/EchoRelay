#pragma once
#include <windows.h>

/// <summary>
/// Voice chat diagnostics: logs what the game asks its net provider (pnsovr.dll / pnsrad.dll) for voice, to find where voice
/// stops. The game only uses the provider's microphone and voice codec functions (voice travels through the game server),
/// so these are counted: microphone available / created / started / samples read, encoder and decoder created, frames
/// encoded (sent) and decoded (heard). A summary is logged every 10 seconds while voice is active. The counters wrap live game
/// functions, so they only run with "voice_trace": true in _local\config.json; the player's default microphone and playback
/// device (which voice chat uses) are always logged at start-up.
/// </summary>
namespace VoipLog
{
	/// <summary>
	/// Starts watching the game's voice functions (hooks GetProcAddress, which the game resolves provider exports with).
	/// </summary>
	/// <param name="log">Writes a line to the patch log.</param>
	VOID Install(VOID(*log)(const CHAR* format, ...));

	/// <summary>
	/// Makes the game take its voice and microphone functions (Mic*, Voip*) from another net provider: those it asks of
	/// <paramref name="replaced"/> (e.g. "pnsovr.dll") come from <paramref name="provider"/> (e.g. "pnsrad.dll"). Call before Install.
	/// </summary>
	VOID UseVoiceProvider(const CHAR* replaced, const CHAR* provider);
}
