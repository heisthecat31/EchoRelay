using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.Matching
{
    /// <summary>
    /// The session success message christmas 2017 build (rad14) clients take: <see cref="LobbySessionSuccessv4"/> without its
    /// leading game type.
    /// </summary>
    public class LobbySessionSuccessv3 : Message
    {
        #region Fields
        /// <summary>
        /// The symbol representing this message type ("SNSLobbySessionSuccessv3").
        /// </summary>
        public override long MessageTypeSymbol => 7876201346521829641;

        public LobbySessionSuccessv4 Success;
        #endregion

        #region Constructor
        public LobbySessionSuccessv3()
        {
            Success = new LobbySessionSuccessv4();
        }
        public LobbySessionSuccessv3(LobbySessionSuccessv4 success)
        {
            Success = success;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Success.MatchingSession);
            Success.Endpoint.Stream(io);
            io.Stream(ref Success.TeamIndex);
            io.Stream(ref Success.Unk1);
            io.Stream(ref Success.ServerEncoderFlags);
            io.Stream(ref Success.ClientEncoderFlags);
            io.Stream(ref Success.ServerSequenceId);
            io.Stream(ref Success.ServerMacKey);
            io.Stream(ref Success.ServerEncKey);
            io.Stream(ref Success.ServerRandomKey);
            io.Stream(ref Success.ClientSequenceId);
            io.Stream(ref Success.ClientMacKey);
            io.Stream(ref Success.ClientEncKey);
            io.Stream(ref Success.ClientRandomKey);
        }

        public override string ToString()
        {
            return Success.ToString().Replace(nameof(LobbySessionSuccessv4), nameof(LobbySessionSuccessv3));
        }
        #endregion
    }
}
