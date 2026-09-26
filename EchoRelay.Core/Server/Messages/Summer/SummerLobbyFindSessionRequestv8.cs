using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the matching service, requesting to find a session (e.g. "Play" from the
    /// main menu, or a lobby terminal). Unlike later versions, it carries no login session token.
    /// Layout: i64 version lock | i64 game type | i64 level (-1) | i64 platform symbol | u8 unk0 | u8 unk1 | 6 bytes pad |
    /// guid channel | session settings JSON (null terminated) | user id.
    /// </summary>
    public class SummerLobbyFindSessionRequestv8 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyFindSessionRequestv8").
        /// </summary>
        public override long MessageTypeSymbol => 0x2A56739E56E6FB7A;

        public long VersionLock;
        public long GameTypeSymbol;
        public long LevelSymbol;
        public long PlatformSymbol;
        public byte Unk0;
        public byte Unk1;
        public Guid Channel;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public SummerLobbyFindSessionRequestv8()
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
            io.Stream(ref LevelSymbol);
            io.Stream(ref PlatformSymbol);
            io.Stream(ref Unk0);
            io.Stream(ref Unk1);
            byte[] padding = new byte[6];
            io.Stream(ref padding, 6);
            io.Stream(ref Channel);
            io.StreamJSON(ref SessionSettings, true, JSONCompressionMode.None);
            UserId.Stream(io);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(version_lock={VersionLock}, game_type={GameTypeSymbol}, level={LevelSymbol}, channel={Channel}, settings={SessionSettings}, user_id={UserId})";
        }
        #endregion
    }
}
