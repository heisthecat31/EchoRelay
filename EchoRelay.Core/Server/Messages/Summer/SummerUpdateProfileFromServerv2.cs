using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's server profile update ("SNSUpdateProfileFromServerv2"), e.g. {"loadout": ...} when a player
    /// changes their equipped cosmetics. The JSON is merged into the user's summer server profile.
    /// Layout: guid login session | user id | i64 unk (-1) | JSON \0.
    /// </summary>
    public class SummerUpdateProfileFromServerv2 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSUpdateProfileFromServerv2").
        /// </summary>
        public override long MessageTypeSymbol => 0x1ADE4CC72E99B6C0;

        public Guid Session;
        public XPlatformId UserId;
        public long Unk0;
        public JObject Update;
        #endregion

        #region Constructor
        public SummerUpdateProfileFromServerv2()
        {
            UserId = new XPlatformId();
            Unk0 = -1;
            Update = new JObject();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Session);
            UserId.Stream(io);
            io.Stream(ref Unk0);
            if (io.StreamMode == StreamMode.Read)
            {
                string json = Encoding.UTF8.GetString(io.ReadBytes((int)(io.Length - io.Position))).TrimEnd('\0');
                Update = json.Length > 0 ? JObject.Parse(json) : new JObject();
            }
            else
            {
                io.Write(Encoding.UTF8.GetBytes(Update.ToString(Formatting.None) + "\0"));
            }
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session={Session}, user_id={UserId}, unk0={Unk0}, update={Update.ToString(Formatting.None)})";
        }
        #endregion
    }
}
