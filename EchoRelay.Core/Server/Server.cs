using EchoRelay.Core.Game;
using System.Text;
using Newtonsoft.Json.Linq;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Services.Config;
using EchoRelay.Core.Server.Services.Login;
using EchoRelay.Core.Server.Services.Matching;
using EchoRelay.Core.Server.Services.ServerDB;
using EchoRelay.Core.Server.Services.Transaction;
using EchoRelay.Core.Server.Services.Social;
using EchoRelay.Core.Server.Storage;
using EchoRelay.Core.Server.Storage.Resources;
using EchoRelay.Core.Server.Storage.Types;
using EchoRelay.Core.Utils;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.WebSockets;
using static EchoRelay.Core.Server.Services.Service;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// A websocket server which implements Echo VR web services.
    /// </summary>
    public class Server
    {
        #region Properties
        /// <summary>
        /// The running state of the server.
        /// </summary>
        public bool Running { get; private set; }
        /// <summary>
        /// The source used to generate cancellation tokens for the server start/stop operations.
        /// </summary>
        public CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// The settings for the server to operate under.
        /// </summary>
        public ServerSettings Settings { get; private set; }
        /// <summary>
        /// The persistent storage layer for the server.
        /// </summary>
        public ServerStorage Storage { get; set; }

        /// <summary>
        /// A cache of symbols to use during server operations.
        /// This is reloaded from storage when the server is started.
        /// </summary>
        public SymbolCache SymbolCache { get; private set; }

        /// <summary>
        /// The <see cref="ConfigService"/> service hosted by this server.
        /// </summary>
        public ConfigService ConfigService { get; private set; }

        /// <summary>
        /// The <see cref="LoginService"/> service hosted by this server.
        /// </summary>
        public LoginService LoginService { get; private set; }

        /// <summary>
        /// The <see cref="MatchingService"/> service hosted by this server.
        /// </summary>
        public MatchingService MatchingService { get; private set; }

        /// <summary>
        /// The <see cref="ServerDBService"/> service hosted by this server.
        /// </summary>
        public ServerDBService ServerDBService { get; private set; }

        /// <summary>
        /// The <see cref="TransactionService"/> service hosted by this server.
        /// </summary>
        public TransactionService TransactionService { get; private set; }

        /// <summary>
        /// The <see cref="SocialService"/> service hosted by this server (summer build parties and friends).
        /// </summary>
        public SocialService SocialService { get; private set; }
        /// <summary>
        /// The IP address of the current server. This is obtained by querying an online service. If it fails to fetch, it will be null.
        /// </summary>
        public IPAddress? PublicIPAddress { get; private set; }

        /// <summary>
        /// A map of request paths to <see cref="Service"/>s which serve them.
        /// </summary>
        private ReadOnlyDictionary<string, Service> _serviceMap;
        #endregion

        #region Events
        /// <summary>
        /// Event for a <see cref="Server"/> having been started.
        /// </summary>
        /// <param name="server">The <see cref="Server"/> which was started.</param>
        public delegate void ServerStartedEventHandler(Server server);
        /// <summary>
        /// Event for a <see cref="Server"/> having been started.
        /// </summary>
        public event ServerStartedEventHandler? OnServerStarted;

        /// <summary>
        /// Event for a <see cref="Server"/> having been stopped.
        /// </summary>
        /// <param name="server">The <see cref="Server"/> which was stopped.</param>
        public delegate void ServerStoppedEventHandler(Server server);
        /// <summary>
        /// Event for a <see cref="Server"/> having been stopped.
        /// </summary>
        public event ServerStoppedEventHandler? OnServerStopped;

        /// <summary>
        /// Event for a client having their IP address authorized by the Access Control Lists (ACLs), before connecting to a service.
        /// </summary>
        /// <param name="server">The <see cref="Server"/> which the authorization took place under.</param>
        /// <param name="client">The IP endpoint which attempted to authorize themselves with the server.</param>
        /// <param name="authorized">The result of the authorization.</param>
        public delegate void AuthorizationResultEventHandler(Server server, IPEndPoint client, bool authorized);
        /// <summary>
        /// Event for a <see cref="Peer"/> having their IP address authorized by the Access Control Lists (ACLs), before connecting to a service.
        /// </summary>
        public event AuthorizationResultEventHandler? OnAuthorizationResult;

        /// <summary>
        /// Forwarded event from all connected services: <see cref="Service.OnPeerConnected"/>.
        /// </summary>
        public event PeerConnectedEventHandler? OnServicePeerConnected;
        /// <summary>
        /// Forwarded event from all connected services: <see cref="Service.OnPeerDisconnected"/>.
        /// </summary>
        public event PeerDisconnectedEventHandler? OnServicePeerDisconnected;
        /// <summary>
        /// Event for a <see cref="Peer"/> authenticating within a <see cref="Service"/>, with a given <see cref="XPlatformId"/>.
        /// </summary>
        public event Peer.AuthenticatedEventHandler? OnServicePeerAuthenticated;
        /// <summary>
        /// Forwarded event from all connected services: <see cref="Service.OnPacketReceived"/>.
        /// </summary>
        public event Peer.PacketReceivedEventHandler? OnServicePacketReceived;
        /// <summary>
        /// Forwarded event from all connected services: <see cref="Service.OnPacketSent"/>.
        /// </summary>
        public event Peer.PacketSentEventHandler? OnServicePacketSent;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="Server"/> with the provided arguments.
        /// </summary>
        /// <param name="port">The port to bind the websocket server to.</param>
        public Server(ServerStorage storage, ServerSettings settings)
        {
            // Set our properties.
            Storage = storage;
            Settings = settings;
            PublicIPAddress = null;
   
            // Create our services
            ConfigService = new ConfigService(this);
            RegisterServiceEvents(ConfigService);

            LoginService = new LoginService(this);
            RegisterServiceEvents(LoginService);

            MatchingService = new MatchingService(this);
            RegisterServiceEvents(MatchingService);

            ServerDBService = new ServerDBService(this);
            RegisterServiceEvents(ServerDBService);

            TransactionService = new TransactionService(this);
            RegisterServiceEvents(TransactionService);

            SocialService = new SocialService(this);
            RegisterServiceEvents(SocialService);

            // Create a map of our services
            _serviceMap = new Dictionary<string, Service>
            {
                { Settings.ConfigServicePath.ToLower(), ConfigService },
                { Settings.LoginServicePath.ToLower(), LoginService },
                { Settings.MatchingServicePath.ToLower(), MatchingService },
                { Settings.ServerDBServicePath.ToLower(), ServerDBService },
                { Settings.TransactionServicePath.ToLower(), TransactionService },
                { Settings.SocialServicePath.ToLower(), SocialService },
            }.AsReadOnly();
        }
        #endregion

        #region Functions
        /// <summary>
        /// Starts the server and its underlying services.
        /// </summary>
        /// <param name="cancellationToken">An optional cancellation token which will be used to stop the server.</param>
        /// <returns>A task representing the server execution.</returns>
        /// <exception cref="InvalidOperationException">An exception thrown if the server is already started when this method is called.</exception>
        public async Task Start(CancellationTokenSource? cancellationTokenSource = null)
        {
            // If we are running already, throw an exception.
            if (Running)
            {
                throw new InvalidOperationException("Server cannot be started if it is already in a running state.");
            }

            // Obtain our public IP address
            PublicIPAddress = await IPAddressUtils.GetExternalIPAddress();

            // Set our state to running
            _cancellationTokenSource = cancellationTokenSource ?? new CancellationTokenSource();
            Running = true;

            // Load the symbol cache from our storage
            SymbolCache = Storage.SymbolCache.Get() ?? new SymbolCache();

            // Create an HTTP listener that hosts over the provided port.
            HttpListener listener = new HttpListener();
            // All addresses (needs administrator); ECHORELAY_LISTEN_HOST=localhost listens locally only, e.g. to test without it.
            string listenHost = Environment.GetEnvironmentVariable("ECHORELAY_LISTEN_HOST") is string host && host.Length > 0 ? host : "*";
            listener.Prefixes.Add($"http://{listenHost}:{Settings.Port}/");

            // Start the listener
            listener.Start();

            // Fire our started event
            OnServerStarted?.Invoke(this);

            // Close requested game servers that nobody is playing on.
            _ = GameServerHosts.RunIdleMonitor(this, _cancellationTokenSource.Token);

            // Enter a loop to accept new web socket connections.
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Only accept here: each connection is handled on its own task, so a slow client (a websocket handshake or an
                    // API reply over a bad connection) doesn't hold up everyone else's, and an error with one connection
                    // doesn't stop the server from accepting new ones (it used to end this loop, and with it all new logins).
                    HttpListenerContext listenerContext;
                    try
                    {
                        listenerContext = await listener.GetContextAsync().WaitAsync(_cancellationTokenSource.Token);
                    }
                    catch (Exception e) when (e is not OperationCanceledException && e is not TimeoutException && listener.IsListening)
                    {
                        TrafficCapture.Log($"Accepting a connection failed: {e.GetType().Name} {e.Message}");
                        continue;
                    }
                    _ = Task.Run(() => HandleContext(listenerContext));
                }
            }
            catch (TimeoutException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            // Ensure our listener is closed
            listener.Close();

            // Set our state to not running.
            Running = false;

            // Fire our stopped event
            OnServerStopped?.Invoke(this);
        }

        /// <summary>
        /// Answers the plain HTTP API requests the summer build makes (dbgcore.dll points them at the API service path):
        /// {api}/status/services (menu service status), {api}/status/news (lobby news board) and {api}/status/serverdb.
        /// </summary>
        /// <param name="context">The HTTP request context.</param>
        /// <returns>True if the request was answered.</returns>
        /// <summary>
        /// The PCs that start game servers players request from the installer, by region: this PC (set by EchoRelay.App) and
        /// EchoRelay.Host instances connected to {server}/hosts.
        /// </summary>
        public GameServerHosts GameServerHosts { get; } = new GameServerHosts();

        /// <summary>
        /// Joins asked for over the API ({api}/matches/join), taken by the account's next matchmaking request.
        /// </summary>
        public MatchJoins MatchJoins { get; } = new MatchJoins();

        /// <summary>
        /// Fired with the address of a game server host (EchoRelay.Host) turned away for a missing or wrong API key.
        /// </summary>
        public event Action<string>? OnHostRejected;

        /// <summary>
        /// Handles POST {api}/servers/request: {"build", "region", "displayname", "password"}. The player is identified like a
        /// lobby build login (their display name's account, with its password), then a game server host in the region decides.
        /// </summary>
        /// <returns>The reply: {"ok", "message"}.</returns>
        private static JObject Reply(bool ok, string message) => new JObject { ["ok"] = ok, ["message"] = message };

        /// <summary>
        /// Reads an API request's JSON body; null if it isn't JSON.
        /// </summary>
        private static JObject? ReadBody(HttpListenerContext context)
        {
            try
            {
                using StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                return JObject.Parse(reader.ReadToEnd());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The player behind an API request: the account a lobby build login with the body's "displayname" uses, which must
        /// exist and have the body's "password". The id, or why not (a reply).
        /// </summary>
        private (XPlatformId? Id, JObject? Refusal) ApiAccount(JObject body, string action)
        {
            string displayName = body.Value<string>("displayname")?.Trim() ?? "";
            string password = body.Value<string>("password") ?? "";
            if (displayName.Length == 0)
                return (null, Reply(false, "Enter your display name first."));
            XPlatformId accountId = LoginService.GetSummerAccountId(displayName);
            AccountResource? account = Storage.Accounts.Get(accountId);
            if (account == null || account.AccountLockHash == null)
                return (null, Reply(false, $"Log in to the game on this server once first, then {action}."));
            if (!account.Authenticate(password))
                return (null, Reply(false, "Wrong password for this display name."));
            if (account.Banned)
                return (null, Reply(false, "This account is banned."));
            return (accountId, null);
        }

        private async Task<JObject> HandleGameServerRequest(HttpListenerContext context)
        {
            JObject? body = ReadBody(context);
            if (body == null)
                return Reply(false, "The request wasn't understood.");
            string build = body.Value<string>("build")?.Trim().ToLowerInvariant() ?? "";
            string displayName = body.Value<string>("displayname")?.Trim() ?? "";
            string? region = body.Value<string>("region");
            if (!GameServerBuilds.Names.ContainsKey(build))
                return Reply(false, "Unknown game version.");
            var (id, refusal) = ApiAccount(body, "request a game server");
            if (refusal != null)
                return refusal;
            XPlatformId accountId = id!;

            if (GameServerHosts.GetRegions(null).Count == 0)
                return Reply(false, "This server doesn't take game server requests.");
            try
            {
                GameServerRequestResult result = await GameServerHosts.Request(new GameServerRequest(build, accountId, displayName, context.Request.RemoteEndPoint?.Address), region);
                if (result.Accepted)
                    MatchJoins.SetOwnServer(accountId, build);
                return Reply(result.Accepted, result.Message);
            }
            catch (Exception ex)
            {
                return Reply(false, "The server couldn't start a game server: " + ex.Message);
            }
        }

        /// <summary>
        /// The build id (GameServerBuilds.Names) a game server runs, or null.
        /// </summary>
        private static string? BuildOf(RegisteredGameServer server)
            => GameServerBuilds.Names.Keys.FirstOrDefault(build => GameServerBuilds.ServesBuild(build, server.VersionLock));

        /// <summary>
        /// GET {api}/matches[?build=halloween]: the public sessions running on lobby build game servers. Who plays in them
        /// isn't listed. A private match isn't listed; it's joined by its id.
        /// </summary>
        private JObject ListMatches(HttpListenerContext context)
        {
            string? build = context.Request.QueryString["build"]?.Trim().ToLowerInvariant();
            JArray matches = new JArray();
            foreach (RegisteredGameServer server in ServerDBService.Registry.RegisteredGameServers.Values)
            {
                string? serverBuild = BuildOf(server);
                if (serverBuild == null || !server.SessionStarted || server.SessionLobbyType == ERGameServerStartSession.LobbyType.Private)
                    continue;
                if (!string.IsNullOrEmpty(build) && build != serverBuild)
                    continue;
                matches.Add(MatchJson(server));
            }
            return new JObject { ["matches"] = matches };
        }

        /// <summary>
        /// POST {api}/matches/join: {"id", "displayname", "password"}. The account's next matchmaking request (Play in the
        /// game) goes to that session, public or private, if it still has room then.
        /// </summary>
        private JObject HandleMatchJoin(HttpListenerContext context)
        {
            JObject? body = ReadBody(context);
            if (body == null)
                return Reply(false, "The request wasn't understood.");
            if (!Guid.TryParse(body.Value<string>("id")?.Trim(), out Guid session))
                return Reply(false, "That isn't a match id.");
            var (id, refusal) = ApiAccount(body, "join a match");
            if (refusal != null)
                return refusal;
            RegisteredGameServer? server = ServerDBService.Registry.GetGameServer(session);
            if (server == null || BuildOf(server) == null)
                return Reply(false, "That match isn't running.");
            if (!server.HasRoom)
                return Reply(false, "That match is full, locked or has just ended.");
            MatchJoins.Set(id!, session);
            JObject joined = Reply(true, $"Press Play in the game within {MatchJoins.Lifetime.TotalMinutes:0} minutes to join the match.");
            joined["build"] = BuildOf(server);
            return joined;
        }

        /// <summary>
        /// A game type as players call it ("Arena"), or its symbol's name.
        /// </summary>
        private static string? ModeName(string? gameType) => gameType switch
        {
            "social_2.0" or "social" => "Lobby",
            "social_2.0_private" => "Private lobby",
            "echo_arena" or "arena" => "Arena",
            "echo_arena_private" => "Private arena",
            "echo_combat" => "Combat",
            "echo_combat_private" => "Private combat",
            "echo_arenacombat" => "Arena combat",
            _ => gameType,
        };

        /// <summary>
        /// A running session as the API lists it.
        /// </summary>
        private JObject MatchJson(RegisteredGameServer server) => new JObject
        {
            ["id"] = server.SessionId!.Value.ToString(),
            ["build"] = BuildOf(server),
            ["build_name"] = BuildOf(server) is string b ? GameServerBuilds.Names[b] : null,
            ["gametype"] = server.SessionGameTypeSymbol is long g ? SymbolCache.GetName(g) : null,
            ["mode"] = ModeName(server.SessionGameTypeSymbol is long m ? SymbolCache.GetName(m) : null),
            ["level"] = server.SessionLevelSymbol is long l ? SymbolCache.GetName(l) : null,
            ["region"] = SymbolCache.GetName(server.RegionSymbol),
            ["private"] = server.SessionLobbyType == ERGameServerStartSession.LobbyType.Private,
            ["players"] = server.SessionPlayerCount,
            ["limit"] = server.SessionPlayerLimits.TotalPlayerLimit,
            ["joinable"] = server.HasRoom,
        };

        /// <summary>
        /// POST {api}/matches/current: {"displayname", "password"}. The match the account plays in now ({"match": null} if
        /// none), with its id to give friends.
        /// </summary>
        private async Task<JObject> HandleCurrentMatch(HttpListenerContext context)
        {
            JObject? body = ReadBody(context);
            if (body == null)
                return Reply(false, "The request wasn't understood.");
            var (id, refusal) = ApiAccount(body, "look up your match");
            if (refusal != null)
                return refusal;
            foreach (RegisteredGameServer server in ServerDBService.Registry.RegisteredGameServers.Values)
            {
                if (!server.SessionStarted || BuildOf(server) == null)
                    continue;
                foreach (var (_, peer) in await server.GetPlayers())
                    if (peer?.UserId == id)
                    {
                        JObject found = Reply(true, "");
                        found["match"] = MatchJson(server);
                        return found;
                    }
            }
            JObject none = Reply(true, "You aren't in a match.");
            none["match"] = null;
            return none;
        }

        private bool TryHandleApiRequest(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            string api = Settings.ApiServicePath.TrimEnd('/').ToLowerInvariant();
            JObject? response = null;
            // The christmas 2017 build's dedicated servers check {api}/status/serverdb before logging in; halloween 2017's
            // append "prod/status/serverdb" to the API host as it is (e.g. /apiprod/status/serverdb).
            if (path == api + "/status/services" || path == api + "/status/serverdb"
                || (path.StartsWith(api) && path.EndsWith("/status/serverdb")))
                response = new JObject { ["available"] = true, ["message"] = Settings.SummerServiceStatus };
            else if (path == api + "/status/news")
                response = new JObject { ["message"] = Settings.SummerNews };
            else if (path == api + "/players")
            {
                // Who's online, for the installer: display names and game versions only (no ids or addresses).
                JArray players = new JArray(LoginService.GetOnlinePlayers()
                    .Select(player => new JObject { ["name"] = player.Name, ["version"] = player.Version }));
                response = new JObject { ["players"] = players };
            }
            else if (path == api + "/servers/regions")
            {
                // The regions that can start a game server of a build (?build=summer), for the installer's region picker.
                string? build = context.Request.QueryString["build"]?.Trim().ToLowerInvariant();
                response = new JObject { ["regions"] = new JArray(GameServerHosts.GetRegions(string.IsNullOrEmpty(build) ? null : build)) };
            }
            else if (path == api + "/matches")
                response = ListMatches(context);
            else if (path == api + "/matches/join")
                response = HandleMatchJoin(context);
            else if (path == api + "/matches/current")
            {
                _ = Task.Run(async () => WriteApiResponse(context, await HandleCurrentMatch(context)));
                return true;
            }
            else if (path == api + "/servers/request")
            {
                // Answered once a game server host has decided, without holding up the accept loop.
                _ = Task.Run(async () => WriteApiResponse(context, await HandleGameServerRequest(context)));
                return true;
            }
            if (response == null)
                return false;
            WriteApiResponse(context, response);
            return true;
        }

        /// <summary>
        /// Handles one accepted HTTP request: an API call, a game server host, or a websocket connection to a service (for as
        /// long as it stays connected). Errors only affect this connection.
        /// </summary>
        private async Task HandleContext(HttpListenerContext listenerContext)
        {
            try
            {
                // Verify the request is a web socket request
                if (!listenerContext.Request.IsWebSocketRequest && TryHandleApiRequest(listenerContext))
                    return;
                if (!listenerContext.Request.IsWebSocketRequest)
                {
                    TrafficCapture.Log($"HTTP {listenerContext.Request.HttpMethod} {listenerContext.Request.Url} from {listenerContext.Request.RemoteEndPoint} (not a websocket request, returning 400)");

                    // Return a bad request HTTP status code.
                    listenerContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    listenerContext.Response.Close();

                    // TODO: Log the interaction.

                    // Do not accept this client.
                    return;
                }

                // Verify the IP is authorized against the ACL (if we have no ACL, we accept no connections).
                AccessControlListResource? acl = Storage.AccessControlList.Get();
                bool authorized = acl?.CheckAuthorized(listenerContext.Request.RemoteEndPoint.Address) ?? false;
                OnAuthorizationResult?.Invoke(this, listenerContext.Request.RemoteEndPoint, authorized);
                if (!authorized)
                {
                    // TODO: Log the interaction.

                    // Do not accept this client.
                    return;
                }

                // Attempt to accept the web socket connection.
                WebSocketContext webSocketContext;
                try
                {
                    webSocketContext = await listenerContext.AcceptWebSocketAsync(subProtocol: null);
                }
                catch (Exception e)
                {
                    // Return an internal server error HTTP status code.
                    listenerContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    listenerContext.Response.Close();

                    // TODO: Log the exception.
                    return;
                }

                // Game server hosts (EchoRelay.Host) connect to /hosts, with the ServerDB API key if one is set.
                if (listenerContext.Request.Url?.LocalPath.ToLower().TrimEnd('/') == "/hosts")
                {
                    string? key = listenerContext.Request.QueryString["api_key"];
                    if (Settings.ServerDBApiKey != null && key != Settings.ServerDBApiKey)
                    {
                        // CloseOutputAsync: the host has usually sent its hello already, which CloseAsync (waiting for the
                        // close reply) choked on; that exception used to crash the App and stop the server accepting anyone.
                        OnHostRejected?.Invoke(listenerContext.Request.RemoteEndPoint?.ToString() ?? "?");
                        await webSocketContext.WebSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Invalid API key", CancellationToken.None);
                        webSocketContext.WebSocket.Abort();
                        return;
                    }
                    string address = listenerContext.Request.RemoteEndPoint?.ToString() ?? "?";
                    await GameServerHosts.HandleConnection(webSocketContext.WebSocket, address);
                    return;
                }

                // Try to obtain a service for this request path. If we could not, return an error to the client.
                if (listenerContext.Request.Url == null || !_serviceMap.TryGetValue(listenerContext.Request.Url.LocalPath.ToLower() ?? "", out var service))
                {
                    // Return a not found HTTP status code.
                    listenerContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    listenerContext.Response.Close();

                    // TODO: Log the interaction.
                    return;
                }

                // Handle the connection for as long as it stays open.
                await service.HandleConnection(listenerContext, webSocketContext.WebSocket);
            }
            catch (Exception e)
            {
                // A game that quits, crashes or loses its network just drops the connection (Echo never completes the
                // close handshake), so those aren't worth logging; anything else is.
                if (!IsDroppedConnection(e))
                    TrafficCapture.Log($"Connection from {listenerContext.Request.RemoteEndPoint} failed: {e.GetType().Name} {e.Message}");
                try { listenerContext.Response.Abort(); } catch { }
            }
        }

        /// <summary>
        /// Whether an exception from a websocket connection is just the other end going away.
        /// </summary>
        private static bool IsDroppedConnection(Exception e)
        {
            if (e is not WebSocketException webSocketException)
                return false;
            return webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
                || e.InnerException is HttpListenerException || e.InnerException is IOException || e.InnerException is ObjectDisposedException;
        }

        /// <summary>
        /// Writes an API request's JSON response and closes it.
        /// </summary>
        private static void WriteApiResponse(HttpListenerContext context, JObject response)
        {
            // Read the request details up front: closing the response disposes the context, so reading them afterwards
            // throws (which used to make every successful response log as "failed: Cannot access a disposed object").
            string request = $"HTTP {context.Request.HttpMethod} {context.Request.Url} from {context.Request.RemoteEndPoint}";
            try
            {
                string json = response.ToString(Newtonsoft.Json.Formatting.None);
                byte[] body = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.Close();
                TrafficCapture.LogAll($"{request} -> {json}");
            }
            catch (Exception e)
            {
                TrafficCapture.Log($"{request} failed: {e.Message}");
            }
        }

        /// <summary>
        /// Stops the server and its underlying services.
        /// Note: Servers are run in another task. This method may return before the server has stopped.
        /// </summary>
        public void Stop()
        {
            // Cancel any cancellation token we have now.
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Registers a <see cref="Service"/>'s events to be forwarded to ones provided on a <see cref="Server"/>-level here.
        /// </summary>
        /// <param name="service">The <see cref="Service"/> to forward events from.</param>
        public void RegisterServiceEvents(Service service)
        {
            // Forward all events from the service.
            service.OnPeerConnected += Service_OnPeerConnected;
            service.OnPeerDisconnected += Service_OnPeerDisconnected;
            service.OnPeerAuthenticated += Service_OnPeerAuthenticated;
            service.OnPacketSent += Service_OnPacketSent;
            service.OnPacketReceived += Service_OnPacketReceived;
        }
        #endregion

        #region Event Handlers
        private void Service_OnPeerConnected(Service service, Peer peer)
        {
            OnServicePeerConnected?.Invoke(service, peer);
        }
        private void Service_OnPeerDisconnected(Service service, Peer peer)
        {
            OnServicePeerDisconnected?.Invoke(service, peer);
        }
        private void Service_OnPeerAuthenticated(Service service, Peer peer, XPlatformId userId)
        {
            OnServicePeerAuthenticated?.Invoke(service, peer, userId);
        }
        private void Service_OnPacketReceived(Service service, Peer sender, Packet packet)
        {
            OnServicePacketReceived?.Invoke(service, sender, packet);
        }

        private void Service_OnPacketSent(Service service, Peer sender, Packet packet)
        {
            OnServicePacketSent?.Invoke(service, sender, packet);
        }
        #endregion
    }
}