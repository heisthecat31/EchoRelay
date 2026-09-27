using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Social
{
    /// <summary>
    /// A message on EchoRelay's social service (unofficial). The summer build does parties and friends through the Oculus
    /// Platform SDK (Oculus Rooms and the Oculus friends list), which doesn't work without Oculus services. EchoRelay.Patch
    /// (dbgcore.dll) answers those SDK calls itself and forwards them here as JSON, in both directions.
    /// </summary>
    public class SocialMessage : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type (unofficial).
        /// </summary>
        public override long MessageTypeSymbol => 0x7777777777771000;

        /// <summary>
        /// The message contents.
        /// </summary>
        public JObject Data;
        #endregion

        #region Constructor
        public SocialMessage()
        {
            Data = new JObject();
        }
        public SocialMessage(JObject data)
        {
            Data = data;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            if (io.StreamMode == StreamMode.Read)
            {
                string json = Encoding.UTF8.GetString(io.ReadBytes((int)(io.Length - io.Position))).TrimEnd('\0');
                Data = json.Length > 0 ? JObject.Parse(json) : new JObject();
            }
            else
            {
                io.Write(Encoding.UTF8.GetBytes(Data.ToString(Formatting.None)));
            }
        }

        public override string ToString()
        {
            string json = Data.ToString(Formatting.None);
            return $"{GetType().Name}({(json.Length > 300 ? json.Substring(0, 300) + "..." : json)})";
        }
        #endregion
    }
}
