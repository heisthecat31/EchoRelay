using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's leaderboard request ("SNSLeaderboardRequest"), sent by the lobby's stat boards.
    /// Layout: u64 tag | u64 scope | u64 unk | i64 count | null separated stat names, ending with an empty name |
    /// (scope 1 only) u64 user count | user ids.
    /// The first stat name is the board's ranked stat, the rest are shown alongside it. The tag is echoed back in the
    /// <see cref="SummerLeaderboardResponse"/> (the client derives it from the board and "_global" or "_user").
    /// </summary>
    public class SummerLeaderboardRequest : Message
    {
        #region Constants
        /// <summary>
        /// A top of the board request.
        /// </summary>
        public const ulong SCOPE_GLOBAL = 0;
        /// <summary>
        /// A request for the board around the given users.
        /// </summary>
        public const ulong SCOPE_USER = 1;
        #endregion

        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLeaderboardRequest").
        /// </summary>
        public override long MessageTypeSymbol => 0x0D9BA32B0B8E9EEC;

        public ulong Tag;
        public ulong Scope;
        public ulong Unk0;
        public long Count;
        public string[] StatNames;
        public XPlatformId[] UserIds;
        #endregion

        #region Constructor
        public SummerLeaderboardRequest()
        {
            StatNames = Array.Empty<string>();
            UserIds = Array.Empty<XPlatformId>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Tag);
            io.Stream(ref Scope);
            io.Stream(ref Unk0);
            io.Stream(ref Count);

            if (io.StreamMode == StreamMode.Read)
            {
                // Stat names, terminated by an empty name.
                List<string> names = new List<string>();
                while (io.Position < io.Length)
                {
                    List<byte> name = new List<byte>();
                    byte b;
                    while (io.Position < io.Length && (b = io.ReadByte()) != 0)
                        name.Add(b);
                    if (name.Count == 0)
                        break;
                    names.Add(Encoding.UTF8.GetString(name.ToArray()));
                }
                StatNames = names.ToArray();

                // User scoped requests end with the users to center the board on.
                List<XPlatformId> users = new List<XPlatformId>();
                if (io.Length - io.Position >= 8)
                {
                    ulong userCount = io.ReadUInt64();
                    for (ulong i = 0; i < userCount && io.Length - io.Position >= XPlatformId.SIZE; i++)
                    {
                        XPlatformId userId = new XPlatformId();
                        userId.Stream(io);
                        users.Add(userId);
                    }
                }
                UserIds = users.ToArray();
            }
            else
            {
                foreach (string name in StatNames)
                {
                    io.Write(Encoding.UTF8.GetBytes(name));
                    io.Write((byte)0);
                }
                io.Write((byte)0);
                if (Scope == SCOPE_USER)
                {
                    io.Write((ulong)UserIds.Length);
                    foreach (XPlatformId userId in UserIds)
                        userId.Stream(io);
                }
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(tag=0x{Tag:x16}, scope={Scope}, count={Count}, stats=[{string.Join(", ", StatNames)}], users=[{string.Join(", ", UserIds.AsEnumerable())}])";
        }
        #endregion
    }
}
