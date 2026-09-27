using EchoRelay.Core.Server.Messages.Login;
using EchoRelay.Core.Server.Storage.Types;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The halloween lobby build's (rad15_halloween) login settings, "SNSLoginClientSettings". Later builds renamed it
    /// SNSLoginSettings, which this build doesn't know, so it has to be sent this one instead (same layout). Without it
    /// the logged in menu never appears.
    /// </summary>
    public class HalloweenLoginClientSettings : LoginSettings
    {
        /// <summary>
        /// The symbol representing this message type ("SNSLoginClientSettings").
        /// </summary>
        public override long MessageTypeSymbol => 0x208DA2538A66A18D;

        public HalloweenLoginClientSettings() : base() { }
        public HalloweenLoginClientSettings(LoginSettingsResource resource) : base(resource) { }
    }
}
