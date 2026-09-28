using EchoRelay.Core.Game;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// The PCs that start game servers players request from the installer, by region (e.g. "EU", "US"). The PC running
    /// EchoRelay.App can be one (Settings > Game Server Requests); other PCs run EchoRelay.Host, which connects to
    /// {server}/hosts. A request goes to a host in the region the player picked that has the requested build.
    /// </summary>
    public class GameServerHosts
    {
        /// <summary>
        /// A PC that can start game servers.
        /// </summary>
        private class Host
        {
            public string Region = "";
            public string Name = "";
            public HashSet<string> Builds = new HashSet<string>();
            public Func<GameServerRequest, Task<GameServerRequestResult>> Start = null!;
            public bool Local;
        }

        private readonly object _lock = new object();
        private readonly List<Host> _hosts = new List<Host>();

        /// <summary>
        /// Fired with a line describing a host connecting/disconnecting or a request, for the host's log.
        /// </summary>
        public event Action<string>? OnLog;

        /// <summary>
        /// Sets (or, with no builds, removes) the host running on this PC.
        /// </summary>
        public void SetLocalHost(string region, string name, IEnumerable<string> builds, Func<GameServerRequest, GameServerRequestResult> start)
        {
            lock (_lock)
            {
                _hosts.RemoveAll(host => host.Local);
                HashSet<string> buildSet = new HashSet<string>(builds);
                if (buildSet.Count == 0)
                    return;
                _hosts.Insert(0, new Host
                {
                    Region = NormalizeRegion(region),
                    Name = name,
                    Builds = buildSet,
                    Start = request => Task.FromResult(start(request)),
                    Local = true,
                });
            }
        }

        private static string NormalizeRegion(string? region)
        {
            string trimmed = (region ?? "").Trim();
            return trimmed.Length == 0 ? "Main" : trimmed.Length > 24 ? trimmed.Substring(0, 24) : trimmed;
        }

        /// <summary>
        /// The regions that can start a game server of a build (all builds if null), each once, local host's first.
        /// </summary>
        public List<string> GetRegions(string? build)
        {
            lock (_lock)
                return _hosts.Where(host => build == null || host.Builds.Contains(build))
                    .Select(host => host.Region).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Asks a host in the region (any region if null) with the build to start a game server. Hosts are tried in turn until
        /// one accepts (a full host declines).
        /// </summary>
        public async Task<GameServerRequestResult> Request(GameServerRequest request, string? region)
        {
            List<Host> candidates;
            lock (_lock)
                candidates = _hosts.Where(host => host.Builds.Contains(request.Build) &&
                    (string.IsNullOrWhiteSpace(region) || string.Equals(host.Region, region.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            string buildName = GameServerBuilds.Names[request.Build];
            if (candidates.Count == 0)
                return new GameServerRequestResult(false, string.IsNullOrWhiteSpace(region)
                    ? $"No PC on this server hosts {buildName} game servers right now."
                    : $"No PC in {region} hosts {buildName} game servers right now.");
            GameServerRequestResult? last = null;
            foreach (Host host in candidates)
            {
                try
                {
                    last = await host.Start(request);
                }
                catch (Exception ex)
                {
                    last = new GameServerRequestResult(false, "The game server host didn't answer: " + ex.Message);
                }
                OnLog?.Invoke($"[HOSTS] {request.DisplayName} requested a {request.Build} game server in {host.Region} from '{host.Name}': " +
                    $"{(last.Accepted ? "started" : "declined")} - {last.Message}\n");
                if (last.Accepted)
                    return last;
            }
            return last!;
        }

        /// <summary>
        /// Runs a remote host's connection ({server}/hosts): {"t":"hello","region","name","builds":[...]} from the host, then
        /// {"t":"start","rid","build","requester","name"} requests answered by {"t":"result","rid","ok","message"}.
        /// </summary>
        public async Task HandleConnection(WebSocket socket, string address)
        {
            Host? host = null;
            ConcurrentDictionary<long, TaskCompletionSource<GameServerRequestResult>> pending = new ConcurrentDictionary<long, TaskCompletionSource<GameServerRequestResult>>();
            SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
            long nextRequestId = 1;
            byte[] buffer = new byte[16384];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    // Read one text message.
                    using MemoryStream message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (received.MessageType == WebSocketMessageType.Close)
                            return;
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage && message.Length < 1 << 20);
                    JObject json;
                    try
                    {
                        json = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                    }
                    catch
                    {
                        continue;
                    }

                    string type = json.Value<string>("t") ?? "";
                    if (type == "hello" && host == null)
                    {
                        host = new Host
                        {
                            Region = NormalizeRegion(json.Value<string>("region")),
                            Name = json.Value<string>("name") ?? address,
                            Builds = new HashSet<string>((json["builds"] as JArray ?? new JArray()).Select(b => b.ToString().ToLowerInvariant()).Where(GameServerBuilds.Names.ContainsKey)),
                        };
                        host.Start = async request =>
                        {
                            long id = Interlocked.Increment(ref nextRequestId);
                            TaskCompletionSource<GameServerRequestResult> completion = new TaskCompletionSource<GameServerRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                            pending[id] = completion;
                            JObject start = new JObject
                            {
                                ["t"] = "start",
                                ["rid"] = id,
                                ["build"] = request.Build,
                                ["requester"] = request.Requester.ToString(),
                                ["name"] = request.DisplayName,
                            };
                            byte[] bytes = Encoding.UTF8.GetBytes(start.ToString(Newtonsoft.Json.Formatting.None));
                            await sendLock.WaitAsync();
                            try
                            {
                                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                            finally
                            {
                                sendLock.Release();
                            }
                            Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                            pending.TryRemove(id, out _);
                            return finished == completion.Task ? completion.Task.Result : new GameServerRequestResult(false, "The game server host didn't answer in time.");
                        };
                        lock (_lock)
                            _hosts.Add(host);
                        OnLog?.Invoke($"[HOSTS] '{host.Name}' ({address}) connected: region {host.Region}, builds {string.Join(", ", host.Builds)}\n");
                    }
                    else if (type == "result" && pending.TryRemove(json.Value<long>("rid"), out var completion))
                    {
                        completion.TrySetResult(new GameServerRequestResult(json.Value<bool>("ok"), json.Value<string>("message") ?? ""));
                    }
                }
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                if (host != null)
                {
                    lock (_lock)
                        _hosts.Remove(host);
                    OnLog?.Invoke($"[HOSTS] '{host.Name}' ({address}) disconnected\n");
                }
                foreach (var completion in pending.Values)
                    completion.TrySetResult(new GameServerRequestResult(false, "The game server host disconnected."));
            }
        }
    }

    /// <summary>
    /// Starts requested game servers on this PC, within limits: a headless server of the requested build, from its
    /// executable. Used by EchoRelay.App and EchoRelay.Host.
    /// </summary>
    public class GameServerLauncher
    {
        private readonly object _lock = new object();
        private readonly List<(string requester, Process process)> _started = new List<(string, Process)>();

        /// <summary>The game executable of each build this PC can start ("summer" -> path).</summary>
        public IReadOnlyDictionary<string, string> Executables { get; set; } = new Dictionary<string, string>();

        /// <summary>How many requested game servers each player can have running at once.</summary>
        public int PerPlayer { get; set; } = 2;

        /// <summary>How many requested game servers can run at once, from all players.</summary>
        public int Max { get; set; } = 6;

        /// <summary>
        /// The builds this PC can start (whose executable exists).
        /// </summary>
        public IEnumerable<string> Builds => Executables.Where(pair => File.Exists(pair.Value)).Select(pair => pair.Key);

        public GameServerRequestResult Start(GameServerRequest request)
        {
            lock (_lock)
            {
                string buildName = GameServerBuilds.Names.TryGetValue(request.Build, out string? name) ? name : request.Build;
                if (!Executables.TryGetValue(request.Build, out string? executable) || !File.Exists(executable))
                    return new GameServerRequestResult(false, $"This PC doesn't host {buildName} game servers.");
                _started.RemoveAll(entry => HasExited(entry.process));
                string requester = request.Requester.ToString();
                int perPlayer = Math.Max(1, PerPlayer);
                int mine = _started.Count(entry => entry.requester == requester);
                if (mine >= perPlayer)
                    return new GameServerRequestResult(false, $"You already have {mine} requested game server{(mine == 1 ? "" : "s")} running (the limit is {perPlayer}).");
                if (_started.Count >= Math.Max(1, Max))
                    return new GameServerRequestResult(false, "This PC is running as many requested game servers as it allows. Try again later, or another region.");
                Process? process = GameLauncher.Launch(executable, GameLauncher.LaunchRole.Server, noOVR: true, headless: true);
                if (process == null)
                    return new GameServerRequestResult(false, "The game server didn't start.");
                _started.Add((requester, process));
                return new GameServerRequestResult(true, $"Starting a {buildName} game server. It takes about half a minute; then press Play in the game.");
            }
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }
}
