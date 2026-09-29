using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EchoRelay.Host
{
    /// <summary>
    /// What a host runs with: saved in EchoRelayHost.config.json next to EchoRelay.Host.exe by the guided setup.
    /// </summary>
    internal class HostConfig
    {
        /// <summary>The EchoRelay server (ws://ADDRESS:PORT).</summary>
        [JsonProperty("relay")]
        public string? Relay { get; set; }

        /// <summary>The server's ServerDB API key, if it uses one.</summary>
        [JsonProperty("api_key")]
        public string? ApiKey { get; set; }

        /// <summary>The region players pick in the installer for this PC (e.g. US-West).</summary>
        [JsonProperty("region")]
        public string? Region { get; set; }

        /// <summary>Each game version's install folder (summer, halloween, winter, christmas, halloween2017).</summary>
        [JsonProperty("installs")]
        public Dictionary<string, string> Installs { get; set; } = new Dictionary<string, string>();

        /// <summary>This host's name in the EchoRelay server's log.</summary>
        [JsonProperty("name")]
        public string Name { get; set; } = Environment.MachineName;

        /// <summary>The game servers' frame rate, ticks a second (fixed timestep; 0 = uncapped, a whole CPU core each).</summary>
        [JsonProperty("tick_rate")]
        public int TickRate { get; set; } = 120;

        /// <summary>Requested game servers of each game version one player can have running at once.</summary>
        [JsonProperty("per_player")]
        public int PerPlayer { get; set; } = 2;

        /// <summary>Requested game servers that can run on this PC at once, from all players.</summary>
        [JsonProperty("max")]
        public int Max { get; set; } = 6;

        /// <summary>Christmas and halloween 2017 servers render in software (WARP) even with a GPU. It's the fallback without
        /// one anyway.</summary>
        [JsonProperty("christmas_software_rendering")]
        public bool ChristmasSoftwareRendering { get; set; }

        /// <summary>Check for and install the latest EchoRelay DLLs every time the host starts.</summary>
        [JsonProperty("update_on_start")]
        public bool UpdateOnStart { get; set; } = true;

        /// <summary>Where the latest DLLs are released (GitHub owner/repo).</summary>
        [JsonProperty("game_files_repository")]
        public string GameFilesRepository { get; set; } = "heisthecat31/EchoRelay";

        /// <summary>Extra command line arguments for a version's game servers, e.g. { "summer": "-noconsole" }. Edit the file to set.</summary>
        [JsonProperty("extra_args")]
        public Dictionary<string, string> ExtraArgs { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Each build's extra game server arguments: extra_args, plus -warp for christmas_software_rendering.
        /// </summary>
        public Dictionary<string, string[]> GameServerArguments()
        {
            Dictionary<string, string[]> arguments = new Dictionary<string, string[]>();
            foreach (string build in Installs.Keys)
            {
                List<string> list = ExtraArgs.TryGetValue(build, out string? extra)
                    ? extra.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new List<string>();
                if ((build == "christmas" || build == "halloween2017") && ChristmasSoftwareRendering && !list.Contains("-warp"))
                    list.Add("-warp");
                if (list.Count > 0)
                    arguments[build] = list.ToArray();
            }
            return arguments;
        }

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, "EchoRelayHost.config.json");

        public static HostConfig? Load()
        {
            try
            {
                return File.Exists(FilePath) ? JsonConvert.DeserializeObject<HostConfig>(File.ReadAllText(FilePath)) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Save() => File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    /// <summary>
    /// The guided setup a game server host runs (EchoRelay.Host with no arguments, e.g. from EchoRelay-Host.bat): where
    /// each game version is installed, updating their EchoRelay DLLs from the latest GitHub release, and this PC's region.
    /// </summary>
    internal static class HostSetup
    {
        private static readonly (string build, string exe)[] Builds =
        {
            ("summer", "echovr.exe"), ("halloween", "echovr.exe"), ("winter", "echovr.exe"), ("christmas", "EchoArena.exe"),
            ("halloween2017", "EchoArena.exe"),
        };

        private static uint BuildTimestamp(string build) => build switch
        {
            "summer" => SummerBuild.ExecutableTimestamp,
            "halloween" => SummerBuild.HalloweenExecutableTimestamp,
            "winter" => SummerBuild.WinterExecutableTimestamp,
            "halloween2017" => SummerBuild.Halloween2017ExecutableTimestamp,
            _ => SummerBuild.ChristmasExecutableTimestamp,
        };

        /// <summary>A build's game executable in an install folder.</summary>
        public static string ExecutablePath(string build, string folder) =>
            Path.Combine(folder, "bin", "win7", Builds.First(b => b.build == build).exe);

        /// <summary>
        /// Asks everything (offering the saved answers), updates the installs, saves the config and returns it.
        /// </summary>
        public static async Task<HostConfig> RunAsync()
        {
            Console.WriteLine("EchoRelay game server host setup");
            Console.WriteLine("================================");
            Console.WriteLine();

            HostConfig? saved = HostConfig.Load();
            if (saved != null && saved.Installs.Count > 0 && !string.IsNullOrWhiteSpace(saved.Region) && !string.IsNullOrWhiteSpace(saved.Relay))
            {
                Console.WriteLine("Saved setup:");
                PrintConfig(saved);
                Console.WriteLine("(Every option is in EchoRelayHost.config.json, next to this .bat; answer n to go through them again.)");
                if (AskYesNo("Use it", true))
                {
                    if (saved.UpdateOnStart)
                        await UpdateGameFilesAsync(saved);
                    saved.Save(); // adds options newer versions know about, with their defaults
                    Done(saved);
                    return saved;
                }
                Console.WriteLine();
            }

            HostConfig config = saved ?? new HostConfig();

            // 1. Where each version is installed.
            Console.WriteLine("Where is each Echo VR version installed? Paste (or drag in) its folder, the one with bin\\win7 in it.");
            Console.WriteLine("Type skip for a version this PC doesn't host. Enter keeps the folder shown in [brackets].");
            Console.WriteLine();
            foreach (var (build, _) in Builds)
                AskInstall(config, build);
            if (config.Installs.Count == 0)
                throw new InvalidOperationException("No game versions were set up, so there's nothing to host.");

            // 2. The EchoRelay server, from the installs' own config (so game servers and host use the same one).
            (string? relay, string? apiKey) = RelayFromInstalls(config);
            if (relay != null && (config.Relay == null || relay != config.Relay))
            {
                config.Relay = relay;
                if (!string.IsNullOrEmpty(apiKey))
                    config.ApiKey = apiKey;
            }
            if (config.Relay == null)
            {
                Console.WriteLine("The installs' _local\\config.json doesn't say which EchoRelay server to use.");
                config.Relay = AskRequired("EchoRelay server address (e.g. 203.0.113.5:6800)", null);
                if (!config.Relay.StartsWith("ws://") && !config.Relay.StartsWith("wss://"))
                    config.Relay = "ws://" + config.Relay;
            }
            Console.WriteLine($"EchoRelay server: {config.Relay}");
            Console.WriteLine();

            // 3. The latest DLLs.
            await UpdateGameFilesAsync(config);

            // 4. Game server options and region.
            Console.WriteLine("Game server options (Enter keeps the value in [brackets]):");
            config.TickRate = AskNumber("  Tick rate: frames a second each game server runs at (fixed timestep; lower uses less CPU, 0 = uncapped)", config.TickRate, 0, 1000);
            config.PerPlayer = AskNumber("  Game servers one player can have running at once, per game version", config.PerPlayer, 1, 10);
            config.Max = AskNumber("  Game servers this PC runs at once, in total", config.Max, 1, 50);
            if (config.Installs.ContainsKey("christmas") || config.Installs.ContainsKey("halloween2017"))
                config.ChristmasSoftwareRendering = AskYesNo("  Christmas / Halloween 2017: render in software instead of on the GPU (no GPU use, a little more CPU)", config.ChristmasSoftwareRendering);
            config.UpdateOnStart = AskYesNo("  Check for EchoRelay DLL updates every time the host starts", config.UpdateOnStart);
            config.Name = AskRequired("  This PC's name, in the EchoRelay server's log", config.Name).Trim();
            Console.WriteLine();
            config.Region = AskRequired("Name this PC's region, as players will see it (e.g. US-West, EU)", config.Region).Trim();
            if (config.Region.Length > 24)
                config.Region = config.Region.Substring(0, 24);

            // 5. The API key last: the EchoRelay server only lets hosts connect with it, if it uses one. Not asked when the
            // installs' config (or this host's saved one) already has it.
            Console.WriteLine();
            if (!string.IsNullOrEmpty(apiKey))
                Console.WriteLine("API key: found in the installs' _local\\config.json.");
            else if (!string.IsNullOrEmpty(config.ApiKey))
                Console.WriteLine($"API key: using the saved one (api_key in {Path.GetFileName(HostConfig.FilePath)}).");
            else
            {
                Console.WriteLine("The EchoRelay server's ServerDB API key (its owner has it: EchoRelay settings, \"Use API key authentication\").");
                string? key = Ask("API key (Enter if the server doesn't use one)", null);
                if (!string.IsNullOrWhiteSpace(key))
                    config.ApiKey = key.Trim();
            }

            config.Save();
            Console.WriteLine();
            Console.WriteLine("Saved to EchoRelayHost.config.json, next to this .bat (every option can be changed there too).");
            Done(config);
            return config;
        }

        private static void PrintConfig(HostConfig config)
        {
            foreach (var install in config.Installs)
                Console.WriteLine($"  {GameServerBuilds.Names.GetValueOrDefault(install.Key, install.Key),-18} {install.Value}");
            Console.WriteLine($"  {"Server",-18} {config.Relay}");
            Console.WriteLine($"  {"Region",-18} {config.Region}");
            Console.WriteLine($"  {"API key",-18} {(string.IsNullOrEmpty(config.ApiKey) ? "none" : "saved")}");
            Console.WriteLine($"  {"Name",-18} {config.Name}");
            Console.WriteLine($"  {"Tick rate",-18} {(config.TickRate == 0 ? "uncapped" : config.TickRate + " frames a second")}");
            Console.WriteLine($"  {"Per player",-18} {config.PerPlayer} per game version");
            Console.WriteLine($"  {"At once",-18} {config.Max}");
            if (config.Installs.ContainsKey("christmas") || config.Installs.ContainsKey("halloween2017"))
                Console.WriteLine($"  {"2017 builds",-18} {(config.ChristmasSoftwareRendering ? "software rendering" : "GPU (software if there is none)")}");
            Console.WriteLine($"  {"Updates",-18} {(config.UpdateOnStart ? "checked at every start" : "off")}");
            foreach (var extra in config.ExtraArgs.Where(e => !string.IsNullOrWhiteSpace(e.Value)))
                Console.WriteLine($"  {"Extra args " + extra.Key,-18} {extra.Value}");
        }

        private static void Done(HostConfig config)
        {
            Console.WriteLine();
            Console.WriteLine("All Done, Leave this window open and wait for someone to request a game server, once your region");
            Console.WriteLine($"({config.Region}) is requested it will launch that game server automatically.");
            Console.WriteLine();
        }

        private static void AskInstall(HostConfig config, string build)
        {
            string name = GameServerBuilds.Names[build];
            config.Installs.TryGetValue(build, out string? current);
            while (true)
            {
                string? answer = Ask($"{name} install folder", current);
                if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
                {
                    config.Installs.Remove(build);
                    Console.WriteLine($"  Skipped {name}.");
                    return;
                }
                string folder = answer.Trim().Trim('"');
                // Accept the executable itself, or bin\win7, too.
                if (File.Exists(folder))
                    folder = Path.GetDirectoryName(folder) ?? folder;
                if (string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "win7", StringComparison.OrdinalIgnoreCase))
                    folder = Directory.GetParent(folder.TrimEnd('\\'))?.Parent?.FullName ?? folder;
                string exe = ExecutablePath(build, folder);
                if (!File.Exists(exe))
                {
                    Console.WriteLine($"  {exe} isn't there. Try again (or type skip).");
                    current = null;
                    continue;
                }
                if (SummerBuild.ReadPETimestamp(exe) != BuildTimestamp(build))
                {
                    Console.WriteLine($"  That's {SummerBuild.GetBuildName(exe) ?? "an unknown build"}, not {name}. Try again (or type skip).");
                    current = null;
                    continue;
                }
                config.Installs[build] = Path.GetFullPath(folder);
                Console.WriteLine($"  OK: {name}");
                return;
            }
        }

        /// <summary>
        /// The EchoRelay server ("ws://host:port") and API key the installs' _local\config.json use (its serverdb_host).
        /// </summary>
        private static (string? relay, string? apiKey) RelayFromInstalls(HostConfig config)
        {
            foreach (string folder in config.Installs.Values)
            {
                try
                {
                    string path = Path.Combine(folder, "_local", "config.json");
                    if (!File.Exists(path))
                        continue;
                    string? serverDb = JObject.Parse(File.ReadAllText(path)).Value<string>("serverdb_host");
                    if (string.IsNullOrWhiteSpace(serverDb) || !Uri.TryCreate(serverDb, UriKind.Absolute, out Uri? uri))
                        continue;
                    string? key = Regex.Match(uri.Query, @"[?&]api_key=([^&]*)") is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
                    return ($"{uri.Scheme}://{uri.Authority}", key);
                }
                catch
                {
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Installs the latest release's EchoRelay DLLs into every install that doesn't have them yet (like the installer).
        /// </summary>
        public static async Task UpdateGameFilesAsync(HostConfig config)
        {
            Console.WriteLine($"Checking {config.GameFilesRepository} for the latest EchoRelay game files...");
            try
            {
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoRelay.Host/1.0");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string json = await http.GetStringAsync($"https://api.github.com/repos/{config.GameFilesRepository}/releases/latest");
                JObject release = JObject.Parse(json);
                string tag = release.Value<string>("tag_name") ?? "";
                string? asset = (release["assets"] as JArray)?.Select(a => a.Value<string>("browser_download_url"))
                    .FirstOrDefault(url => url != null && url.EndsWith("-GameFiles.zip", StringComparison.OrdinalIgnoreCase));
                if (tag.Length == 0 || asset == null)
                {
                    Console.WriteLine("  The latest release has no GameFiles zip; keeping the installed files.");
                    return;
                }
                var outdated = config.Installs.Where(install => ReadInstalledTag(install.Value) != tag).ToList();
                if (outdated.Count == 0)
                {
                    Console.WriteLine($"  All installs already have {tag}.");
                    return;
                }
                Console.WriteLine($"  Downloading {tag}...");
                byte[] zip = await http.GetByteArrayAsync(asset);
                using ZipArchive archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                foreach (var (build, folder) in outdated)
                {
                    try
                    {
                        InstallGameFiles(archive, build, folder);
                        File.WriteAllText(TagPath(folder), tag);
                        Console.WriteLine($"  Updated {GameServerBuilds.Names[build]} to {tag}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Couldn't update {GameServerBuilds.Names[build]} ({ex.Message}); it keeps its current files.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Couldn't check for updates ({ex.Message}); keeping the installed files.");
            }
            Console.WriteLine();
        }

        private static string TagPath(string folder) => Path.Combine(folder, "bin", "win7", "echorelay_gamefiles.txt");

        private static string? ReadInstalledTag(string folder)
        {
            try
            {
                return File.Exists(TagPath(folder)) ? File.ReadAllText(TagPath(folder)).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A build's DLLs from a GameFiles zip (christmas and halloween 2017 load the patch as dbghelp.dll; the zip keeps their
        /// copies under christmas/). A DLL a running game has loaded can't be overwritten but can be renamed, so the old one is moved aside.
        /// </summary>
        private static void InstallGameFiles(ZipArchive archive, string build, string folder)
        {
            (string entry, string target)[] files = build == "christmas" || build == "halloween2017"
                ? new[] { ("christmas/bin/win7/dbghelp.dll", @"bin\win7\dbghelp.dll"), ("christmas/bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") }
                : new[] { ("bin/win7/dbgcore.dll", @"bin\win7\dbgcore.dll"), ("bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") };
            foreach (var (entryName, file) in files)
            {
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry == null)
                    continue;
                string target = Path.Combine(folder, file);
                string temp = target + ".new";
                using (Stream source = entry.Open())
                using (FileStream destination = File.Create(temp))
                    source.CopyTo(destination);
                if (File.Exists(target))
                {
                    string old = target + ".old";
                    try { File.Delete(old); } catch { }
                    if (File.Exists(old))
                        old = target + ".old" + DateTime.UtcNow.Ticks;
                    File.Move(target, old);
                }
                File.Move(temp, target);
            }
        }

        private static string? Ask(string question, string? current)
        {
            Console.Write(string.IsNullOrEmpty(current) ? $"{question}: " : $"{question} [{current}]: ");
            string? answer = Console.ReadLine();
            ColorConsole.LineEnded();
            if (answer == null)
                throw new InvalidOperationException("No input (run it in a console window).");
            answer = answer.Trim().Trim('\uFEFF').Trim(); // a pasted or piped byte order mark
            return answer.Length > 0 ? answer : current;
        }

        private static int AskNumber(string question, int current, int min, int max)
        {
            while (true)
            {
                string? answer = Ask(question, current.ToString());
                if (int.TryParse(answer, out int value) && value >= min && value <= max)
                    return value;
                Console.WriteLine($"    Enter a number from {min} to {max}.");
            }
        }

        private static string AskRequired(string question, string? current)
        {
            while (true)
            {
                string? answer = Ask(question, current);
                if (!string.IsNullOrWhiteSpace(answer))
                    return answer;
            }
        }

        private static bool AskYesNo(string question, bool yes)
        {
            string answer = Ask($"{question}? ({(yes ? "Y/n" : "y/N")})", null) ?? "";
            return answer.Length == 0 ? yes : answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }
    }
}
