using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace EchoRelay.App.Utils
{
    /// <summary>
    /// Keeps EchoRelay.App up to date from the GitHub releases: when a newer release has an App zip, offers to update, then
    /// downloads it, closes, swaps in the new EchoRelay.App.exe and starts it again as administrator (in the same folder,
    /// so it finds the same settings.json).
    /// </summary>
    public static class AppUpdater
    {
        /// <summary>Where releases are published (GitHub owner/repo).</summary>
        public const string Repository = "heisthecat31/EchoRelay";

        /// <summary>This build's version (Directory.Build.targets).</summary>
        public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>A release's version from its tag ("v0.8.0", "v0.7.3-Halloween-Summer-Christmas"), or null.</summary>
        public static Version? ParseTag(string tag)
        {
            Match match = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
            return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
        }

        /// <summary>
        /// Extracts EchoRelay.App.exe from an App zip, or from the App zip inside a release bundle.
        /// </summary>
        public static void ExtractApp(ZipArchive archive, string destination)
        {
            using (archive)
            {
                ZipArchiveEntry? exe = archive.Entries.FirstOrDefault(e => e.Name.Equals("EchoRelay.App.exe", StringComparison.OrdinalIgnoreCase));
                if (exe != null)
                {
                    exe.ExtractToFile(destination, true);
                    return;
                }
                ZipArchiveEntry inner = archive.Entries.FirstOrDefault(e => e.Name.EndsWith("-App-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The release has no EchoRelay.App.exe.");
                MemoryStream innerZip = new MemoryStream();
                using (Stream stream = inner.Open())
                    stream.CopyTo(innerZip);
                innerZip.Position = 0;
                ExtractApp(new ZipArchive(innerZip, ZipArchiveMode.Read), destination);
            }
        }

        /// <summary>
        /// Checks for a newer release and, if the person agrees, updates and restarts. Quietly does nothing when offline or
        /// up to date, unless <paramref name="reportUpToDate"/> (a manual check).
        /// </summary>
        public static async Task CheckAsync(Form owner, bool reportUpToDate = false)
        {
            string tag;
            Version? latest;
            string? asset;
            try
            {
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoRelay.App/" + CurrentVersion.ToString(3));
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                JObject release = JObject.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
                tag = release.Value<string>("tag_name") ?? "";
                latest = ParseTag(tag);
                // The App zip, or else the release bundle (which has the App zip inside).
                List<string> urls = (release["assets"] as JArray)?.Select(a => a.Value<string>("browser_download_url") ?? "").ToList() ?? new List<string>();
                asset = urls.FirstOrDefault(url => url.EndsWith("-App-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    ?? urls.FirstOrDefault(url => url.EndsWith("-release.zip", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                if (reportUpToDate)
                    MessageBox.Show(owner, "Couldn't check for updates: " + ex.Message, "Echo Relay: Updates");
                return;
            }
            if (latest == null || latest <= CurrentVersion || asset == null)
            {
                if (reportUpToDate)
                    MessageBox.Show(owner, $"EchoRelay v{CurrentVersion.ToString(3)} is the latest version.", "Echo Relay: Updates");
                return;
            }

            if (MessageBox.Show(owner, $"EchoRelay {tag} is out (this is v{CurrentVersion.ToString(3)}).\n\n" +
                "Update now? EchoRelay closes (stopping the server), updates, and starts again as administrator.",
                "Echo Relay: Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where EchoRelay.App.exe is.");
                string folder = Path.Combine(Path.GetTempPath(), "EchoRelayUpdate");
                Directory.CreateDirectory(folder);
                string newExe = Path.Combine(folder, "EchoRelay.App.exe");

                // Download with a progress window (the release can be a few hundred MB).
                using (Form progressForm = new Form
                {
                    Text = "Echo Relay: Updating", FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false,
                    StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(420, 90), ShowInTaskbar = false,
                })
                {
                    Label status = new Label { Location = new Point(12, 12), Size = new Size(396, 20), Text = $"Downloading EchoRelay {tag}…" };
                    ProgressBar bar = new ProgressBar { Location = new Point(12, 40), Size = new Size(396, 23), Maximum = 1000 };
                    progressForm.Controls.AddRange(new Control[] { status, bar });
                    progressForm.Show(owner);
                    owner.Enabled = false;
                    try
                    {
                        using HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoRelay.App/" + CurrentVersion.ToString(3));
                        using HttpResponseMessage response = await http.GetAsync(asset, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        long? total = response.Content.Headers.ContentLength;
                        MemoryStream zip = new MemoryStream();
                        using (Stream stream = await response.Content.ReadAsStreamAsync())
                        {
                            byte[] buffer = new byte[1 << 16];
                            int read;
                            while ((read = await stream.ReadAsync(buffer)) > 0)
                            {
                                zip.Write(buffer, 0, read);
                                if (total > 0)
                                    bar.Value = (int)Math.Min(1000, zip.Length * 1000 / total.Value);
                                status.Text = $"Downloading EchoRelay {tag}… {zip.Length / 1048576} MB" + (total > 0 ? $" of {total.Value / 1048576} MB" : "");
                            }
                        }
                        status.Text = "Unpacking…";
                        zip.Position = 0;
                        await Task.Run(() => ExtractApp(new ZipArchive(zip, ZipArchiveMode.Read), newExe));
                    }
                    finally
                    {
                        owner.Enabled = true;
                    }
                }

                // Don't loop: the downloaded app must really be newer than this one.
                string? downloadedVersion = FileVersionInfo.GetVersionInfo(newExe).FileVersion;
                if (downloadedVersion == null || !Version.TryParse(downloadedVersion, out Version? downloaded) || downloaded <= CurrentVersion)
                {
                    MessageBox.Show(owner, $"The {tag} release's EchoRelay.App.exe is version {downloadedVersion ?? "unknown"}, not newer than this one " +
                        $"(v{CurrentVersion.ToString(3)}), so it wasn't installed.", "Echo Relay: Update");
                    return;
                }

                // A script waits for this process to exit, replaces the exe (keeping the old one as .old), and starts the new
                // one as administrator in the same working folder (where settings.json is).
                static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
                string script = Path.Combine(folder, "update.ps1");
                File.WriteAllText(script, string.Join(Environment.NewLine,
                    $"$ErrorActionPreference = 'Stop'",
                    $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue",
                    $"for ($i = 0; $i -lt 20; $i++) {{",
                    $"  try {{ Copy-Item -LiteralPath {Quote(exe)} -Destination {Quote(exe + ".old")} -Force; Copy-Item -LiteralPath {Quote(newExe)} -Destination {Quote(exe)} -Force; break }}",
                    $"  catch {{ Start-Sleep -Milliseconds 500 }}",
                    $"}}",
                    $"Start-Process -FilePath {Quote(exe)} -WorkingDirectory {Quote(Environment.CurrentDirectory)} -Verb RunAs"));
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "The update failed: " + ex.Message + "\n\nDownload it from https://github.com/" + Repository + "/releases instead.",
                    "Echo Relay: Update");
            }
        }
    }
}
