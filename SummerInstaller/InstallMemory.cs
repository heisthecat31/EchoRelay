using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SummerInstaller
{
    /// <summary>
    /// Remembers where each game version is installed and which one was used last (%APPDATA%\EchoClassicLobbies\installs.json),
    /// so picking a version goes straight to its install and Play.
    /// </summary>
    public static class InstallMemory
    {
        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoClassicLobbies", "installs.json");

        /// <summary>The version used last (a build id: "summer", "halloween", "winter", "aprilfools", "christmas", ...), or null.</summary>
        public static string? LastBuild { get; private set; }

        private static readonly Dictionary<string, string> Folders = new Dictionary<string, string>();

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                foreach (var pair in InstallerSettings.ParseFlatJson(File.ReadAllText(FilePath)))
                {
                    if (pair.Value is not string value)
                        continue;
                    if (pair.Key == "lastBuild")
                        LastBuild = value;
                    else if (pair.Key.StartsWith("folder.", StringComparison.Ordinal))
                        Folders[pair.Key.Substring("folder.".Length)] = value;
                }
            }
            catch
            {
                // A damaged file just means nothing is remembered.
            }
        }

        /// <summary>
        /// The remembered install folder of a version, if it still has that version installed.
        /// </summary>
        public static string? FolderFor(GameBuild build)
        {
            return Folders.TryGetValue(build.Id, out string? folder) && File.Exists(Path.Combine(folder, build.Executable)) ? folder : null;
        }

        /// <summary>
        /// Every remembered install that still has its version installed.
        /// </summary>
        public static List<(GameBuild build, string folder)> AllInstalls(IEnumerable<GameBuild> builds)
        {
            List<(GameBuild, string)> installs = new List<(GameBuild, string)>();
            foreach (GameBuild build in builds)
                if (FolderFor(build) is string folder)
                    installs.Add((build, folder));
            return installs;
        }

        /// <summary>
        /// Remembers a version's install folder, and that it was used last.
        /// </summary>
        public static void Remember(GameBuild build, string folder)
        {
            Folders[build.Id] = folder;
            LastBuild = build.Id;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                StringBuilder json = new StringBuilder("{\n  \"lastBuild\": \"" + Escape(LastBuild) + "\"");
                foreach (var pair in Folders.OrderBy(p => p.Key))
                    json.Append(",\n  \"folder." + Escape(pair.Key) + "\": \"" + Escape(pair.Value) + "\"");
                json.Append("\n}\n");
                File.WriteAllText(FilePath, json.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Not being able to remember isn't worth interrupting the player for.
            }
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
