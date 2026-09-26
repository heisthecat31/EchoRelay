using EchoRelay.Core.Game;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Summer
{
    /// <summary>
    /// A message from a summer build client to the matching service, requesting player sessions for the session it was
    /// matched to. Layout: guid matched session | i64 platform symbol | u64 count | count * user id.
    /// </summary>
    public class SummerLobbyPlayerSessionsRequestv3 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbyPlayerSessionsRequestv3").
        /// </summary>
        public override long MessageTypeSymbol => -7281482002396079613;

        public Guid MatchingSession;
        public long PlatformSymbol;
        public XPlatformId[] PlayerIds;

        /// <summary>
        /// The requesting user (the first player id).
        /// </summary>
        public XPlatformId? UserId => PlayerIds.Length > 0 ? PlayerIds[0] : null;
        #endregion

        #region Constructor
        public SummerLobbyPlayerSessionsRequestv3()
        {
            PlayerIds = Array.Empty<XPlatformId>();
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref MatchingSession);
            io.Stream(ref PlatformSymbol);
            ulong count = (ulong)PlayerIds.Length;
            io.Stream(ref count);
            if (io.StreamMode == StreamMode.Read)
            {
                count = Math.Min(count, (ulong)((io.Length - io.Position) / XPlatformId.SIZE));
                PlayerIds = new XPlatformId[count];
                for (ulong i = 0; i < count; i++)
                    PlayerIds[i] = new XPlatformId();
            }
            foreach (XPlatformId playerId in PlayerIds)
                playerId.Stream(io);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(matching_session={MatchingSession}, player_ids=[{string.Join(", ", PlayerIds.AsEnumerable())}])";
        }
        #endregion
    }
}
