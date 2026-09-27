using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a christmas 2017 build (rad14) client to the matching service, requesting to find a session ("Play"
    /// from the main menu). Summer's <see cref="SummerLobbyFindSessionRequestv8"/> without the level.
    /// Layout: i64 version lock | i64 game type | i64 platform symbol | u8 unk0 | u8 unk1 | 6 bytes pad | guid channel |
    /// session settings JSON (null terminated) | user id.
    /// </summary>
    public class ChristmasLobbyFindSessionRequestv6 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyFindSessionRequestv6").
        /// </summary>
        public override long MessageTypeSymbol => 0x2A56739E56E6FB74;

        public long VersionLock;
        public long GameTypeSymbol;
        public long PlatformSymbol;
        public byte Unk0;
        public byte Unk1;
        public Guid Channel;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public ChristmasLobbyFindSessionRequestv6()
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
            io.Stream(ref Channel);
            io.StreamJSON(ref SessionSettings, true, JSONCompressionMode.None);
            UserId.Stream(io);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(version_lock={VersionLock}, game_type={GameTypeSymbol}, channel={Channel}, settings={SessionSettings}, user_id={UserId})";
        }
        #endregion
    }
}
