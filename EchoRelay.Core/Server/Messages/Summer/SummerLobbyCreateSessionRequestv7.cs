using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using System.Text;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the matching service, requesting a new session be created (e.g. a lobby
    /// terminal's "create match"). Like find, it carries no login session token.
    /// Layout: i64 region | i64 version lock | i64 game type | i64 level | i64 platform symbol | u8 lobby type | ... |
    /// guid channel | session settings JSON (null terminated) | user id. The bytes between the lobby type and the channel
    /// are not fully mapped, so the channel is read relative to the JSON and the user id from the end of the message.
    /// </summary>
    public class SummerLobbyCreateSessionRequestv7 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyCreateSessionRequestv7").
        /// </summary>
        public override long MessageTypeSymbol => 0x599A6B1BBDA3CC1D;

        public long RegionSymbol;
        public long VersionLock;
        public long GameTypeSymbol;
        public long LevelSymbol;
        public long PlatformSymbol;
        public byte LobbyTypeValue;
        public byte[] Unmapped;
        public Guid Channel;
        public SessionSettings SessionSettings;
        public XPlatformId UserId;
        public short TeamIndex = -1;

        /// <summary>
        /// The bytes after the lobby type, as received (the layout past it isn't fully mapped; logged for diagnosis).
        /// </summary>
        public byte[] RawTail = Array.Empty<byte>();

        /// <summary>
        /// The lobby type requested for the new session.
        /// </summary>
        public LobbyType LobbyType => LobbyTypeValue == 1 ? LobbyType.Private : LobbyType.Public;
        #endregion

        #region Constructor
        public SummerLobbyCreateSessionRequestv7()
        {
            Unmapped = new byte[7];
            SessionSettings = new SessionSettings();
            UserId = new XPlatformId();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref RegionSymbol);
            io.Stream(ref VersionLock);
            io.Stream(ref GameTypeSymbol);
            io.Stream(ref LevelSymbol);
            io.Stream(ref PlatformSymbol);
            io.Stream(ref LobbyTypeValue);

            if (io.StreamMode == StreamMode.Read)
            {
                byte[] rest = io.ReadBytes((int)(io.Length - io.Position));
                RawTail = rest;
                int jsonStart = Array.IndexOf(rest, (byte)'{');
                int jsonEnd = jsonStart < 0 ? -1 : Array.IndexOf(rest, (byte)0, jsonStart);
                if (jsonStart < 0 || jsonEnd < 0)
                    throw new IOException("SNSLobbyCreateSessionRequestv7 has no session settings JSON");
                SessionSettings = JsonConvert.DeserializeObject<SessionSettings>(Encoding.UTF8.GetString(rest, jsonStart, jsonEnd - jsonStart)) ?? new SessionSettings();

                // The channel is the 16 bytes preceding the JSON.
                int channelStart = Math.Max(0, jsonStart - 16);
                byte[] channel = new byte[16];
                Array.Copy(rest, channelStart, channel, 0, jsonStart - channelStart);
                Channel = new Guid(channel);
                Unmapped = rest.Take(channelStart).ToArray();

                // The message ends with the user id and an i16 team index.
                UserId = new XPlatformId();
                if (rest.Length - (jsonEnd + 1) >= XPlatformId.SIZE + 2)
                {
                    int userIdStart = rest.Length - XPlatformId.SIZE - 2;
                    UserId = new XPlatformId((PlatformCode)BitConverter.ToUInt64(rest, userIdStart), BitConverter.ToUInt64(rest, userIdStart + 8));
                    TeamIndex = BitConverter.ToInt16(rest, rest.Length - 2);
                }
            }
            else
            {
                io.Stream(ref Unmapped, Unmapped.Length);
                io.Stream(ref Channel);
                io.StreamJSON(ref SessionSettings, true, JSONCompressionMode.None);
                UserId.Stream(io);
                io.Stream(ref TeamIndex);
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(version_lock={VersionLock}, game_type={GameTypeSymbol}, level={LevelSymbol}, lobby_type={LobbyType}, channel={Channel}, settings={SessionSettings}, user_id={UserId}, raw_tail={Convert.ToHexString(RawTail)})";
        }
        #endregion
    }
}
