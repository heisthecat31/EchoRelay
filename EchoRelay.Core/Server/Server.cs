using EchoRelay.Core.Game;
using System.Text;
using Newtonsoft.Json.Linq;
using EchoRelay.Core.Server.Messages;
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
            listener.Prefixes.Add($"http://*:{Settings.Port}/");

            // Start the listener
            listener.Start();

            // Fire our started event
            OnServerStarted?.Invoke(this);

            // Enter a loop to accept new web socket connections.
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Upon receipt of a connection request, obtain the context and verify it is a web socket request.
                    HttpListenerContext listenerContext = await listener.GetContextAsync().WaitAsync(_cancellationTokenSource.Token);

                    // Verify the request is a web socket request
                    if (!listenerContext.Request.IsWebSocketRequest && TryHandleApiRequest(listenerContext))
                        continue;
                    if (!listenerContext.Request.IsWebSocketRequest)
                    {
                        TrafficCapture.Log($"HTTP {listenerContext.Request.HttpMethod} {listenerContext.Request.Url} from {listenerContext.Request.RemoteEndPoint} (not a websocket request, returning 400)");

                        // Return a bad request HTTP status code.
                        listenerContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        listenerContext.Response.Close();

                        // TODO: Log the interaction.

                        // Do not accept this client.
                        continue;
                    }

                    // Verify the IP is authorized against the ACL (if we have no ACL, we accept no connections).
                    AccessControlListResource? acl = Storage.AccessControlList.Get();
                    bool authorized = acl?.CheckAuthorized(listenerContext.Request.RemoteEndPoint.Address) ?? false;
                    OnAuthorizationResult?.Invoke(this, listenerContext.Request.RemoteEndPoint, authorized);
                    if (!authorized)
                    {
                        // TODO: Log the interaction.

                        // Do not accept this client.
                        continue;
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
                        continue;
                    }

                    // Game server hosts (EchoRelay.Host) connect to /hosts, with the ServerDB API key if one is set.
                    if (listenerContext.Request.Url?.LocalPath.ToLower().TrimEnd('/') == "/hosts")
                    {
                        string? key = listenerContext.Request.QueryString["api_key"];
                        if (Settings.ServerDBApiKey != null && key != Settings.ServerDBApiKey)
                        {
                            await webSocketContext.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid API key", CancellationToken.None);
                            continue;
                        }
                        string address = listenerContext.Request.RemoteEndPoint?.ToString() ?? "?";
                        _ = Task.Run(() => GameServerHosts.HandleConnection(webSocketContext.WebSocket, address));
                        continue;
                    }

                    // Try to obtain a service for this request path. If we could not, return an error to the client.
                    if (listenerContext.Request.Url == null || !_serviceMap.TryGetValue(listenerContext.Request.Url.LocalPath.ToLower() ?? "", out var service))
                    {
                        // Return a not found HTTP status code.
                        listenerContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        listenerContext.Response.Close();

                        // TODO: Log the interaction.
                        continue;
                    }

                    // Create a task to handle the new connection asynchronously (we do not await, so we can continue accepting connections).
                    var handleConnectionTask = Task.Run(() => service.HandleConnection(listenerContext, webSocketContext.WebSocket));
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
        /// Handles POST {api}/servers/request: {"build", "region", "displayname", "password"}. The player is identified like a
        /// lobby build login (their display name's account, with its password), then a game server host in the region decides.
        /// </summary>
        /// <returns>The reply: {"ok", "message"}.</returns>
        private async Task<JObject> HandleGameServerRequest(HttpListenerContext context)
        {
            static JObject Reply(bool ok, string message) => new JObject { ["ok"] = ok, ["message"] = message };
            JObject body;
            try
            {
                using StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                body = JObject.Parse(reader.ReadToEnd());
            }
            catch
            {
                return Reply(false, "The request wasn't understood.");
            }
            string build = body.Value<string>("build")?.Trim().ToLowerInvariant() ?? "";
            string displayName = body.Value<string>("displayname")?.Trim() ?? "";
            string password = body.Value<string>("password") ?? "";
            string? region = body.Value<string>("region");
            if (!GameServerBuilds.Names.ContainsKey(build))
                return Reply(false, "Unknown game version.");
            if (displayName.Length == 0)
                return Reply(false, "Enter your display name first.");

            // The same account a lobby build login with this display name uses; it must exist and have this password.
            XPlatformId accountId = LoginService.GetSummerAccountId(displayName);
            AccountResource? account = Storage.Accounts.Get(accountId);
            if (account == null || account.AccountLockHash == null)
                return Reply(false, "Log in to the game on this server once first, then request a game server.");
            if (!account.Authenticate(password))
                return Reply(false, "Wrong password for this display name.");
            if (account.Banned)
                return Reply(false, "This account is banned.");

            if (GameServerHosts.GetRegions(null).Count == 0)
                return Reply(false, "This server doesn't take game server requests.");
            try
            {
                GameServerRequestResult result = await GameServerHosts.Request(new GameServerRequest(build, accountId, displayName, context.Request.RemoteEndPoint?.Address), region);
                return Reply(result.Accepted, result.Message);
            }
            catch (Exception ex)
            {
                return Reply(false, "The server couldn't start a game server: " + ex.Message);
            }
        }

        private bool TryHandleApiRequest(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            string api = Settings.ApiServicePath.TrimEnd('/').ToLowerInvariant();
            JObject? response = null;
            // The christmas 2017 build's dedicated servers check {api}/status/serverdb before logging in.
            if (path == api + "/status/services" || path == api + "/status/serverdb")
                response = new JObject { ["available"] = true, ["message"] = Settings.SummerServiceStatus };
            else if (path == api + "/status/news")
                response = new JObject { ["message"] = Settings.SummerNews };
            else if (path == api + "/servers/regions")
            {
                // The regions that can start a game server of a build (?build=summer), for the installer's region picker.
                string? build = context.Request.QueryString["build"]?.Trim().ToLowerInvariant();
                response = new JObject { ["regions"] = new JArray(GameServerHosts.GetRegions(string.IsNullOrEmpty(build) ? null : build)) };
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
                TrafficCapture.Log($"{request} -> {json}");
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