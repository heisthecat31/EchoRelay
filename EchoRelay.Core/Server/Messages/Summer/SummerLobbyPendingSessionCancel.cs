using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the matching service, cancelling its pending find/create request.
    /// Layout: one byte.
    /// </summary>
    public class SummerLobbyPendingSessionCancel : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyPendingSessionCancel").
        /// </summary>
        public override long MessageTypeSymbol => 0x70F2DA850F25105A;

        public byte Unk0;
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Unk0);
        }

        public override string ToString()
        {
            return $"{GetType().Name}()";
        }
        #endregion
    }
}
