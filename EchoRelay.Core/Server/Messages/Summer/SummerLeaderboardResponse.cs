using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's leaderboard reply ("SNSLeaderboardResponse").
    /// Layout: u64 tag (from the request) | u64 uncompressed length | zlib(JSON array of entries), where each entry is
    /// {"rank": int, "displayname": string, "score": [stat name, value string], "related": [[stat name, value string], ...]}.
    /// </summary>
    public class SummerLeaderboardResponse : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLeaderboardResponse").
        /// </summary>
        public override long MessageTypeSymbol => 0x4757A48A7AF6AEF7;

        public ulong Tag;
        public JArray Entries;
        #endregion

        #region Constructor
        public SummerLeaderboardResponse()
        {
            Entries = new JArray();
        }
        public SummerLeaderboardResponse(ulong tag, JArray entries)
        {
            Tag = tag;
            Entries = entries;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Tag);
            if (io.StreamMode == StreamMode.Read)
            {
                ulong length = io.ReadUInt64();
                byte[] compressed = io.ReadBytes((int)(io.Length - io.Position));
                Entries = JArray.Parse(Encoding.UTF8.GetString(Compression.DecompressZlib(compressed), 0, (int)length).TrimEnd('\0'));
            }
            else
            {
                // Not null terminated: the client parses exactly the uncompressed length and rejects trailing bytes.
                byte[] raw = Encoding.UTF8.GetBytes(Entries.ToString(Formatting.None));
                io.Write((ulong)raw.Length);
                io.Write(Compression.CompressZlib(raw));
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(tag=0x{Tag:x16}, entries={Entries.Count})";
        }
        #endregion
    }
}
