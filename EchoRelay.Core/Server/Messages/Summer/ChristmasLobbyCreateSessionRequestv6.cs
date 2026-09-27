using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using System.Text;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a christmas 2017 build (rad14) client to the matching service, requesting a new session be created (a
    /// lobby terminal's private match). The leader sends it for the whole party, listing every member.
    /// Layout (from a captured request): i64 region (-1) | i64 version lock | i64 game type | i64 platform symbol ("ovr") |
    /// u64 entrant count | u32 lobby type (1: private) | u32 unk (3) | session settings JSON \0 | user id × entrant count |
    /// unmapped tail (4 bytes).
    /// </summary>
    public class ChristmasLobbyCreateSessionRequestv6 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyCreateSessionRequestv6").
        /// </summary>
        public override long MessageTypeSymbol => 6456590782678944796;

        public long RegionSymbol;
        public long VersionLock;
        public long GameTypeSymbol;
        public long PlatformSymbol;
        public ulong EntrantCount;
        public uint LobbyTypeValue;
        public uint Unk0;
        public SessionSettings SessionSettings;
        public List<XPlatformId> Entrants;
        public byte[] Tail;

        /// <summary>
        /// The player creating the session (the party leader): the first entrant.
        /// </summary>
        public XPlatformId UserId => Entrants.Count > 0 ? Entrants[0] : new XPlatformId();

        /// <summary>
        /// The lobby type requested for the new session.
        /// </summary>
        public LobbyType LobbyType => LobbyTypeValue == 1 ? LobbyType.Private : LobbyType.Public;
        #endregion

        #region Constructor
        public ChristmasLobbyCreateSessionRequestv6()
        {
            RegionSymbol = -1;
            SessionSettings = new SessionSettings();
            Entrants = new List<XPlatformId>();
            Tail = Array.Empty<byte>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref RegionSymbol);
            io.Stream(ref VersionLock);
            io.Stream(ref GameTypeSymbol);
            io.Stream(ref PlatformSymbol);
            io.Stream(ref EntrantCount);
            io.Stream(ref LobbyTypeValue);
            io.Stream(ref Unk0);
            if (io.StreamMode == StreamMode.Read)
            {
                byte[] rest = io.ReadBytes((int)(io.Length - io.Position));
                int jsonEnd = Array.IndexOf(rest, (byte)0);
                if (jsonEnd < 0)
                    throw new IOException("SNSLobbyCreateSessionRequestv6 has no session settings JSON");
                SessionSettings = JsonConvert.DeserializeObject<SessionSettings>(Encoding.UTF8.GetString(rest, 0, jsonEnd)) ?? new SessionSettings();
                // The entrants follow the JSON (16 bytes each); whatever is left is an unmapped tail.
                int offset = jsonEnd + 1;
                Entrants = new List<XPlatformId>();
                for (ulong i = 0; i < EntrantCount && i < 16 && offset + 16 <= rest.Length; i++, offset += 16)
                    Entrants.Add(new XPlatformId((PlatformCode)BitConverter.ToUInt64(rest, offset), BitConverter.ToUInt64(rest, offset + 8)));
                Tail = rest[offset..];
            }
            else
            {
                io.Write(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(SessionSettings) + "\0"));
                foreach (XPlatformId entrant in Entrants)
                    entrant.Stream(io);
                io.Write(Tail);
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(version_lock={VersionLock}, game_type={GameTypeSymbol}, lobby_type={LobbyType}, settings={SessionSettings}, entrants=[{string.Join(", ", Entrants)}], tail={Convert.ToHexString(Tail)})";
        }
        #endregion
    }
}
