using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's login reply ("SNSLoginProfileResult"). The client stays on the login screen until it receives
    /// this with a success result.
    /// Layout: guid session | user id | u32 unk | u8 result | 3 bytes pad | u64 uncompressed length |
    /// zlib(client profile JSON \0 server profile JSON), where the server profile is not null terminated.
    /// </summary>
    public class SummerLoginProfileResult : Message
    {
        #region Constants
        /// <summary>
        /// The result code the summer client treats as a successful login.
        /// </summary>
        public const byte RESULT_SUCCESS = 0x0B;
        /// <summary>
        /// Shown by the client as "Invalid login request".
        /// </summary>
        public const byte RESULT_INVALID_REQUEST = 0;
        /// <summary>
        /// Shown by the client as "Login authentication failed" (e.g. a wrong account password).
        /// </summary>
        public const byte RESULT_AUTHENTICATION_FAILED = 7;
        /// <summary>
        /// Shown by the client as "Access has been restricted by Ready At Dawn" (a ban).
        /// </summary>
        public const byte RESULT_RESTRICTED = 8;
        #endregion

        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLoginProfileResult").
        /// </summary>
        public override long MessageTypeSymbol => 0x236CCBEFA38074C0;

        public Guid Session;
        public XPlatformId UserId;
        public uint Unk0;
        public byte Result;
        public JObject ClientProfile;
        public JObject ServerProfile;
        #endregion

        #region Constructor
        public SummerLoginProfileResult()
        {
            UserId = new XPlatformId();
            ClientProfile = new JObject();
            ServerProfile = new JObject();
        }
        public SummerLoginProfileResult(Guid session, XPlatformId userId, byte result, JObject clientProfile, JObject serverProfile)
        {
            Session = session;
            UserId = userId;
            Result = result;
            ClientProfile = clientProfile;
            ServerProfile = serverProfile;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Session);
            UserId.Stream(io);
            io.Stream(ref Unk0);
            io.Stream(ref Result);
            byte[] padding = new byte[3];
            io.Stream(ref padding, 3);

            // Failures carry no profiles, but the length field is part of the fixed 0x30 byte header the client requires
            // (shorter messages are dropped, leaving the client waiting on a black screen).
            if (Result != RESULT_SUCCESS)
            {
                ulong none = 0;
                if (io.StreamMode == StreamMode.Write || io.Length - io.Position >= 8)
                    io.Stream(ref none);
                return;
            }

            if (io.StreamMode == StreamMode.Read)
            {
                ulong length = io.ReadUInt64();
                byte[] compressed = io.ReadBytes((int)(io.Length - io.Position));
                string raw = Encoding.UTF8.GetString(Compression.DecompressZlib(compressed), 0, (int)length);
                string[] parts = raw.Split('\0', 2);
                ClientProfile = JObject.Parse(parts[0]);
                ServerProfile = parts.Length > 1 && parts[1].Trim('\0').Length > 0 ? JObject.Parse(parts[1].Trim('\0')) : new JObject();
            }
            else
            {
                byte[] raw = Encoding.UTF8.GetBytes(ClientProfile.ToString(Formatting.None) + "\0" + ServerProfile.ToString(Formatting.None));
                io.Write((ulong)raw.Length);
                io.Write(Compression.CompressZlib(raw));
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session={Session}, user_id={UserId}, result=0x{Result:X2})";
        }
        #endregion
    }
}
