using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using Newtonsoft.Json.Linq;
using System.Net.WebSockets;
using System.Text;

namespace EchoRelay.Host
{
    /// <summary>
    /// A game server host for an EchoRelay server that runs on another PC (e.g. one per region): it connects to the server's
    /// /hosts, and starts the headless game servers players request from the installer ("Request server") in its region.
    /// The game installs it starts must have their _local\config.json pointed at that EchoRelay server.
    /// </summary>
    internal class Program
    {
        private const string Usage = @"EchoRelay.Host: starts requested game servers for an EchoRelay server, in a region.

Usage:
  EchoRelay.Host                (no options: guided setup, saved in EchoRelayHost.config.json; see EchoRelay-Host.bat)
  EchoRelay.Host --relay ws://ADDRESS:PORT --region EU --game summer=C:\Games\Echo VR Summer\bin\win7\echovr.exe [options]

  --relay ws://ADDRESS:PORT   The EchoRelay server (the address players' configs use, with ws://).
  --region NAME               The region players pick in the installer for this PC (e.g. EU, US).
  --game BUILD=PATH           A game this PC can start servers of (repeat for each). BUILD is summer, halloween,
                              winter (christmas 2018), christmas (christmas 2017), halloween2017 or lobby158 (1.58);
                              PATH its echovr.exe / EchoArena.exe.
  --perplayer N               Requested game servers of each game version a player can have running at once (default 2).
                              Requested game servers are closed once they've been empty for 5 minutes.
  --max N                     Requested game servers that can run at once (default 6).
  --tickrate N                The game servers' frame rate (default 120; 0 = uncapped, a whole CPU core each).
  --apikey KEY                The server's ServerDB API key, if it uses one.
  --name NAME                 This host's name in the server's log (default: the PC's name).";

        private static async Task<int> Main(string[] args)
        {
            string? relay = null, region = null, apiKey = null, name = Environment.MachineName;
            GameServerLauncher launcher = new GameServerLauncher();
            // Game servers this host started before it restarted (an update, a crash) are taken back, so they still close when empty.
            int adopted = launcher.EnablePersistence(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoRelay", "requested-game-servers-host.json"));
            if (adopted > 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Took back {adopted} requested game server{(adopted == 1 ? "" : "s")} started before this host restarted");
            Dictionary<string, string> executables = new Dictionary<string, string>();
            if (args.Length == 0)
            {
                ColorConsole.Install();
                // Guided setup (EchoRelay-Host.bat): asks for the installs and region, updates the DLLs, then hosts.
                HostConfig config;
                try
                {
                    config = await HostSetup.RunAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("Setup stopped: " + ex.Message);
                    return 1;
                }
                relay = config.Relay;
                region = config.Region;
                apiKey = string.IsNullOrEmpty(config.ApiKey) ? null : config.ApiKey;
                launcher.PerPlayer = config.PerPlayer;
                launcher.Max = config.Max;
                launcher.TickRate = (uint)Math.Max(0, config.TickRate);
                launcher.ExtraArguments = config.GameServerArguments();
                name = string.IsNullOrWhiteSpace(config.Name) ? Environment.MachineName : config.Name;
                foreach (var install in config.Installs)
                    executables[install.Key] = HostSetup.ExecutablePath(install.Key, install.Value);
            }
            else try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                    switch (args[i].ToLowerInvariant())
                    {
                        case "--relay": relay = Next(); break;
                        case "--region": region = Next(); break;
                        case "--apikey": apiKey = Next(); break;
                        case "--name": name = Next(); break;
                        case "--perplayer": launcher.PerPlayer = int.Parse(Next()); break;
                        case "--max": launcher.Max = int.Parse(Next()); break;
                        case "--tickrate": launcher.TickRate = uint.Parse(Next()); break;
                        case "--game":
                        {
                            string value = Next();
                            int equals = value.IndexOf('=');
                            string build = equals > 0 ? value.Substring(0, equals).Trim().ToLowerInvariant() : "";
                            string path = equals > 0 ? value.Substring(equals + 1).Trim().Trim('"') : "";
                            if (!GameServerBuilds.Names.ContainsKey(build))
                                throw new ArgumentException($"Unknown build '{build}' in --game (summer, halloween, winter, christmas, halloween2017 or lobby158)");
                            if (!File.Exists(path))
                                throw new ArgumentException($"--game {build}: {path} doesn't exist");
                            executables[build] = path;
                            break;
                        }
                        case "--help": case "-h": case "/?": Console.WriteLine(Usage); return 0;
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }
                if (relay == null || region == null || executables.Count == 0)
                    throw new ArgumentException("--relay, --region and at least one --game are required");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine();
                Console.WriteLine(Usage);
                return 1;
            }
            if (relay == null || region == null)
                return 1;
            launcher.Executables = executables;
            launcher.TickRate ??= 120;

            Uri uri = new Uri(relay.TrimEnd('/') + "/hosts" + (apiKey != null ? "?api_key=" + Uri.EscapeDataString(apiKey) : ""));
            Console.WriteLine($"Hosting {string.Join(", ", executables.Keys)} game servers in region {region} for {relay}");
            while (true)
            {
                try
                {
                    await RunConnection(uri, region, name, launcher);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disconnected from {relay}; reconnecting in 10 seconds");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Couldn't connect to {relay} ({ex.Message}); retrying in 10 seconds");
                }
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        private static async Task RunConnection(Uri uri, string region, string name, GameServerLauncher launcher)
        {
            using ClientWebSocket socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(uri, CancellationToken.None);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connected");
            await Send(socket, new JObject { ["t"] = "hello", ["region"] = region, ["name"] = name, ["builds"] = new JArray(launcher.Builds) });

            byte[] buffer = new byte[16384];
            while (socket.State == WebSocketState.Open)
            {
                using MemoryStream message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        if (socket.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] The EchoRelay server refused this host: {socket.CloseStatusDescription}. " +
                                "Set its ServerDB API key (\"api_key\" in EchoRelayHost.config.json, or run the setup again and answer n).");
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                JObject json;
                try
                {
                    json = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                }
                catch
                {
                    continue;
                }
                if (json.Value<string>("t") == "stop")
                {
                    ushort[] ports = (json["ports"] as JArray ?? new JArray()).Select(port => (ushort)port.Value<int>()).ToArray();
                    int stopped = launcher.StopIdle(ports);
                    if (stopped > 0)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Closed {stopped} requested game server{(stopped == 1 ? "" : "s")}, empty for {GameServerHosts.IdleTimeout.TotalMinutes:0} minutes");
                    continue;
                }
                if (json.Value<string>("t") != "start")
                    continue;
                string build = json.Value<string>("build") ?? "";
                string requester = json.Value<string>("requester") ?? "";
                string player = json.Value<string>("name") ?? requester;
                GameServerRequestResult result = XPlatformId.Parse(requester) is XPlatformId id
                    ? launcher.Start(new GameServerRequest(build, id, player, null))
                    : new GameServerRequestResult(false, "Invalid request.");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {player} requested a {build} game server: {(result.Accepted ? "started" : "declined")} - {result.Message}");
                await Send(socket, new JObject { ["t"] = "result", ["rid"] = json["rid"], ["ok"] = result.Accepted, ["message"] = result.Message });
            }
        }

        private static async Task Send(ClientWebSocket socket, JObject json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
