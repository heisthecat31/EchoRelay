using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a lobby build client to the login service reporting an analytics event ("SNSTelemetryEvent"), e.g. a
    /// failed in-app purchase: {"from":"main_menu","sku":"unlock_echo_combat","time_spent_ms":12}. The original servers
    /// collected these; EchoRelay only logs them.
    /// Layout: the user's XPlatformId, the event's symbol, then its JSON details (null-terminated).
    /// </summary>
    public class SummerTelemetryEvent : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSTelemetryEvent").
        /// </summary>
        public override long MessageTypeSymbol => unchecked((long)0xF9BC2A364E230214);

        /// <summary>
        /// Event names the lobby builds send, by symbol (unknown ones are logged as their symbol).
        /// </summary>
        private static readonly Dictionary<long, string> EventNames = new[] { "iap_failure", "iap_success" }
            .ToDictionary(Symbol.Hash, name => name);

        public XPlatformId UserId = new XPlatformId();
        public long EventSymbol;
        public string Details = "";
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            UserId.Stream(io);
            io.Stream(ref EventSymbol);
            io.Stream(ref Details, true);
        }

        /// <summary>The event's name, or its symbol if it isn't a known one.</summary>
        public string EventName => EventNames.TryGetValue(EventSymbol, out string? name) ? name : $"0x{EventSymbol:x16}";

        public override string ToString()
        {
            return $"TelemetryEvent(user_id={UserId}, event={EventName}, details={Details})";
        }
        #endregion
    }
}
