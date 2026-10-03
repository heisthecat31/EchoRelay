using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the matching service, requesting to join a specific session (lobby id), e.g.
    /// a party member following their party leader into a match.
    /// Layout: guid lobby id | i64 version lock | i64 platform symbol | u64 unk0 | u64 unk1 | session settings JSON \0 |
    /// user id | i16 team index.
    /// </summary>
    public class SummerLobbyJoinSessionRequestv6 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyJoinSessionRequestv6").
        /// </summary>
        public override long MessageTypeSymbol => 0x2F03468F77FFB210;

        public Guid LobbyId;
        public long VersionLock;
        public long PlatformSymbol;
        public ulong Unk0;
        public ulong Unk1;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        public short TeamIndex;
        #endregion

        #region Constructor
        public SummerLobbyJoinSessionRequestv6()
        {
            SessionSettings = new SessionSettings();
            UserId = new XPlatformId();
            TeamIndex = -1;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref LobbyId);
            io.Stream(ref VersionLock);
            io.Stream(ref PlatformSymbol);
            io.Stream(ref Unk0);
            io.Stream(ref Unk1);
            io.StreamJSON(ref SessionSettings, true, JSONCompressionMode.None);
            UserId.Stream(io);
            // Christmas 2018 (rad15_winter) sends no team index: a party member following their leader ends at the user id.
            if (io.StreamMode == StreamMode.Write || io.Position + 2 <= io.Length)
                io.Stream(ref TeamIndex);
            else
                TeamIndex = -1;
        }

        public override string ToString()
        {
            return $"{GetType().Name}(lobby_id={LobbyId}, version_lock={VersionLock}, team={TeamIndex}, settings={SessionSettings}, user_id={UserId})";
        }
        #endregion
    }
}
