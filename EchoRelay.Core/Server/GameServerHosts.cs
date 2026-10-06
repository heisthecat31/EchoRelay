using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Services.ServerDB;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
            /// <summary>Closes the requested game servers on these UDP ports that this host started.</summary>
            public Func<ushort[], Task> StopIdle = null!;
            public bool Local;
            /// <summary>Where a remote host connected from (its game servers connect from there too).</summary>
            public IPAddress? Address;
        }

        /// <summary>
        /// How long a game server can go without players before the host that started it (on request) closes it.
        /// </summary>
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How many game servers a player can have started (on any host, of any version) within <see cref="RequestWindow"/>.
        /// Only against spamming: how many can run at once is each host's per-player limit (2 per version). Servers that
        /// crashed or were closed empty still count here, so this is higher than that.
        /// </summary>
        public const int RequestsPerWindow = 4;

        /// <summary>
        /// How many game servers can be requested from one address within <see cref="RequestWindow"/>, whatever display
        /// names ask (a new name is a new account). Higher than <see cref="RequestsPerWindow"/>: players share an address
        /// at home.
        /// </summary>
        public const int RequestsPerAddress = 8;
        public static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(10);

        /// <summary>When each player's accepted requests were made, within the last <see cref="RequestWindow"/>.</summary>
        private readonly Dictionary<string, List<DateTime>> _recentRequests = new Dictionary<string, List<DateTime>>();

        /// <summary>
        /// If a player has used up their requests for now, how long until they can make another.
        /// </summary>
        private TimeSpan? RequestCooldown(string requester, DateTime now, int limit = RequestsPerWindow)
        {
            lock (_lock)
            {
                if (!_recentRequests.TryGetValue(requester, out List<DateTime>? times))
                    return null;
                times.RemoveAll(time => now - time >= RequestWindow);
                if (times.Count == 0)
                    _recentRequests.Remove(requester);
                return times.Count >= limit ? times.Min() + RequestWindow - now : null;
            }
        }

        private void RecordRequest(string requester, DateTime now)
        {
            lock (_lock)
            {
                if (!_recentRequests.TryGetValue(requester, out List<DateTime>? times))
                    _recentRequests[requester] = times = new List<DateTime>();
                times.Add(now);
            }
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
        public void SetLocalHost(string region, string name, IEnumerable<string> builds, Func<GameServerRequest, GameServerRequestResult> start,
            Func<ushort[], int>? stopIdle = null)
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
                    StopIdle = ports =>
                    {
                        int stopped = stopIdle?.Invoke(ports) ?? 0;
                        if (stopped > 0)
                            OnLog?.Invoke($"[HOSTS] Closed {stopped} requested game server{(stopped == 1 ? "" : "s")} on this PC, empty for {IdleTimeout.TotalMinutes:0} minutes\n");
                        return Task.CompletedTask;
                    },
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
            string requester = request.Requester.ToString();
            if (RequestCooldown(requester, DateTime.UtcNow) is TimeSpan wait)
            {
                int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
                OnLog?.Invoke($"[HOSTS] {request.DisplayName} requested a {request.Build} game server: declined, {RequestsPerWindow} already in the last {RequestWindow.TotalMinutes:0} minutes\n");
                return new GameServerRequestResult(false, $"You can request {RequestsPerWindow} game servers every {RequestWindow.TotalMinutes:0} minutes. " +
                    $"Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
            }
            string? address = request.Address == null ? null : "address:" + request.Address;
            if (address != null && RequestCooldown(address, DateTime.UtcNow, RequestsPerAddress) is TimeSpan addressWait)
            {
                int minutes = Math.Max(1, (int)Math.Ceiling(addressWait.TotalMinutes));
                OnLog?.Invoke($"[HOSTS] {request.DisplayName} requested a {request.Build} game server: declined, {RequestsPerAddress} already from {request.Address} in the last {RequestWindow.TotalMinutes:0} minutes\n");
                return new GameServerRequestResult(false, $"{RequestsPerAddress} game servers were requested from your network in the last {RequestWindow.TotalMinutes:0} minutes. " +
                    $"Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
            }
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
                {
                    RecordRequest(requester, DateTime.UtcNow);
                    if (address != null)
                        RecordRequest(address, DateTime.UtcNow);
                    return last;
                }
            }
            return last!;
        }

        /// <summary>
        /// How long after starting game servers for a build automatically (<see cref="StartForLogin"/>) it waits before doing
        /// so again: they take about half a minute to register, and every login meanwhile would start more.
        /// </summary>
        public static readonly TimeSpan AutoStartCooldown = TimeSpan.FromMinutes(3);

        /// <summary>When game servers were last started automatically for each build.</summary>
        private readonly Dictionary<string, DateTime> _autoStarted = new Dictionary<string, DateTime>();

        /// <summary>
        /// A player logged in to a build: if no game server of that build is registered, starts
        /// <see cref="ServerSettings.AutoStartGameServers"/> of them on the hosts that have it (this PC's first). They're
        /// requested by <see cref="GameServerBuilds.AutomaticRequester"/>, so they don't use up the player's own requests, and
        /// they close when empty like any requested game server.
        /// </summary>
        public async Task StartForLogin(Server server, string build, string displayName)
        {
            int count = server.Settings.AutoStartGameServers;
            if (count <= 0 || !GameServerBuilds.Names.TryGetValue(build, out string? buildName))
                return;
            if (server.ServerDBService.Registry.RegisteredGameServers.Values.Any(gameServer => GameServerBuilds.ServesBuild(build, gameServer.VersionLock)))
                return;
            List<Host> candidates;
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_autoStarted.TryGetValue(build, out DateTime last) && now - last < AutoStartCooldown)
                    return;
                candidates = _hosts.Where(host => host.Builds.Contains(build)).ToList();
                if (candidates.Count == 0)
                    return;
                _autoStarted[build] = now;
            }

            GameServerRequest request = new GameServerRequest(build, GameServerBuilds.AutomaticRequester, $"(automatic, {displayName} logged in)", null);
            int started = 0;
            foreach (Host host in candidates)
            {
                while (started < count)
                {
                    GameServerRequestResult result;
                    try
                    {
                        result = await host.Start(request);
                    }
                    catch (Exception ex)
                    {
                        result = new GameServerRequestResult(false, "The game server host didn't answer: " + ex.Message);
                    }
                    if (!result.Accepted)
                    {
                        OnLog?.Invoke($"[HOSTS] '{host.Name}' couldn't start a {build} game server for {displayName}'s login: {result.Message}\n");
                        break;
                    }
                    started++;
                }
                if (started >= count)
                    break;
            }
            OnLog?.Invoke($"[HOSTS] {displayName} logged in to {buildName}, which had no game servers: started {started} of {count}\n");
        }

        /// <summary>
        /// Every 30 seconds, finds registered game servers that have had no players for <see cref="IdleTimeout"/> and asks the
        /// host on their PC to close them. Hosts only close game servers they started on request, so others are left alone.
        /// </summary>
        public async Task RunIdleMonitor(Server server, CancellationToken token)
        {
            Dictionary<ulong, DateTime> emptySince = new Dictionary<ulong, DateTime>();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    DateTime now = DateTime.UtcNow;
                    List<RegisteredGameServer> idle = new List<RegisteredGameServer>();
                    var registered = server.ServerDBService.Registry.RegisteredGameServers;
                    foreach (var pair in registered)
                    {
                        if (pair.Value.SessionPlayerCount > 0)
                            emptySince.Remove(pair.Key);
                        else if (!emptySince.TryGetValue(pair.Key, out DateTime since))
                            emptySince[pair.Key] = now;
                        else if (now - since >= IdleTimeout)
                            idle.Add(pair.Value);
                    }
                    foreach (ulong gone in emptySince.Keys.Where(id => !registered.ContainsKey(id)).ToList())
                        emptySince.Remove(gone);
                    if (idle.Count == 0)
                        continue;

                    List<Host> hosts;
                    lock (_lock)
                        hosts = _hosts.ToList();
                    foreach (Host host in hosts)
                    {
                        ushort[] ports = idle.Where(gameServer => IsOnHost(host, gameServer, server)).Select(gameServer => gameServer.Port).Distinct().ToArray();
                        if (ports.Length > 0)
                            await host.StopIdle(ports);
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[HOSTS] Checking for empty game servers failed: {ex.Message}\n");
                }
            }
        }

        /// <summary>
        /// Whether a game server runs on a host's PC: for a remote host, it connected from the host's address; for this PC, it
        /// connected from here (loopback, this PC's addresses, or the server's public address).
        /// </summary>
        private static bool IsOnHost(Host host, RegisteredGameServer gameServer, Server server)
        {
            IPAddress peer = Normalize(gameServer.Peer.Address);
            if (!host.Local)
                return host.Address != null && (peer.Equals(host.Address) || Normalize(gameServer.ExternalAddress).Equals(host.Address));
            if (IPAddress.IsLoopback(peer) || (server.PublicIPAddress != null && peer.Equals(Normalize(server.PublicIPAddress))))
                return true;
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                    .Any(address => Normalize(address.Address).Equals(peer));
            }
            catch
            {
                return false;
            }
        }

        private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        /// <summary>
        /// Runs a remote host's connection ({server}/hosts): {"t":"hello","region","name","builds":[...]} from the host, then
        /// {"t":"start","rid","build","requester","name"} requests answered by {"t":"result","rid","ok","message"}, and
        /// {"t":"stop","ports":[...]} for requested game servers that have been empty for <see cref="IdleTimeout"/>.
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
                            Address = IPEndPoint.TryParse(address, out IPEndPoint? endPoint) ? Normalize(endPoint.Address) : null,
                        };
                        host.StopIdle = async ports =>
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(new JObject { ["t"] = "stop", ["ports"] = new JArray(ports.Select(port => (int)port)) }.ToString(Newtonsoft.Json.Formatting.None));
                            await sendLock.WaitAsync();
                            try
                            {
                                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                            finally
                            {
                                sendLock.Release();
                            }
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
        private readonly List<(string requester, string build, Process process)> _started = new List<(string, string, Process)>();

        /// <summary>
        /// Where the started game servers are remembered (see <see cref="EnablePersistence"/>), or null.
        /// </summary>
        private string? _stateFile;

        /// <summary>
        /// Remembers the game servers this launcher starts in a file, and takes back the ones still running from a previous
        /// run. Without it, a restart (an update, a crash) forgot every requested game server it had started: they kept
        /// running, and were never closed when empty. A process is only taken back if its start time matches, so a reused
        /// process id is never mistaken for one.
        /// </summary>
        /// <returns>How many running game servers were taken back.</returns>
        public int EnablePersistence(string stateFile)
        {
            lock (_lock)
            {
                _stateFile = stateFile;
                int adopted = 0;
                try
                {
                    if (File.Exists(stateFile))
                    {
                        foreach (JToken entry in JArray.Parse(File.ReadAllText(stateFile)))
                        {
                            int pid = entry.Value<int>("pid");
                            long started = entry.Value<long>("started");
                            if (_started.Any(item => item.process.Id == pid))
                                continue;
                            try
                            {
                                Process process = Process.GetProcessById(pid);
                                if (process.HasExited || Math.Abs(process.StartTime.ToUniversalTime().Ticks - started) > TimeSpan.TicksPerSecond)
                                    continue;
                                _started.Add((entry.Value<string>("requester") ?? "", entry.Value<string>("build") ?? "", process));
                                adopted++;
                            }
                            catch
                            {
                                // Not running any more.
                            }
                        }
                    }
                }
                catch
                {
                    // An unreadable file: start over.
                }
                SaveState();
                return adopted;
            }
        }

        /// <summary>
        /// Writes the started game servers to the state file (call with the lock held).
        /// </summary>
        private void SaveState()
        {
            if (_stateFile == null)
                return;
            try
            {
                JArray entries = new JArray();
                foreach (var (requester, build, process) in _started)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;
                        entries.Add(new JObject { ["pid"] = process.Id, ["started"] = process.StartTime.ToUniversalTime().Ticks, ["requester"] = requester, ["build"] = build });
                    }
                    catch
                    {
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
                File.WriteAllText(_stateFile, entries.ToString());
            }
            catch
            {
                // Best effort: the servers still close while this process runs.
            }
        }

        /// <summary>The game executable of each build this PC can start ("summer" -> path).</summary>
        public IReadOnlyDictionary<string, string> Executables { get; set; } = new Dictionary<string, string>();

        /// <summary>How many requested game servers of each game version a player can have running at once.</summary>
        public int PerPlayer { get; set; } = 2;

        /// <summary>How many requested game servers can run at once, from all players.</summary>
        public int Max { get; set; } = 6;

        /// <summary>The game servers' frame rate (ticks a second; 0 = uncapped), or null for the game's default (120 on lobby builds).</summary>
        public uint? TickRate { get; set; }

        /// <summary>Extra command line arguments for each build's game servers ("christmas" -> ["-warp"]).</summary>
        public IReadOnlyDictionary<string, string[]> ExtraArguments { get; set; } = new Dictionary<string, string[]>();

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
                int mine = _started.Count(entry => entry.requester == requester && entry.build == request.Build);
                // Game servers started automatically for a login aren't any player's; only the total limit applies to them.
                if (mine >= perPlayer && requester != GameServerBuilds.AutomaticRequester.ToString())
                    return new GameServerRequestResult(false, $"You already have {mine} requested {buildName} game server{(mine == 1 ? "" : "s")} running (the limit is {perPlayer} per game version). " +
                        $"Empty ones close after {GameServerHosts.IdleTimeout.TotalMinutes:0} minutes.");
                if (_started.Count >= Math.Max(1, Max))
                    return new GameServerRequestResult(false, "This PC is running as many requested game servers as it allows. Try again later, or another region.");
                Process? process = GameLauncher.Launch(executable, GameLauncher.LaunchRole.Server, noOVR: true, headless: true, timeStep: TickRate,
                    additionalArgs: ExtraArguments.TryGetValue(request.Build, out string[]? extra) ? new List<string>(extra) : null);
                if (process == null)
                    return new GameServerRequestResult(false, "The game server didn't start.");
                _started.Add((requester, request.Build, process));
                SaveState();
                return new GameServerRequestResult(true, $"Starting a {buildName} game server. It takes about half a minute; then press Play in the game.");
            }
        }

        /// <summary>
        /// Closes the game servers this launcher started (on request) that listen on any of these UDP ports.
        /// </summary>
        /// <returns>How many were closed.</returns>
        public int StopIdle(IEnumerable<ushort> ports)
        {
            HashSet<int> pids = new HashSet<int>(ports.Select(port => UdpPortOwner(port)).Where(pid => pid != 0));
            int stopped = 0;
            lock (_lock)
            {
                _started.RemoveAll(entry => HasExited(entry.process));
                foreach (var entry in _started.Where(entry => pids.Contains(entry.process.Id)).ToList())
                {
                    try
                    {
                        entry.process.Kill();
                        stopped++;
                    }
                    catch
                    {
                    }
                    _started.Remove(entry);
                }
                SaveState();
            }
            return stopped;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

        /// <summary>
        /// The process listening on an IPv4 UDP port (0 if none, or not on Windows).
        /// </summary>
        public static int UdpPortOwner(ushort port)
        {
            if (!OperatingSystem.IsWindows())
                return 0;
            const int AF_INET = 2, UDP_TABLE_OWNER_PID = 1;
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(table, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                    return 0;
                // MIB_UDPTABLE_OWNER_PID: a DWORD count, then rows of { DWORD address, DWORD port (network order), DWORD pid }.
                int count = Marshal.ReadInt32(table);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = table + 4 + i * 12;
                    int rowPort = (ushort)IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row + 4) & 0xFFFF));
                    if (rowPort == port)
                        return Marshal.ReadInt32(row + 8);
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
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
