using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the login service, requesting another player's server profile (which carries
    /// their loadout). Clients send it for each player they see join, and dress that player's avatar from the reply
    /// (SNSProfileResponsev2). Layout: u64 unk | user id.
    /// </summary>
    public class SummerProfileRequestv2 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSProfileRequestv2").
        /// </summary>
        public override long MessageTypeSymbol => 0x7A24E0B22443B982;

        public ulong Unk0;
        public XPlatformId UserId;
        #endregion

        #region Constructor
        public SummerProfileRequestv2()
        {
            UserId = new XPlatformId();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Unk0);
            UserId.Stream(io);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(unk0=0x{Unk0:X16}, user_id={UserId})";
        }
        #endregion
    }
}
