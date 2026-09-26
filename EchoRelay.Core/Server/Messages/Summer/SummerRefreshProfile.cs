using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// The summer build's profile refresh request ("SNSRefreshProfile"). Clients send it for themselves after joining a
    /// session, and game servers send it for each player that joins, to obtain the player's server profile (loadout).
    /// Layout: guid login session | user id | u64 flags (1 from clients, 0 from game servers).
    /// </summary>
    public class SummerRefreshProfile : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSRefreshProfile").
        /// </summary>
        public override long MessageTypeSymbol => 0x3AFF685D5E06BC6D;

        public Guid Session;
        public XPlatformId UserId;
        public ulong Flags;
        #endregion

        #region Constructor
        public SummerRefreshProfile()
        {
            UserId = new XPlatformId();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Session);
            UserId.Stream(io);
            if (io.StreamMode == StreamMode.Write || io.Length - io.Position >= 8)
                io.Stream(ref Flags);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(session={Session}, user_id={UserId}, flags={Flags})";
        }
        #endregion
    }
}
