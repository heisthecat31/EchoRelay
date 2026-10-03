namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween 2017 build's request to create a session ("SNSLobbyCreateSessionRequestv5"): a lobby terminal's private
    /// match, sent by the party leader for the whole party. Without a reply the party stays on "finding" for good.
    /// Laid out exactly like christmas 2017's <see cref="ChristmasLobbyCreateSessionRequestv6"/>. Captured (146 bytes, a party
    /// of two): i64 region (-1) | i64 version lock (halloween 2017's) | i64 game type | i64 platform symbol ("ovr") |
    /// u64 entrant count (2) | u32 lobby type (1: private) | u32 unk (2) |
    /// {"gametype":...,"level":-1,"closedlobby":true} \0 | user id × 2 | tail (4 bytes).
    /// </summary>
    public class Halloween2017LobbyCreateSessionRequestv5 : ChristmasLobbyCreateSessionRequestv6
    {
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyCreateSessionRequestv5").
        /// </summary>
        public override long MessageTypeSymbol => 0x599A6B1BBDA3CC1F;
    }
}
