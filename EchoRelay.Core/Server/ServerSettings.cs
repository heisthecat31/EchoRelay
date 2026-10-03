using EchoRelay.Core.Game;
using System.Web;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// Settings for a given <see cref="Server"/> to execute.
    /// </summary>
    public class ServerSettings
    {
        #region Properties
        /// <summary>
        /// The port which the websocket server is bound to.
        /// </summary>
        public ushort Port { get; }

        /// <summary>
        /// The path at which the API service processes requests.
        /// </summary>
        public string ApiServicePath { get; }

        /// <summary>
        /// The path at which the <see cref="ConfigService"/> processes requests.
        /// </summary>
        public string ConfigServicePath { get; }

        /// <summary>
        /// The path at which the <see cref="LoginService"/> processes requests.
        /// </summary>
        public string LoginServicePath { get; }

        /// <summary>
        /// The path at which the <see cref="MatchingService"/> processes requests.
        /// </summary>
        public string MatchingServicePath { get; }

        /// <summary>
        /// The path at which the <see cref="ServerDBService"/> processes requests.
        /// </summary>
        public string ServerDBServicePath { get; }

        /// <summary>
        /// The path at which the <see cref="TransactionService"/> processes requests.
        /// </summary>
        public string TransactionServicePath { get; }

        /// <summary>
        /// The HTTP path for the social service (summer build parties and friends, fed by EchoRelay.Patch).
        /// </summary>
        public string SocialServicePath { get; } = "/social";

        /// <summary>
        /// The grace period for which a disconnected peer may reconnect and use the same session token.
        /// Sessions after this time will expire for clients which disconnected.
        /// </summary>
        public TimeSpan SessionDisconnectedTimeout { get; }

        /// <summary>
        /// An API key which if set (non-null), must be provided as a query parameter when 
        /// connecting to ServerDB to successfully pass game server registration.
        /// </summary>
        public string? ServerDBApiKey { get; }

        /// <summary>
        /// Indicates whether game servers registering to ServerDB should be validated by performing
        /// a raw ping request to them, at registration time.
        /// </summary>
        public bool ServerDBValidateServerEndpoint { get; }
        /// <summary>
        /// The timeout in milliseconds before failing game server validation, if <see cref="ServerDBValidateServerEndpointTimeout"/> was provided.
        /// This is a timeout used individually for a send and receive operation.
        /// </summary>
        public int ServerDBValidateServerEndpointTimeout { get; }

        /// <summary>
        /// Indicates whether the matching service should try to force the user into any available game server,
        /// in the event that there are not enough game servers to create the requested session.
        /// </summary>
        public bool ForceIntoAnySessionIfCreationFails { get; }

        /// <summary>
        /// Indicates whether the matching service should prefer to match peers to game servers which
        /// have more players within them, then by ping, if true. If false, then ping is prioritized
        /// before player count.
        /// </summary>
        public bool FavorPopulationOverPing { get; }

        /// <summary>
        /// Indicates whether summer build (rad15_summer) clients should be given every cosmetic unlock and a max level
        /// in their login profile.
        /// </summary>
        public bool SummerUnlockAll { get; }

        /// <summary>
        /// The maximum time to wait for a summer build game server to finish loading a newly started session before
        /// sending clients to it. Summer clients that join a server mid-load get stuck in "server loading".
        /// </summary>
        public TimeSpan SummerSessionLoadTimeout { get; }

        /// <summary>
        /// The text shown on the summer build's lobby news board (served at {ApiServicePath}/status/news).
        /// </summary>
        public string SummerNews { get; }

        /// <summary>
        /// The service status message summer build clients receive (served at {ApiServicePath}/status/services).
        /// </summary>
        public string SummerServiceStatus { get; }

        /// <summary>
        /// Testing: every new session, on any build (the lobby builds and the final one), starts on this level (a symbol),
        /// whatever level or game type it was requested with, e.g. mpl_arena_a, or a Lone Echo level loaded from Lone Echo's
        /// game data added to the game servers' and players' installs. Null for the normal levels.
        /// </summary>
        public long? ForcedLevel { get; set; }

        /// <summary>
        /// When a player logs in to a lobby build that has no game server registered, this many game servers of that build
        /// are started for it on the game server hosts (0 = off). They don't count against the player's own requests.
        /// </summary>
        public int AutoStartGameServers { get; set; }
        #endregion

        #region Constructor
        public ServerSettings(ushort port = 777, string apiServicePath = "/api", string configServicePath = "/config",
            string loginServicePath = "/login", string matchingServicePath = "/matching",
            string serverdbServicePath = "/serverdb", string transactionServicePath = "/transaction", TimeSpan? disconnectedSessionTimeout = null,
            string? serverDbApiKey = null, bool serverDBValidateServerEndpoint = false, int serverDBValidateServerEndpointTimeout = 3000, bool forceIntoAnySessionIfCreationFails = false, bool favorPopulationOverPing = true,
            bool summerUnlockAll = true, TimeSpan? summerSessionLoadTimeout = null, string? summerNews = null, string? summerServiceStatus = null)
        {
            Port = port;
            ApiServicePath = apiServicePath;
            ConfigServicePath = configServicePath;
            LoginServicePath = loginServicePath;
            MatchingServicePath = matchingServicePath;
            ServerDBServicePath = serverdbServicePath;
            TransactionServicePath = transactionServicePath;

            SessionDisconnectedTimeout = disconnectedSessionTimeout ?? TimeSpan.FromMinutes(1);
            ServerDBApiKey = string.IsNullOrEmpty(serverDbApiKey) ? null : serverDbApiKey;
            ServerDBValidateServerEndpoint = serverDBValidateServerEndpoint;
            ServerDBValidateServerEndpointTimeout = serverDBValidateServerEndpointTimeout;
            ForceIntoAnySessionIfCreationFails = forceIntoAnySessionIfCreationFails;
            FavorPopulationOverPing = favorPopulationOverPing;
            SummerUnlockAll = summerUnlockAll;
            SummerSessionLoadTimeout = summerSessionLoadTimeout ?? TimeSpan.FromSeconds(30);
            SummerNews = summerNews ?? "Welcome back to the Echo VR summer lobby, running on EchoRelay.";
            SummerServiceStatus = summerServiceStatus ?? "Online";
        }
        #endregion

        #region Functions
        /// <summary>
        /// Generates a new <see cref="ServiceConfig"/> for the summer lobby build (rad15_summer).
        /// </summary>
        /// <param name="address">The address or domain to use for the base URL for all host endpoints.</param>
        /// <param name="serverConfig">Indicates whether sensitive gameserver-only fields should be included in the config.</param>
        /// <returns>Returns the generated <see cref="ServiceConfig"/>.</returns>
        public ServiceConfig GenerateSummerServiceConfig(string address, bool serverConfig = true)
        {
            // The summer build uses the same service hosts (it has no config/transaction/api services, but extra keys are ignored).
            return GenerateServiceConfig(address, serverConfig, SummerBuild.PublisherLock);
        }

        /// <summary>
        /// Generates a new <see cref="ServiceConfig"/> with the provided address string (e.g. "localhost", "123.123.123.123", etc)
        /// </summary>
        /// <param name="address">The address or domain to use for the base URL for all host endpoints.</param>
        /// <param name="serverConfig">Indicates whether sensitive gameserver-only fields should be included in the config.</param>
        /// <param name="publisherLock">The publisher/environment lock to generate with.</param>
        /// <returns>Returns the generated <see cref="ServiceConfig"/>.</returns>
        public ServiceConfig GenerateServiceConfig(string address, bool serverConfig = true, string publisherLock = "rad15_live", string? serverPlugin = null)
        {
            // Obtain our base host
            string webSocketHost = $"ws://{address}:{Port}";
            string httpHost = $"http://{address}:{Port}";

            // Construct our ServerDB path
            string serverDBHost = webSocketHost + ServerDBServicePath;
            if (ServerDBApiKey != null)
            {
                serverDBHost += $"?api_key={HttpUtility.UrlEncode(ServerDBApiKey)}";
            }

            // Return a new service config
            return new ServiceConfig(
                apiServiceHost: httpHost + ApiServicePath,
                configServiceHost: webSocketHost + ConfigServicePath,
                loginServiceHost: webSocketHost + LoginServicePath + $"?auth=AccountPassword&displayname=AccountName",
                matchingServiceHost: webSocketHost + MatchingServicePath,
                serverdbServiceHost: serverConfig ? serverDBHost : null,
                transactionServiceHost: webSocketHost + TransactionServicePath,
                socialServiceHost: webSocketHost + SocialServicePath,
                publisherLock: publisherLock,
                serverPlugin: serverPlugin
                );
        }
        #endregion
    }
}
