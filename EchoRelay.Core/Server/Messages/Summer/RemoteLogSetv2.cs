using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween / christmas 2018 builds' remote log upload to the login service ("SNSRemoteLogSetv2"), sent by game
    /// servers with gameplay events (SESSION_STARTED, PLAYER_DEATH, ROUND_OVER, MATCH_OVER, NET_EXPLOSION, ...).
    /// <see cref="Login.RemoteLogSetv3"/> with the session id and a shorter header. Captured: user id (a server's: platform
    /// 0, uninitialized account) | guid session id | u32 log level (2) | u32 unk (1) | u64 log count |
    /// (count - 1) u32 offsets of each later log, relative to the end of the offset table | null-terminated JSON logs.
    /// </summary>
    public class RemoteLogSetv2 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSRemoteLogSetv2").
        /// </summary>
        public override long MessageTypeSymbol => 0x244B47685187EAE0;

        public XPlatformId UserId;
        public Guid SessionId;
        public uint LogLevel;
        public uint Unk0;
        public string[] Logs;
        #endregion

        #region Constructor
        public RemoteLogSetv2()
        {
            UserId = new XPlatformId();
            Logs = Array.Empty<string>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            UserId.Stream(io);
            io.Stream(ref SessionId);
            io.Stream(ref LogLevel);
            io.Stream(ref Unk0);

            if (io.StreamMode == StreamMode.Read)
            {
                ulong logCount = io.ReadUInt64();
                uint[] offsets = new uint[logCount];
                for (uint i = 1; i < offsets.Length; i++)
                    offsets[i] = io.ReadUInt32();
                long jsonBufferStart = io.Position;
                Logs = new string[logCount];
                for (int i = 0; i < offsets.Length; i++)
                {
                    io.Position = jsonBufferStart + offsets[i];
                    Logs[i] = io.ReadString(true);
                }
            }
            else
            {
                io.Write((ulong)Logs.Length);
                StreamIO encodedBufferIO = new StreamIO(io.DefaultByteOrder, StreamMode.Write);
                for (int i = 0; i < Logs.Length; i++)
                {
                    if (i > 0)
                        io.Write((uint)encodedBufferIO.Position);
                    encodedBufferIO.Write(Logs[i], true);
                }
                io.Write(encodedBufferIO.ToArray());
                encodedBufferIO.Close();
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session_id={SessionId}, log_level={LogLevel}, logs=[{string.Join(", ", Logs)}])";
        }
        #endregion
    }
}
