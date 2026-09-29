using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween 2017 build's matchmaking request ("SNSLobbyFindSessionRequestv4"), e.g. for the lobby: christmas 2017's
    /// <see cref="ChristmasLobbyFindSessionRequestv6"/> without its channel. Captured:
    /// version lock | game type ("social") | platform ("ovr") | 16 bytes (u64 1, u64 0) |
    /// {"gametype":...,"level":... (mpl_lobby_a_spooky),"closedlobby":false} | user id.
    /// </summary>
    public class Halloween2017LobbyFindSessionRequestv4 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyFindSessionRequestv4").
        /// </summary>
        public override long MessageTypeSymbol => 0x2A56739E56E6FB76;

        public long VersionLock;
        public long GameTypeSymbol;
        public long PlatformSymbol;
        public ulong Unk0;
        public ulong Unk1;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public Halloween2017LobbyFindSessionRequestv4()
        {
            SessionSettings = new SessionSettings();
            UserId = new XPlatformId();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref VersionLock);
            io.Stream(ref GameTypeSymbol);
            io.Stream(ref PlatformSymbol);
            io.Stream(ref Unk0);
            io.Stream(ref Unk1);
            io.StreamJSON(ref SessionSettings, true, JSONCompressionMode.None);
            UserId.Stream(io);
        }

        /// <summary>
        /// The christmas 2017 request it corresponds to (matched the same way).
        /// </summary>
        public ChristmasLobbyFindSessionRequestv6 ToChristmasRequest()
        {
            return new ChristmasLobbyFindSessionRequestv6
            {
                VersionLock = VersionLock,
                GameTypeSymbol = GameTypeSymbol,
                PlatformSymbol = PlatformSymbol,
                Channel = Guid.Empty,
                SessionSettings = SessionSettings,
                UserId = UserId,
            };
        }

        public override string ToString()
        {
            return $"{GetType().Name}(version_lock={VersionLock}, game_type={GameTypeSymbol}, unk0={Unk0}, unk1={Unk1}, settings={SessionSettings}, user_id={UserId})";
        }
        #endregion
    }
}
