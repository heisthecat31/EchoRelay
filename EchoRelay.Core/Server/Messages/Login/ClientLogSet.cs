using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Login
{
    /// <summary>
    /// A batch of the game's own log lines ("SNSClientLogSet"), which clients and game servers send to the login service,
    /// e.g. "[NETGAME] Unknown loadout instance name 0x41D2D432192A0612". EchoRelay doesn't act on them.
    /// Layout: guid login session (zero from game servers) | user id | u64 level | u64 line count | u32 offset table size
    /// (count * 4) | the lines, null-terminated | padding to 4 bytes | u32 offset of each line.
    /// </summary>
    public class ClientLogSet : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSClientLogSet").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0xCEF0056C7F77C50A);

        public Guid Session;
        public XPlatformId UserId = new XPlatformId();
        public ulong Level;
        public string[] Lines = Array.Empty<string>();
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Session);
            UserId.Stream(io);
            io.Stream(ref Level);
            if (io.StreamMode == StreamMode.Read)
            {
                ulong count = io.ReadUInt64();
                io.ReadUInt32();
                byte[] rest = io.ReadBytes((int)(io.Length - io.Position));
                int tableSize = (int)Math.Min(count * 4, (ulong)rest.Length);
                int textSize = rest.Length - tableSize;
                List<string> lines = new List<string>();
                for (int i = 0; i < tableSize / 4; i++)
                {
                    int start = BitConverter.ToInt32(rest, textSize + i * 4);
                    if (start < 0 || start >= textSize)
                        continue;
                    int end = Array.IndexOf(rest, (byte)0, start, textSize - start);
                    lines.Add(Encoding.UTF8.GetString(rest, start, (end < 0 ? textSize : end) - start));
                }
                Lines = lines.ToArray();
            }
            else
            {
                io.Write((ulong)Lines.Length);
                io.Write((uint)(Lines.Length * 4));
                List<int> offsets = new List<int>();
                using MemoryStream text = new MemoryStream();
                foreach (string line in Lines)
                {
                    offsets.Add((int)text.Length);
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\0");
                    text.Write(bytes, 0, bytes.Length);
                }
                while (text.Length % 4 != 0)
                    text.WriteByte(0);
                io.Write(text.ToArray());
                foreach (int offset in offsets)
                    io.Write((uint)offset);
            }
        }

        public override string ToString()
        {
            return $"ClientLogSet(user_id={UserId}, level={Level}, lines=[{string.Join(" | ", Lines.Select(line => line.Replace("\n", " ").Replace("\t", " ").Trim()))}])";
        }
        #endregion
    }
}
