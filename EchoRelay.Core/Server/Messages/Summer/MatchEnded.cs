using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween 2018 build's match result, sent by a game server to the login service when a match ends
    /// ("SNSMatchEnded"; the latest build's is SNSMatchEndedv5). Captured (257 bytes, 2 players):
    /// guid session id | i64 game type symbol (echo_arena, echo_combat) | char[64] level (mpl_arena_a) |
    /// char[16] end reason (timelimit_hit) | u64 unk0 (2321 in arena, 1 in combat) | u64 unk1 (2) | u64 unk2 (0) |
    /// null-terminated JSON scores ({"rounds":[{"teams":[{"score":0},{"score":0}]}]}) |
    /// per player: guid | user id | u16 unk | u16 unk | 4 bytes padding.
    /// </summary>
    public class MatchEnded : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSMatchEnded").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0xC7BA60CD3BC9EE9C);

        public Guid SessionId;
        public long GameTypeSymbol;
        public string Level;
        public string EndReason;
        public ulong Unk0;
        public ulong Unk1;
        public ulong Unk2;
        public string Scores;
        public Player[] Players;

        public class Player
        {
            public Guid Id;
            public XPlatformId UserId = new XPlatformId();
            public ushort Unk0;
            public ushort Unk1;

            public override string ToString()
            {
                return $"({UserId}, id={Id}, unk0={Unk0}, unk1={Unk1})";
            }
        }
        #endregion

        #region Constructor
        public MatchEnded()
        {
            Level = "";
            EndReason = "";
            Scores = "";
            Players = Array.Empty<Player>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref SessionId);
            io.Stream(ref GameTypeSymbol);
            io.Stream(ref Level, 64);
            io.Stream(ref EndReason, 16);
            Level = Level.Split('\0')[0];
            EndReason = EndReason.Split('\0')[0];
            io.Stream(ref Unk0);
            io.Stream(ref Unk1);
            io.Stream(ref Unk2);
            io.Stream(ref Scores, true);

            if (io.StreamMode == StreamMode.Read)
            {
                List<Player> players = new List<Player>();
                while (io.Length - io.Position >= 40)
                {
                    Player player = new Player();
                    player.Id = io.ReadGuid();
                    player.UserId.Stream(io);
                    player.Unk0 = io.ReadUInt16();
                    player.Unk1 = io.ReadUInt16();
                    io.ReadUInt32();
                    players.Add(player);
                }
                Players = players.ToArray();
            }
            else
            {
                foreach (Player player in Players)
                {
                    io.Write(player.Id);
                    player.UserId.Stream(io);
                    io.Write(player.Unk0);
                    io.Write(player.Unk1);
                    io.Write((uint)0);
                }
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session_id={SessionId}, game_type={GameTypeSymbol}, level={Level}, end_reason={EndReason}, unk0={Unk0}, unk1={Unk1}, unk2={Unk2}, scores={Scores}, players=[{string.Join(", ", (IEnumerable<Player>)Players)}])";
        }
        #endregion
    }
}
