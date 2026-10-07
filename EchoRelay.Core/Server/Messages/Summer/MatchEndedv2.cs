using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's match result, sent by a game server to the login service when a match ends ("SNSMatchEndedv2").
    /// Laid out as <see cref="MatchEnded"/>, but each player record is 48 bytes. Captured (273 bytes, 2 players):
    /// guid session id | i64 game type symbol (echo_combat, echo_arenacombat) | char[64] level (mpl_combat_dyson) |
    /// char[16] end reason (timelimit_hit) | u64 unk0 (1 in combat, 783 in arena combat) | u64 player count | u64 unk2 (0) |
    /// null-terminated JSON scores ({"rounds":[{"teams":[{"score":0},{"score":0}]}]}) |
    /// per player: guid | user id | u64 unix time (some minutes before the match ended, likely when they joined) | u64 team.
    /// </summary>
    public class MatchEndedv2 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSMatchEndedv2").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0x80119C19AC72D692);

        public Guid SessionId;
        public long GameTypeSymbol;
        public string Level;
        public string EndReason;
        public ulong Unk0;
        public ulong PlayerCount;
        public ulong Unk2;
        public string Scores;
        public Player[] Players;

        public class Player
        {
            public Guid Id;
            public XPlatformId UserId = new XPlatformId();
            public ulong Time;
            public ulong Team;

            public override string ToString()
            {
                return $"({UserId}, id={Id}, time={DateTimeOffset.FromUnixTimeSeconds((long)Time):u}, team={Team})";
            }
        }
        #endregion

        #region Constructor
        public MatchEndedv2()
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
            io.Stream(ref PlayerCount);
            io.Stream(ref Unk2);
            io.Stream(ref Scores, true);

            if (io.StreamMode == StreamMode.Read)
            {
                List<Player> players = new List<Player>();
                while (io.Length - io.Position >= 48)
                {
                    Player player = new Player();
                    player.Id = io.ReadGuid();
                    player.UserId.Stream(io);
                    player.Time = io.ReadUInt64();
                    player.Team = io.ReadUInt64();
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
                    io.Write(player.Time);
                    io.Write(player.Team);
                }
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session_id={SessionId}, game_type={GameTypeSymbol}, level={Level}, end_reason={EndReason}, unk0={Unk0}, player_count={PlayerCount}, unk2={Unk2}, scores={Scores}, players=[{string.Join(", ", (IEnumerable<Player>)Players)}])";
        }
        #endregion
    }
}
