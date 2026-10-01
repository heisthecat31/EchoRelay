using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween 2017 build's request to join a specific session by lobby id ("SNSLobbyJoinSessionRequestv4"), e.g. a
    /// party member following their leader or -lobbyid. Christmas 2017's <see cref="ChristmasLobbyJoinSessionRequestv5"/>
    /// plus a team. Captured (66 bytes): guid lobby id | i64 version lock (halloween 2017's) | i64 platform symbol ("ovr") |
    /// u64 unk0 (1) | u64 unk1 (1) | user id | u16 team index (0).
    /// </summary>
    public class Halloween2017LobbyJoinSessionRequestv4 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyJoinSessionRequestv4").
        /// </summary>
        public override long MessageTypeSymbol => 0x2F03468F77FFB212;

        public Guid LobbyId;
        public long VersionLock;
        public long PlatformSymbol;
        public ulong Unk0;
        public ulong Unk1;
        public XPlatformId UserId;
        public short TeamIndex;
        #endregion

        #region Constructor
        public Halloween2017LobbyJoinSessionRequestv4()
        {
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
            UserId.Stream(io);
            if (io.StreamMode == StreamMode.Read)
            {
                if (io.Length - io.Position >= 2)
                    TeamIndex = io.ReadInt16();
            }
            else
            {
                io.Write(TeamIndex);
            }
        }

        /// <summary>
        /// The christmas 2017 request it corresponds to (handled the same way).
        /// </summary>
        public ChristmasLobbyJoinSessionRequestv5 ToChristmasRequest()
        {
            return new ChristmasLobbyJoinSessionRequestv5
            {
                LobbyId = LobbyId,
                VersionLock = VersionLock,
                PlatformSymbol = PlatformSymbol,
                Unk0 = Unk0,
                Unk1 = Unk1,
                UserId = UserId,
            };
        }

        public override string ToString()
        {
            return $"{GetType().Name}(lobby_id={LobbyId}, version_lock={VersionLock}, unk0={Unk0}, unk1={Unk1}, user_id={UserId}, team_index={TeamIndex})";
        }
        #endregion
    }
}
