using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;
using Newtonsoft.Json.Linq;
using static EchoRelay.Core.Server.Messages.Login.LoginRequest;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build (rad15_summer) client to the login service, requesting to log in.
    /// Layout: guid session | user id | 8-byte locale ("en") | account info JSON (null terminated).
    /// </summary>
    public class SummerLoginRequest : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLoginRequest").
        /// </summary>
        public override long MessageTypeSymbol => -6508315284937585088;

        /// <summary>
        /// The session the client last had (zero on a fresh login).
        /// </summary>
        public Guid Session;
        /// <summary>
        /// The user identifier of the client logging in.
        /// </summary>
        public XPlatformId UserId;
        /// <summary>
        /// The client's language, e.g. "en".
        /// </summary>
        public string Locale;
        /// <summary>
        /// The account information supplied by the client.
        /// </summary>
        public LoginAccountInfo AccountInfo;
        #endregion

        #region Constructor
        public SummerLoginRequest()
        {
            Session = new Guid();
            UserId = new XPlatformId();
            Locale = "en";
            AccountInfo = new LoginAccountInfo();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Session);
            UserId.Stream(io);
            io.Stream(ref Locale, 8);
            io.StreamJSON(ref AccountInfo, true, JSONCompressionMode.None);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session={Session}, user_id={UserId}, locale={Locale}, account_data={AccountInfo.ToLogString()})";
        }
        #endregion
    }
}
