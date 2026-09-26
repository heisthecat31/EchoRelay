using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's profile refresh reply ("SNSRefreshProfileResult").
    /// Layout: user id | u32 unk | u8 result | 3 bytes pad | server profile JSON \0 (not compressed).
    /// Clients store the profile as profile_ro.json (and its "loadout" as profile_lo.json); game servers apply it to the
    /// joining player. Game servers treat results 3 and 8 as failures.
    /// </summary>
    public class SummerRefreshProfileResult : Message
    {
        #region Constants
        /// <summary>
        /// The result code for a successful refresh.
        /// </summary>
        public const byte RESULT_SUCCESS = 0x0B;
        #endregion

        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSRefreshProfileResult").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0xB48E249377FE5E02);

        public XPlatformId UserId;
        public uint Unk0;
        public byte Result;
        public JObject Profile;
        #endregion

        #region Constructor
        public SummerRefreshProfileResult()
        {
            UserId = new XPlatformId();
            Profile = new JObject();
        }
        public SummerRefreshProfileResult(XPlatformId userId, byte result, JObject profile)
        {
            UserId = userId;
            Result = result;
            Profile = profile;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            UserId.Stream(io);
            io.Stream(ref Unk0);
            io.Stream(ref Result);
            byte[] padding = new byte[3];
            io.Stream(ref padding, 3);

            if (io.StreamMode == StreamMode.Read)
            {
                string json = Encoding.UTF8.GetString(io.ReadBytes((int)(io.Length - io.Position))).TrimEnd('\0');
                Profile = json.Length > 0 ? JObject.Parse(json) : new JObject();
            }
            else
            {
                io.Write(Encoding.UTF8.GetBytes(Profile.ToString(Formatting.None) + "\0"));
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(user_id={UserId}, result=0x{Result:X2})";
        }
        #endregion
    }
}
