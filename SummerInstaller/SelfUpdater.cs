using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SummerInstaller
{
    /// <summary>
    /// Keeps the installer itself up to date: on start it asks GitHub for the latest release, and if that release is newer
    /// than this exe (its version is the release's, from Directory.Build.targets) and has an EchoClassicLobbies.exe, it
    /// downloads it and swaps it in. A running exe can't be overwritten but can be renamed, so this one is moved aside
    /// (EchoClassicLobbies.exe.old, deleted on the next start), the new one takes its name, and is started instead.
    /// Any failure (offline, rate limited, a read-only folder) just leaves this version running.
    /// </summary>
    public static class SelfUpdater
    {
        private const string AssetName = "EchoClassicLobbies.exe";

        /// <summary>
        /// Removes the copy a previous update moved aside (it was still running then).
        /// </summary>
        public static void CleanUp()
        {
            try
            {
                string old = ExePath + ".old";
                if (File.Exists(old))
                    File.Delete(old);
            }
            catch
            {
                // Still in use (the old copy hasn't exited yet): removed next time.
            }
        }

        /// <summary>
        /// Checks for a newer installer and, if there is one, installs it and starts it.
        /// </summary>
        /// <returns>The new version if it was installed and started (this process should exit), otherwise null.</returns>
        public static async Task<string?> UpdateAsync(string repository)
        {
            if (string.IsNullOrWhiteSpace(repository))
                return null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoClassicLobbies/" + CurrentVersion);
                using CancellationTokenSource check = new CancellationTokenSource(TimeSpan.FromSeconds(6));

                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository.Trim()}/releases/latest");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using HttpResponseMessage response = await http.SendAsync(request, check.Token);
                if (!response.IsSuccessStatusCode)
                    return null;
                string json = await response.Content.ReadAsStringAsync();
                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                string url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/" + Regex.Escape(AssetName) + ")\"").Groups[1].Value;
                Version? latest = ParseVersion(tag);
                if (latest == null || url.Length == 0 || latest <= CurrentVersion)
                    return null;

                byte[] exe = await http.GetByteArrayAsync(url);
                // A real Windows executable, not an error page.
                if (exe.Length < 64 * 1024 || exe[0] != (byte)'M' || exe[1] != (byte)'Z')
                    return null;

                string path = ExePath;
                string fresh = path + ".new";
                string old = path + ".old";
                File.WriteAllBytes(fresh, exe);
                // Only a newer installer replaces this one. A release can carry an installer older than its tag (not rebuilt
                // for it); taking that would download it again on every start.
                Version? downloaded = ParseVersion(FileVersionInfo.GetVersionInfo(fresh).FileVersion ?? "");
                if (downloaded == null || downloaded <= CurrentVersion)
                {
                    File.Delete(fresh);
                    return null;
                }
                if (File.Exists(old))
                    File.Delete(old);
                File.Move(path, old);
                try
                {
                    File.Move(fresh, path);
                }
                catch
                {
                    File.Move(old, path); // Put this version back.
                    throw;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
                return tag;
            }
            catch
            {
                try
                {
                    if (File.Exists(ExePath + ".new"))
                        File.Delete(ExePath + ".new");
                }
                catch
                {
                }
                return null;
            }
        }

        private static string ExePath => Assembly.GetExecutingAssembly().Location;

        /// <summary>This installer's version (the release it was built for).</summary>
        public static Version CurrentVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>
        /// A release tag's version ("v0.8.9" -> 0.8.9; anything after the numbers, e.g. "-summer.13", is ignored).
        /// </summary>
        public static Version? ParseVersion(string tag)
        {
            Match match = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success)
                return null;
            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
        }
    }
}
