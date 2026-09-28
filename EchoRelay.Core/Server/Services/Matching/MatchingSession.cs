using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services.ServerDB;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Services.Matching
{
    public class MatchingSession
    {
        public XPlatformId UserId { get; private set; }
        public Guid? LobbyId { get; private set; }
        public Guid? Channel { get; private set; }
        public long? GameTypeSymbol { get; private set; }
        public long? LevelSymbol { get; private set; }
        public LobbyType NewSessionLobbyType { get; private set; }
        public LobbyType[] SearchLobbyTypes
        {
            get
            {
                return (NewSessionLobbyType == LobbyType.Private) ? new LobbyType[] { LobbyType.Unassigned } : new LobbyType[] { LobbyType.Unassigned, LobbyType.Public };
            }
        }
        public ERGameServerStartSession.SessionSettings SessionSettings { get; private set; }
        public TeamIndex TeamIndex { get; private set; }

        /// <summary>
        /// The team the matched game server assigned this player (summer arena/combat, where players don't pick one).
        /// </summary>
        public TeamIndex? AssignedTeam { get; set; }

        public RegisteredGameServer? MatchedGameServer { get; set; }
        public Guid? MatchedSessionId { get; set; }

        /// <summary>
        /// Whether the request came in a christmas 2017 build message. Its failure message must be the one that build reads
        /// (SNSLobbySessionFailurev2) even if its version lock is unknown (e.g. a wrong publisher_lock in its config).
        /// </summary>
        public bool IsChristmasClient { get; set; }

        /// <summary>
        /// Indicates whether this matching session belongs to a summer build (rad15_summer) client, which can only be
        /// matched to summer game servers and only understands older message versions.
        /// </summary>
        public bool IsSummer { get; set; }

        /// <summary>
        /// The version lock a lobby build client (summer or halloween) sent with its request. Lobby build clients are only
        /// matched to game servers registered with the same version lock.
        /// </summary>
        public long? VersionLock { get; set; }
        private MatchingSession(XPlatformId userId, Guid? lobbyId, Guid? channel, long? gameTypeSymbol, long? levelSymbol, LobbyType newSessionLobbyType, TeamIndex teamIndex, ERGameServerStartSession.SessionSettings sessionSettings)
        {
            UserId = userId;
            LobbyId = lobbyId;
            Channel = channel;
            GameTypeSymbol = gameTypeSymbol;
            LevelSymbol = levelSymbol;
            NewSessionLobbyType = newSessionLobbyType;
            TeamIndex = teamIndex;
            SessionSettings = sessionSettings;
        }

        public static MatchingSession FromCreateSessionCriteria(XPlatformId userId, Guid? channel, long? gameTypeSymbol, long? levelSymbol, LobbyType lobbyType, TeamIndex teamIndex, ERGameServerStartSession.SessionSettings sessionSettings)
        {
            return new MatchingSession(userId, null, channel, gameTypeSymbol, levelSymbol, lobbyType, teamIndex, sessionSettings);
        }
        public static MatchingSession FromFindSessionCriteria(XPlatformId userId, Guid? channel, long? gameTypeSymbol, TeamIndex teamIndex, ERGameServerStartSession.SessionSettings sessionSettings)
        {
            return new MatchingSession(userId, null, channel, gameTypeSymbol, null, LobbyType.Public, teamIndex, sessionSettings);
        }

        public static MatchingSession FromJoinSpecificSessionCriteria(XPlatformId userId, Guid? lobbyId, TeamIndex teamIndex, ERGameServerStartSession.SessionSettings sessionSettings)
        {
            return new MatchingSession(userId, lobbyId, null, null, null, LobbyType.Public, teamIndex, sessionSettings);
        }
    }
}
