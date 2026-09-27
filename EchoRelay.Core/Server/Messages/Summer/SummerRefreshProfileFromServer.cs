using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from the login service to a summer build client, pushing its updated server profile after a game server
    /// changed it (e.g. a loadout save sent with SNSUpdateProfileFromServerv2). The client replaces its cached server profile
    /// (profile_ro.json / profile_lo.json) and re-applies it to its entrant ("[NETGAME] RefreshProfileFromServerCB").
    /// Without it, the client keeps the server profile it got at login until it logs in again.
    /// Layout (from the client's decoder, which requires a 0x18 byte header): user id | 8 bytes unk | server profile JSON \0.
    /// </summary>
    public class SummerRefreshProfileFromServer : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSRefreshProfileFromServer").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0xACAC6DA4E65C3AEE);

        public XPlatformId UserId;
        public ulong Unk0;
        public JObject Profile;
        #endregion

        #region Constructor
        public SummerRefreshProfileFromServer()
        {
            UserId = new XPlatformId();
            Profile = new JObject();
        }
        public SummerRefreshProfileFromServer(XPlatformId userId, JObject profile)
        {
            UserId = userId;
            Profile = profile;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            UserId.Stream(io);
            io.Stream(ref Unk0);
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
