using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a christmas 2017 build (rad14) client to the matching service, requesting to join a specific session
    /// (lobby id): a party member following their party leader ("[NETGAME] Join Party Lobby"). The party leader's own queued
    /// join waits for its members', so without a reply the whole party stays on "finding".
    /// Layout (from a captured request): guid lobby id | i64 version lock | i64 platform symbol ("ovr") | u64 unk0 (1) |
    /// u64 unk1 (2) | user id. Summer's SNSLobbyJoinSessionRequestv6 without its session settings and team.
    /// </summary>
    public class ChristmasLobbyJoinSessionRequestv5 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyJoinSessionRequestv5").
        /// </summary>
        public override long MessageTypeSymbol => 3387628926720258579;

        public Guid LobbyId;
        public long VersionLock;
        public long PlatformSymbol;
        public ulong Unk0;
        public ulong Unk1;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public ChristmasLobbyJoinSessionRequestv5()
        {
            UserId = new XPlatformId();
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
            UserId.Stream(io);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(lobby_id={LobbyId}, version_lock={VersionLock}, unk0={Unk0}, unk1={Unk1}, user_id={UserId})";
        }
        #endregion
    }
}
