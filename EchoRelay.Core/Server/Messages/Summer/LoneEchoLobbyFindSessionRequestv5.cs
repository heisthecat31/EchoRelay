using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// Lone Echo's (rad14, between echo arena 1.78 and 1.102) matchmaking request ("SNSLobbyFindSessionRequestv5"): christmas
    /// 2017's <see cref="ChristmasLobbyFindSessionRequestv6"/> without its channel. Captured:
    /// version lock | game type | platform | u8 1 | u8 2 | 6 bytes pad |
    /// {"gametype":...,"level":... (mpl_arena_a),"closedlobby":false} | user id.
    /// </summary>
    public class LoneEchoLobbyFindSessionRequestv5 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyFindSessionRequestv5").
        /// </summary>
        public override long MessageTypeSymbol => 0x2A56739E56E6FB77;

        public long VersionLock;
        public long GameTypeSymbol;
        public long PlatformSymbol;
        public byte Unk0;
        public byte Unk1;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public LoneEchoLobbyFindSessionRequestv5()
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
            byte[] padding = new byte[6];
            io.Stream(ref padding, 6);
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
                Unk0 = Unk0,
                Unk1 = Unk1,
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
