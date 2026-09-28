using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SummerInstaller
{
    /// <summary>
    /// A lobby build the installer can install: where to download it and how to set it up.
    /// </summary>
    public class GameBuild
    {
        /// <summary>"summer", "halloween", "winter" (christmas 2018) or "christmas" (christmas 2017).</summary>
        public string Id { get; set; } = "";
        /// <summary>Shown on the build switch and as the window subtitle, e.g. "Summer Lobby".</summary>
        public string Name { get; set; } = "";
        /// <summary>A short label for the build switch, e.g. "Summer 2019".</summary>
        public string ShortName { get; set; } = "";
        public string Tagline { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        /// <summary>
        /// Other links to the same archive, tried in order when the download link can't serve it (e.g. Google Drive's
        /// download quota). installer.json: "downloadMirrors" / "&lt;id&gt;DownloadMirrors", separated by spaces, commas or |.
        /// </summary>
        public string[] DownloadMirrors { get; set; } = Array.Empty<string>();
        public string Sha256 { get; set; } = "";
        public long DownloadSizeBytes { get; set; }
        public string DefaultInstallFolder { get; set; } = "";
        public string ShortcutName { get; set; } = "";
        /// <summary>The config's publisher_lock, which tells EchoRelay which build is logging in.</summary>
        public string PublisherLock { get; set; } = "";
        /// <summary>echovr.exe's PE header timestamp, which identifies the build in an existing install.</summary>
        public uint ExecutableTimestamp { get; set; }
        /// <summary>The game executable, relative to the install folder (the christmas build's is EchoArena.exe).</summary>
        public string Executable { get; set; } = @"bin\win7\echovr.exe";
        /// <summary>
        /// The christmas 2017 build reads its services from older config keys (login_host, matchmaker_host, serverdb_host),
        /// so the installer writes those too.
        /// </summary>
        public bool UsesLegacyConfigKeys { get; set; }
        /// <summary>The lobby, as named in the "press Play" hint.</summary>
        public string LobbyName { get; set; } = "";

        public string ExpandedDefaultInstallFolder => Environment.ExpandEnvironmentVariables(DefaultInstallFolder);
    }

    /// <summary>
    /// The installer's embedded settings (Resources\installer.json) and game config (Resources\config.json).
    /// </summary>
    public class InstallerSettings
    {
        public string Title { get; set; } = "Echo VR";
        public string Subtitle { get; set; } = "Summer Lobby";
        /// <summary>The installer's own name, under the title (it installs either build).</summary>
        public string AppName { get; set; } = "Classic Lobbies";
        /// <summary>
        /// The GitHub repository whose latest release's GameFiles zip keeps the EchoRelay game DLLs up to date ("owner/repo";
        /// empty turns the updates off).
        /// </summary>
        public string GameFilesRepository { get; set; } = "heisthecat31/EchoRelay";
        public string Tagline { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long DownloadSizeBytes { get; set; }
        public string DefaultInstallFolder { get; set; } = "%USERPROFILE%\\Games\\Echo VR Summer";
        public string ShortcutName { get; set; } = "Echo VR Summer";

        /// <summary>
        /// The game config written to &lt;install&gt;\_local\config.json, exactly as embedded.
        /// </summary>
        public string GameConfig { get; private set; } = "{}";

        public string ExpandedDefaultInstallFolder => Environment.ExpandEnvironmentVariables(DefaultInstallFolder);

        /// <summary>
        /// The builds offered, summer first. The summer build uses the original (unprefixed) installer.json keys, the
        /// halloween build the same keys prefixed with "halloween".
        /// </summary>
        public List<GameBuild> Builds { get; } = new List<GameBuild>();

        public GameBuild Summer => Builds[0];

        public GameBuild? FindBuild(string id) => Builds.Find(b => b.Id == id);

        public static InstallerSettings Load()
        {
            InstallerSettings settings;
            string? json = ReadResource("installer.json");
            if (json == null)
            {
                settings = new InstallerSettings();
            }
            else
            {
                Dictionary<string, object> values = ParseFlatJson(json);
                settings = new InstallerSettings
                {
                    Title = Get(values, "title", "Echo VR"),
                    Subtitle = Get(values, "subtitle", "Summer Lobby"),
                    AppName = Get(values, "appName", "Classic Lobbies"),
                    GameFilesRepository = Get(values, "gameFilesRepository", "heisthecat31/EchoRelay"),
                    Tagline = Get(values, "tagline", ""),
                    DownloadUrl = Get(values, "downloadUrl", ""),
                    Sha256 = Get(values, "sha256", ""),
                    DefaultInstallFolder = Get(values, "defaultInstallFolder", "%USERPROFILE%\\Games\\Echo VR Summer"),
                    ShortcutName = Get(values, "shortcutName", "Echo VR Summer"),
                };
                if (values.TryGetValue("downloadSizeBytes", out object? size) && size != null)
                    settings.DownloadSizeBytes = Convert.ToInt64(size);
            }
            settings.GameConfig = ReadResource("config.json") ?? "{}";

            Dictionary<string, object> all = json == null ? new Dictionary<string, object>() : ParseFlatJson(json);
            settings.Builds.Add(new GameBuild
            {
                Id = "summer",
                Name = settings.Subtitle,
                ShortName = "Summer 2019",
                Tagline = settings.Tagline,
                DownloadUrl = settings.DownloadUrl,
                DownloadMirrors = GetList(all, "downloadMirrors"),
                Sha256 = settings.Sha256,
                DownloadSizeBytes = settings.DownloadSizeBytes,
                DefaultInstallFolder = settings.DefaultInstallFolder,
                ShortcutName = settings.ShortcutName,
                PublisherLock = "rad15_summer",
                ExecutableTimestamp = 0x5D388D3C,
                LobbyName = "summer lobby",
            });
            GameBuild halloween = new GameBuild
            {
                Id = "halloween",
                Name = Get(all, "halloweenSubtitle", "Halloween Lobby"),
                ShortName = "Halloween 2018",
                Tagline = Get(all, "halloweenTagline", "The 2018 halloween build, running on community servers."),
                DownloadUrl = Get(all, "halloweenDownloadUrl", ""),
                DownloadMirrors = GetList(all, "halloweenDownloadMirrors"),
                Sha256 = Get(all, "halloweenSha256", ""),
                DefaultInstallFolder = Get(all, "halloweenDefaultInstallFolder", "%USERPROFILE%\\Games\\Echo VR Halloween"),
                ShortcutName = Get(all, "halloweenShortcutName", "Echo VR Halloween"),
                PublisherLock = "rad15_halloween",
                ExecutableTimestamp = 0x5BC7B897,
                LobbyName = "halloween lobby",
            };
            if (all.TryGetValue("halloweenDownloadSizeBytes", out object? halloweenSize) && halloweenSize != null)
                halloween.DownloadSizeBytes = Convert.ToInt64(halloweenSize);
            settings.Builds.Add(halloween);
            GameBuild christmas = new GameBuild
            {
                Id = "christmas",
                Name = Get(all, "christmasSubtitle", "Christmas Lobby"),
                ShortName = "Christmas 2017",
                Tagline = Get(all, "christmasTagline", "The 2017 christmas build, running on community servers."),
                DownloadUrl = Get(all, "christmasDownloadUrl", ""),
                DownloadMirrors = GetList(all, "christmasDownloadMirrors"),
                Sha256 = Get(all, "christmasSha256", ""),
                DefaultInstallFolder = Get(all, "christmasDefaultInstallFolder", "%USERPROFILE%\\Games\\Echo VR Christmas"),
                ShortcutName = Get(all, "christmasShortcutName", "Echo VR Christmas"),
                PublisherLock = "rad15_live",
                ExecutableTimestamp = 0x5A39494F,
                Executable = @"bin\win7\EchoArena.exe",
                UsesLegacyConfigKeys = true,
                LobbyName = "christmas lobby",
            };
            if (all.TryGetValue("christmasDownloadSizeBytes", out object? christmasSize) && christmasSize != null)
                christmas.DownloadSizeBytes = Convert.ToInt64(christmasSize);
            settings.Builds.Add(christmas);
            // The 2018 christmas lobby build: echovr.exe with the summer/halloween config keys and game files (dbgcore.dll).
            GameBuild winter = new GameBuild
            {
                Id = "winter",
                Name = Get(all, "winterSubtitle", "Christmas 2018 Lobby"),
                ShortName = "Christmas 2018",
                Tagline = Get(all, "winterTagline", "The 2018 christmas build, running on community servers."),
                DownloadUrl = Get(all, "winterDownloadUrl", ""),
                DownloadMirrors = GetList(all, "winterDownloadMirrors"),
                Sha256 = Get(all, "winterSha256", ""),
                DefaultInstallFolder = Get(all, "winterDefaultInstallFolder", "%USERPROFILE%\\Games\\Echo VR Christmas 2018"),
                ShortcutName = Get(all, "winterShortcutName", "Echo VR Christmas 2018"),
                PublisherLock = "rad15_winter",
                ExecutableTimestamp = 0x5C17F6B9,
                LobbyName = "christmas lobby",
            };
            if (all.TryGetValue("winterDownloadSizeBytes", out object? winterSize) && winterSize != null)
                winter.DownloadSizeBytes = Convert.ToInt64(winterSize);
            settings.Builds.Add(winter);
            return settings;
        }

        /// <summary>
        /// Parses a flat JSON object of string / number / bool values (all installer.json needs).
        /// </summary>
        internal static Dictionary<string, object> ParseFlatJson(string json)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            foreach (Match m in Regex.Matches(json, @"""((?:[^""\\]|\\.)*)""\s*:\s*(""((?:[^""\\]|\\.)*)""|-?\d+(?:\.\d+)?|true|false|null)"))
            {
                string key = Regex.Unescape(m.Groups[1].Value);
                string raw = m.Groups[2].Value;
                if (m.Groups[3].Success && raw.StartsWith("\""))
                    values[key] = Regex.Unescape(m.Groups[3].Value);
                else if (raw == "true" || raw == "false")
                    values[key] = raw == "true";
                else if (raw != "null")
                    values[key] = double.Parse(raw, CultureInfo.InvariantCulture);
            }
            return values;
        }

        private static string Get(Dictionary<string, object> values, string key, string fallback)
        {
            return values.TryGetValue(key, out object? value) && value is string s ? s : fallback;
        }

        private static string[] GetList(Dictionary<string, object> values, string key)
        {
            return Get(values, key, "").Split(new[] { ' ', '\t', '\r', '\n', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string? ReadResource(string name)
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null)
                return null;
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
