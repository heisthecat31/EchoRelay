using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The 2017 builds' (rad14) match result for skill ratings, sent by a game server to the login service when a match
    /// ends ("SNSProcessSkillUpdates"). Captured (8 + 40 per player): u64 unk (0 or 1; possibly the winning team) |
    /// per player: guid (stays the same for an account over several matches; likely its login session) | user id |
    /// u16 team (0, 1) | 6 bytes padding.
    /// </summary>
    public class ProcessSkillUpdates : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSProcessSkillUpdates").
        /// </summary>
        public override long MessageTypeSymbol => 0x71E2FA5C0F20CB2B;

        public ulong Unk0;
        public Player[] Players;

        public class Player
        {
            public Guid Id;
            public XPlatformId UserId = new XPlatformId();
            public ushort Team;

            public override string ToString()
            {
                return $"({UserId}, id={Id}, team={Team})";
            }
        }
        #endregion

        #region Constructor
        public ProcessSkillUpdates()
        {
            Players = Array.Empty<Player>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Unk0);

            if (io.StreamMode == StreamMode.Read)
            {
                List<Player> players = new List<Player>();
                while (io.Length - io.Position >= 40)
                {
                    Player player = new Player();
                    player.Id = io.ReadGuid();
                    player.UserId.Stream(io);
                    player.Team = io.ReadUInt16();
                    io.ReadBytes(6);
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
                    io.Write(player.Team);
                    io.Write(new byte[6]);
                }
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(unk0={Unk0}, players=[{string.Join(", ", (IEnumerable<Player>)Players)}])";
        }
        #endregion
    }
}
