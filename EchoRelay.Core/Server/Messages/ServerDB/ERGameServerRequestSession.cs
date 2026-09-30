using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Server.Messages.ServerDB
{
    /// <summary>
    /// Unofficial: a game server asks EchoRelay to start a session on itself, on a level and game type given by name (its
    /// -forcelevel / -gametype launch options). EchoRelay starts it like one a player requested, so players are matched
    /// into it. The server sends it again whenever its session ends.
    /// </summary>
    public class ERGameServerRequestSession : Message
    {
        #region Fields
        public override long MessageTypeSymbol => 0x7777777777770B00;

        /// <summary>The level's name, e.g. mpl_arena_a.</summary>
        public string Level;

        /// <summary>The game type's name, e.g. echo_arena. Empty for the level's usual game type.</summary>
        public string GameType;
        #endregion

        #region Constructor
        public ERGameServerRequestSession()
        {
            Level = "";
            GameType = "";
        }
        public ERGameServerRequestSession(string level, string gameType)
        {
            Level = level;
            GameType = gameType;
        }
        #endregion

        #region Functions
        public override void Stream(StreamIO io)
        {
            io.Stream(ref Level);
            io.Stream(ref GameType);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(level={Level}, gametype={GameType})";
        }
        #endregion
    }
}
