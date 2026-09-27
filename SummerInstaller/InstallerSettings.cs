using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SummerInstaller
{
    /// <summary>
    /// The installer's embedded settings (Resources\installer.json) and game config (Resources\config.json).
    /// </summary>
    public class InstallerSettings
    {
        public string Title { get; set; } = "Echo VR";
        public string Subtitle { get; set; } = "Summer Lobby";
        public string Tagline { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long DownloadSizeBytes { get; set; }
        public string DefaultInstallFolder { get; set; } = "%USERPROFILE%\\Games\\Echo VR Summer";
        public string ShortcutName { get; set; } = "Echo VR Summer";

        /// <summary>
        /// The GitHub repository (owner/name) whose latest release provides the EchoRelay game files (SummerGameFiles zip).
        /// </summary>
        public string GameFilesRepository { get; set; } = "heisthecat31/EchoRelay";

        /// <summary>
        /// The game config written to &lt;install&gt;\_local\config.json, exactly as embedded.
        /// </summary>
        public string GameConfig { get; private set; } = "{}";

        public string ExpandedDefaultInstallFolder => Environment.ExpandEnvironmentVariables(DefaultInstallFolder);

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
                    Tagline = Get(values, "tagline", ""),
                    DownloadUrl = Get(values, "downloadUrl", ""),
                    Sha256 = Get(values, "sha256", ""),
                    DefaultInstallFolder = Get(values, "defaultInstallFolder", "%USERPROFILE%\\Games\\Echo VR Summer"),
                    ShortcutName = Get(values, "shortcutName", "Echo VR Summer"),
                    GameFilesRepository = Get(values, "gameFilesRepository", "heisthecat31/EchoRelay"),
                };
                if (values.TryGetValue("downloadSizeBytes", out object? size) && size != null)
                    settings.DownloadSizeBytes = Convert.ToInt64(size);
            }
            settings.GameConfig = ReadResource("config.json") ?? "{}";
            return settings;
        }

        /// <summary>
        /// Parses a flat JSON object of string / number / bool values (all installer.json needs).
        /// </summary>
        private static Dictionary<string, object> ParseFlatJson(string json)
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
