using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from the login service to a summer build client, carrying a player's server profile. It answers
    /// SNSProfileRequestv2, and is also pushed unrequested when a player's loadout changes: the client stores the profile for
    /// that user and re-applies it to their entrant whether or not it asked ("[NETGAME] New user settings for '%s' after
    /// profile received"). Layout (from the client's decoder, which requires a 0x10 byte header): user id | profile JSON \0.
    /// </summary>
    public class SummerProfileResponsev2 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSProfileResponsev2").
        /// </summary>
        public override long MessageTypeSymbol => 0x2261FD8D8F56FAD6;

        public XPlatformId UserId;
        public JObject Profile;
        #endregion

        #region Constructor
        public SummerProfileResponsev2()
        {
            UserId = new XPlatformId();
            Profile = new JObject();
        }
        public SummerProfileResponsev2(XPlatformId userId, JObject profile)
        {
            UserId = userId;
            Profile = profile;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            UserId.Stream(io);
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
            return $"{GetType().Name}(user_id={UserId})";
        }
        #endregion
    }
}
