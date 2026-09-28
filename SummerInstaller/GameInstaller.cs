using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SummerInstaller
{
    public enum InstallStage { Connecting, Downloading, Verifying, Extracting, Configuring, Done }

    public class InstallProgress
    {
        public InstallStage Stage { get; set; }
        /// <summary>0..1, or a negative value when the size is unknown.</summary>
        public double Fraction { get; set; } = -1;
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Downloads, verifies, extracts and configures a lobby build (summer, halloween or christmas).
    /// </summary>
    public class GameInstaller
    {
        /// <summary>
        /// The game executables of the builds, relative to the install folder (see <see cref="GameBuild.Executable"/>).
        /// Used to detect an existing install.
        /// </summary>
        private static readonly string[] GameExecutables = { @"bin\win7\echovr.exe", @"bin\win7\EchoArena.exe" };

        private readonly InstallerSettings _settings;
        private static readonly HttpClient Http;

        static GameInstaller()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoClassicLobbies/1.0");
        }

        public GameInstaller(InstallerSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// The window Proton Drive downloads belong to (their page runs in a window of its own, normally off screen).
        /// </summary>
        public System.Windows.Window? Owner { get; set; }

        public static bool IsInstalled(string folder) => GameExecutables.Any(exe => File.Exists(Path.Combine(folder, exe)));

        /// <summary>
        /// Identifies the build installed in a folder from its game executable's PE header timestamp.
        /// </summary>
        /// <returns>The installed build, or null if there is none (or it's a build this installer doesn't know).</returns>
        public static GameBuild? DetectInstalledBuild(string folder, System.Collections.Generic.IEnumerable<GameBuild> builds)
        {
            foreach (GameBuild build in builds)
            {
                try
                {
                    string exe = Path.Combine(folder, build.Executable);
                    if (!File.Exists(exe))
                        continue;
                    using FileStream stream = File.OpenRead(exe);
                    using BinaryReader reader = new BinaryReader(stream);
                    stream.Position = 0x3C;
                    stream.Position = reader.ReadInt32() + 8; // PE signature (4) + machine (2) + section count (2)
                    if (reader.ReadUInt32() == build.ExecutableTimestamp)
                        return build;
                }
                catch
                {
                }
            }
            return null;
        }

        private static string DownloadFolder(string installFolder) => Path.Combine(installFolder, ".download");
        private static string PartialArchive(string installFolder) => Path.Combine(DownloadFolder(installFolder), "game.zip.part");

        /// <summary>
        /// Whether a previous, interrupted download can be resumed.
        /// </summary>
        public static bool HasPartialDownload(string installFolder) => File.Exists(PartialArchive(installFolder));

        /// <summary>
        /// Runs the full install: download (resuming a partial one), verify, extract, write the config.
        /// </summary>
        public async Task InstallAsync(GameBuild build, string installFolder, string config, string displayName, string password, string publisherLock, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(installFolder);
            Directory.CreateDirectory(DownloadFolder(installFolder));
            string partial = PartialArchive(installFolder);

            await DownloadAsync(build, partial, progress, cancel);
            await Task.Run(() => Verify(build, partial, progress, cancel), cancel);
            await Task.Run(() => Extract(build, partial, installFolder, progress, cancel), cancel);

            progress.Report(new InstallProgress { Stage = InstallStage.Configuring, Detail = "Writing the game config" });
            // The game download carries the EchoRelay game files (dbgcore.dll, pnsradgameserver.dll) and sourcedb the build
            // needs; newer game DLLs from the latest EchoRelay release replace the ones it shipped with.
            WriteConfig(installFolder, config, displayName, password, build, publisherLock);
            progress.Report(new InstallProgress { Stage = InstallStage.Configuring, Detail = "Checking for game file updates" });
            await UpdateGameFilesAsync(build, installFolder, cancel);

            // The archive is no longer needed.
            try { Directory.Delete(DownloadFolder(installFolder), true); } catch { }
            progress.Report(new InstallProgress { Stage = InstallStage.Done, Fraction = 1 });
        }

        #region Online players
        /// <summary>
        /// Who's online on the server a config points at ({api}/players): each player's display name and game version, or
        /// null if the server can't say (offline, or an older EchoRelay).
        /// </summary>
        public static async Task<System.Collections.Generic.List<(string name, string version)>?> GetOnlinePlayersAsync(string config)
        {
            string? api = GetConfigValue(config, "apiservice_host");
            if (string.IsNullOrWhiteSpace(api))
                return null;
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using HttpResponseMessage response = await Http.GetAsync(api!.TrimEnd('/') + "/players", timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return null;
                string json = await response.Content.ReadAsStringAsync();
                if (!json.Contains("\"players\""))
                    return null;
                System.Collections.Generic.List<(string, string)> players = new System.Collections.Generic.List<(string, string)>();
                foreach (Match item in Regex.Matches(json, "\\{[^{}]*\\}"))
                {
                    Match name = Regex.Match(item.Value, "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    Match version = Regex.Match(item.Value, "\"version\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (name.Success)
                        players.Add((Regex.Unescape(name.Groups[1].Value), version.Success ? Regex.Unescape(version.Groups[1].Value) : ""));
                }
                return players;
            }
            catch
            {
                return null;
            }
        }
        #endregion

        #region Game server requests
        /// <summary>
        /// Asks the server a config points at to start a game server of a version for this player ({api}/servers/request).
        /// The server decides; its answer is shown to the player.
        /// </summary>
        /// <returns>Whether a game server is starting, and the server's message.</returns>
        public static async Task<(bool ok, string message)> RequestGameServerAsync(string config, GameBuild build, string? region, string displayName, string password)
        {
            string? api = GetConfigValue(config, "apiservice_host");
            if (string.IsNullOrWhiteSpace(api))
                return (false, "The server config has no \"apiservice_host\", so there's nowhere to send the request.");
            string url = api!.TrimEnd('/') + "/servers/request";
            string body = "{\"build\":\"" + build.Id + "\",\"region\":\"" + JsonEscape(region ?? "") + "\",\"displayname\":\"" + JsonEscape(displayName.Trim()) + "\",\"password\":\"" + JsonEscape(password) + "\"}";
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using HttpResponseMessage response = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), timeout.Token);
                string json = await response.Content.ReadAsStringAsync();
                Match ok = Regex.Match(json, "\"ok\"\\s*:\\s*(true|false)");
                Match message = Regex.Match(json, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!ok.Success)
                    return (false, response.IsSuccessStatusCode ? "The server doesn't know about game server requests (it needs a newer EchoRelay)." : $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                return (ok.Groups[1].Value == "true", message.Success ? Regex.Unescape(message.Groups[1].Value) : "");
            }
            catch (Exception ex)
            {
                return (false, "Couldn't reach the server: " + ex.Message);
            }
        }

        /// <summary>
        /// The regions the server a config points at can start game servers of a version in ({api}/servers/regions), or an
        /// empty list if it can't (or is an older EchoRelay).
        /// </summary>
        public static async Task<System.Collections.Generic.List<string>> GetGameServerRegionsAsync(string config, GameBuild build)
        {
            System.Collections.Generic.List<string> regions = new System.Collections.Generic.List<string>();
            string? api = GetConfigValue(config, "apiservice_host");
            if (string.IsNullOrWhiteSpace(api))
                return regions;
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using HttpResponseMessage response = await Http.GetAsync(api!.TrimEnd('/') + "/servers/regions?build=" + build.Id, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return regions;
                string json = await response.Content.ReadAsStringAsync();
                Match list = Regex.Match(json, "\"regions\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (list.Success)
                    foreach (Match item in Regex.Matches(list.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                        regions.Add(Regex.Unescape(item.Groups[1].Value));
            }
            catch
            {
            }
            return regions;
        }

        private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        #endregion

        #region Game file updates
        /// <summary>
        /// The EchoRelay DLLs a release's GameFiles zip carries for a build: (path in the zip, path in the install).
        /// The christmas build loads the patch as dbghelp.dll, and the zip keeps its copies under christmas/.
        /// </summary>
        private static (string entry, string target)[] GameFilesFor(GameBuild build) => build.Id == "christmas"
            ? new[] { ("christmas/bin/win7/dbghelp.dll", @"bin\win7\dbghelp.dll"), ("christmas/bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") }
            : new[] { ("bin/win7/dbgcore.dll", @"bin\win7\dbgcore.dll"), ("bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") };

        private static string GameFilesVersionPath(string installFolder) => Path.Combine(installFolder, "bin", "win7", "echorelay_gamefiles.txt");

        /// <summary>
        /// Installs the latest EchoRelay release's game DLLs (the patch and the game server plugin) if they're newer than the
        /// installed ones. The game downloads are only rebuilt occasionally, so fixes to these arrive this way. Failures
        /// (offline, rate limited, ...) are ignored: the installed files keep working.
        /// </summary>
        /// <returns>The release tag installed, or null if nothing changed.</returns>
        public async Task<string?> UpdateGameFilesAsync(GameBuild build, string installFolder, CancellationToken cancel)
        {
            var updated = await UpdateAllGameFilesAsync(new[] { (build, installFolder) }, cancel);
            return updated.Count > 0 ? updated[0].tag : null;
        }

        /// <summary>
        /// Serializes updates, so two (e.g. the one on opening and the one before Play) don't move the same files at once.
        /// </summary>
        private static readonly SemaphoreSlim UpdateLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Installs the latest EchoRelay release's game DLLs into every given install (every downloaded game version) that
        /// doesn't have them yet. The release is looked up, and its GameFiles zip downloaded, once. Each install gets the files
        /// its build loads (christmas 2017: dbghelp.dll). One install failing (e.g. files in use) doesn't stop the others.
        /// </summary>
        /// <returns>The installs updated, with the release tag they got.</returns>
        public async Task<List<(GameBuild build, string folder, string tag)>> UpdateAllGameFilesAsync(IEnumerable<(GameBuild build, string folder)> installs, CancellationToken cancel)
        {
            List<(GameBuild, string, string)> updated = new List<(GameBuild, string, string)>();
            List<(GameBuild build, string folder)> targets = installs
                .Where(install => IsInstalled(install.folder))
                .GroupBy(install => Path.GetFullPath(install.folder).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (string.IsNullOrWhiteSpace(_settings.GameFilesRepository) || targets.Count == 0)
                return updated;
            await UpdateLock.WaitAsync(cancel);
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));

                // Find the latest release and its GameFiles asset.
                string api = $"https://api.github.com/repos/{_settings.GameFilesRepository.Trim()}/releases/latest";
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, api);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return updated;
                string json = await response.Content.ReadAsStringAsync();
                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                string asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+-GameFiles\\.zip)\"").Groups[1].Value;
                if (tag.Length == 0 || asset.Length == 0)
                    return updated;
                List<(GameBuild build, string folder)> outdated = targets.Where(install =>
                {
                    string versionPath = GameFilesVersionPath(install.folder);
                    return !File.Exists(versionPath) || File.ReadAllText(versionPath).Trim() != tag;
                }).ToList();
                if (outdated.Count == 0)
                    return updated;

                // Download once, then install the DLLs each build loads.
                using HttpResponseMessage download = await Http.GetAsync(asset, timeout.Token);
                download.EnsureSuccessStatusCode();
                byte[] zip = await download.Content.ReadAsByteArrayAsync();
                using ZipArchive archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                foreach (var (build, folder) in outdated)
                {
                    try
                    {
                        InstallGameFiles(archive, build, folder);
                        File.WriteAllText(GameFilesVersionPath(folder), tag);
                        updated.Add((build, folder, tag));
                    }
                    catch
                    {
                        // This install keeps its files and is tried again next time.
                    }
                }
            }
            catch
            {
                // Offline, rate limited, ...: the installed files keep working.
            }
            finally
            {
                UpdateLock.Release();
            }
            return updated;
        }

        /// <summary>
        /// Installs a build's game DLLs from a GameFiles zip. A loaded DLL (the game is running) can't be overwritten but can
        /// be renamed, so the old one is moved aside.
        /// </summary>
        private static void InstallGameFiles(ZipArchive archive, GameBuild build, string installFolder)
        {
            foreach (var (entryName, file) in GameFilesFor(build))
            {
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry == null)
                    continue;
                string target = Path.Combine(installFolder, file);
                string temp = target + ".new";
                using (Stream source = entry.Open())
                using (FileStream destination = File.Create(temp))
                    source.CopyTo(destination);
                if (File.Exists(target))
                {
                    // An older copy still loaded by a running game can't be deleted; keep it under another name.
                    string old = target + ".old";
                    try { File.Delete(old); } catch { }
                    if (File.Exists(old))
                        old = target + ".old" + DateTime.UtcNow.Ticks;
                    File.Move(target, old);
                }
                File.Move(temp, target);
            }
        }
        #endregion

        #region Download
        /// <summary>
        /// Converts share links from common hosts into direct download links.
        /// </summary>
        public static string ToDirectUrl(string url)
        {
            // Google Drive: https://drive.google.com/file/d/<id>/view?... or open?id=<id>
            Match drive = Regex.Match(url, @"drive\.google\.com/(?:file/d/|open\?id=|uc\?(?:.*&)?id=)([\w-]+)");
            if (drive.Success)
                return $"https://drive.usercontent.google.com/download?id={drive.Groups[1].Value}&export=download&confirm=t";
            // Dropbox: ?dl=0 -> ?dl=1
            if (url.Contains("dropbox.com"))
                return Regex.Replace(url, @"([?&])dl=0", "$1dl=1") + (url.Contains("dl=") ? "" : (url.Contains("?") ? "&dl=1" : "?dl=1"));
            return url;
        }

        /// <summary>
        /// A download link that can't serve the file right now (a web page instead of the file, or an HTTP error), so the
        /// next mirror should be tried.
        /// </summary>
        private class DownloadSourceException : Exception
        {
            public DownloadSourceException(string message, Exception? inner = null) : base(message, inner) { }
        }

        private async Task DownloadAsync(GameBuild build, string partial, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(build.DownloadUrl))
                throw new InvalidOperationException($"This installer has no download link for the {build.Name} yet (Resources\\installer.json → {(build.Id == "summer" ? "downloadUrl" : build.Id + "DownloadUrl")}).");

            // The main link, then any mirrors. All serve the same archive, so a partial download resumes on the next one
            // (the checksum is verified afterwards either way).
            string[] sources = new[] { build.DownloadUrl }.Concat(build.DownloadMirrors).Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().ToArray();
            // Proton Drive links download through their web page (see ProtonDownload), which can't resume: resuming a partial
            // download (from another link) tries the other links first, and without WebView2 Proton links are skipped.
            bool resuming = File.Exists(partial) && new FileInfo(partial).Length > 0;
            bool proton = Owner != null && sources.Any(ProtonDownload.IsProtonLink) && ProtonDownload.IsAvailable();
            sources = sources.Where(u => proton || !ProtonDownload.IsProtonLink(u))
                .OrderBy(u => resuming && ProtonDownload.IsProtonLink(u) ? 1 : 0).ToArray();
            if (sources.Length == 0)
                throw new InvalidOperationException("The game is only on Proton Drive, which needs Microsoft Edge WebView2 (part of Windows 10/11). Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.");
            // Every link's failure is kept for the final message (the last one alone hid why the others failed), and the
            // Proton Drive page's steps go to %LOCALAPPDATA%\EchoClassicLobbies\download.log.
            System.Collections.Generic.List<string> failures = new System.Collections.Generic.List<string>();
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoClassicLobbies", "download.log");
            void Log(string message)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}" + Environment.NewLine);
                }
                catch { }
            }
            Log($"{build.Name}: downloading from {sources.Length} link(s){(resuming ? ", resuming a partial download" : "")}");
            for (int i = 0; ; i++)
            {
                try
                {
                    if (ProtonDownload.IsProtonLink(sources[i]))
                    {
                        try
                        {
                            await ProtonDownload.DownloadAsync(sources[i], partial, build.DownloadSizeBytes, Owner!, progress, cancel, message => Log("Proton Drive: " + message));
                        }
                        catch (ProtonDownloadException ex)
                        {
                            throw new DownloadSourceException(ex.Message, ex);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            throw new DownloadSourceException("Proton Drive: " + ex.Message, ex);
                        }
                    }
                    else
                        await DownloadFromAsync(build, ToDirectUrl(sources[i]), partial, progress, cancel);
                    return;
                }
                catch (DownloadSourceException ex) when (i + 1 < sources.Length)
                {
                    // Try the next mirror.
                    failures.Add($"{DescribeSource(sources[i])}: {ex.Message}");
                    Log($"{DescribeSource(sources[i])} failed: {ex.Message}");
                }
                catch (DownloadSourceException ex) when (sources.Length > 1)
                {
                    failures.Add($"{DescribeSource(sources[i])}: {ex.Message}");
                    Log($"{DescribeSource(sources[i])} failed: {ex.Message}");
                    throw new InvalidOperationException($"None of the {sources.Length} download links worked." + Environment.NewLine + string.Join(Environment.NewLine, failures), ex);
                }
                catch (DownloadSourceException ex)
                {
                    Log($"{DescribeSource(sources[i])} failed: {ex.Message}");
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// Names a download link's host for messages ("Proton Drive", "Google Drive", ...).
        /// </summary>
        private static string DescribeSource(string url)
        {
            if (ProtonDownload.IsProtonLink(url))
                return "Proton Drive";
            if (url.Contains("drive.google.com") || url.Contains("drive.usercontent.google.com"))
                return "Google Drive";
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "Download link";
        }

        private async Task DownloadFromAsync(GameBuild build, string url, string partial, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            progress.Report(new InstallProgress { Stage = InstallStage.Connecting, Detail = "Connecting…" });

            for (int attempt = 0; ; attempt++)
            {
                long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

                // The whole file is already here.
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
                    return;
                if (!response.IsSuccessStatusCode)
                    throw new DownloadSourceException($"The download server answered {(int)response.StatusCode} ({response.ReasonPhrase}).");

                // Some hosts answer with an HTML confirmation page (e.g. Google Drive's large file warning); follow its form.
                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType != null && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await response.Content.ReadAsStringAsync();
                    string? next = FollowConfirmationPage(html, url);
                    if (next == null || attempt >= 3)
                    {
                        // Keep the page for troubleshooting, then say what it was.
                        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(partial)!, "download_error.html"), html); } catch { }
                        throw new DownloadSourceException(DescribeDownloadPage(html));
                    }
                    url = next;
                    continue;
                }

                bool resuming = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                long total = response.Content.Headers.ContentLength is long length ? length + (resuming ? existing : 0) : build.DownloadSizeBytes;
                long done = resuming ? existing : 0;

                using Stream source = await response.Content.ReadAsStreamAsync();
                using FileStream target = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                byte[] buffer = new byte[1 << 20];
                Stopwatch clock = Stopwatch.StartNew();
                long windowStart = done;
                double windowTime = 0, speed = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, cancel);
                    done += read;

                    double elapsed = clock.Elapsed.TotalSeconds;
                    if (elapsed - windowTime >= 0.5)
                    {
                        double instant = (done - windowStart) / (elapsed - windowTime);
                        speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3;
                        windowStart = done;
                        windowTime = elapsed;
                        progress.Report(new InstallProgress
                        {
                            Stage = InstallStage.Downloading,
                            Fraction = total > 0 ? (double)done / total : -1,
                            Detail = DescribeDownload(done, total, speed),
                        });
                    }
                }
                if (total > 0 && done < total)
                    throw new IOException("The download ended early. Press Install again to resume it.");
                return;
            }
        }

        /// <summary>
        /// Explains why a download link returned a web page instead of the file.
        /// </summary>
        private static string DescribeDownloadPage(string html)
        {
            string text = html.ToLowerInvariant();
            if (text.Contains("quota exceeded") || text.Contains("too many users have viewed or downloaded"))
                return "Google Drive's download limit for this file has been reached (too many downloads recently). This usually clears within 24 hours; press Resume download later. Your progress so far is kept.";
            if (text.Contains("you need access") || text.Contains("request access") || text.Contains("accounts.google.com") || text.Contains("sign in"))
                return "The game files aren't shared publicly, so Google Drive asked for a sign-in. The file's owner needs to share it as \"Anyone with the link\".";
            if (text.Contains("not found") || text.Contains("does not exist") || text.Contains("404"))
                return "The game files weren't found at the download link. It may have been moved or deleted.";
            return "The download link returned a web page instead of the game files. Try again later; if it keeps happening, the download link may be wrong.";
        }

        private static string? FollowConfirmationPage(string html, string baseUrl)
        {
            Match form = Regex.Match(html, @"<form[^>]*action=""([^""]+)""[^>]*>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!form.Success)
                return null;
            string action = WebUtility.HtmlDecode(form.Groups[1].Value);
            var inputs = Regex.Matches(form.Groups[2].Value, @"<input[^>]*name=""([^""]+)""[^>]*value=""([^""]*)""", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => Uri.EscapeDataString(m.Groups[1].Value) + "=" + Uri.EscapeDataString(WebUtility.HtmlDecode(m.Groups[2].Value)));
            Uri absolute = new Uri(new Uri(baseUrl), action);
            return absolute + (absolute.Query.Length > 0 ? "&" : "?") + string.Join("&", inputs);
        }

        public static string DescribeDownload(long done, long total, double speed)
        {
            StringBuilder text = new StringBuilder();
            text.Append(FormatBytes(done));
            if (total > 0)
                text.Append(" of ").Append(FormatBytes(total));
            if (speed > 0)
            {
                text.Append("  ·  ").Append(FormatBytes((long)speed)).Append("/s");
                if (total > 0)
                    text.Append("  ·  ").Append(FormatDuration(TimeSpan.FromSeconds((total - done) / speed))).Append(" left");
            }
            return text.ToString();
        }
        #endregion

        #region Verify / extract / configure
        private void Verify(GameBuild build, string archive, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(build.Sha256))
                return;
            using SHA256 sha = SHA256.Create();
            using FileStream stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            byte[] buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                progress.Report(new InstallProgress { Stage = InstallStage.Verifying, Fraction = (double)done / stream.Length, Detail = "Checking the download" });
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
            // Links may serve different releases of the download (e.g. an older upload on a mirror); any listed checksum passes.
            string[] accepted = build.Sha256.Split(new[] { ' ', ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (!accepted.Any(expected => actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(archive);
                throw new InvalidDataException("The download is corrupted (checksum mismatch). Press Install to download it again.");
            }
        }

        private static void Extract(GameBuild build, string archivePath, string installFolder, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);

            // The archive may wrap the game in a folder; the game root is wherever the game executable (bin/win7/...) sits.
            string marker = build.Executable.Replace('\\', '/');
            ZipArchiveEntry? exe = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(marker, StringComparison.OrdinalIgnoreCase));
            if (exe == null)
                throw new InvalidDataException($"The download doesn't contain the game ({build.Executable} not found in the archive).");
            string prefix = exe.FullName.Replace('\\', '/');
            prefix = prefix.Substring(0, prefix.Length - marker.Length);

            string root = Path.GetFullPath(installFolder) + Path.DirectorySeparatorChar;
            var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            long total = Math.Max(1, entries.Sum(e => e.Length));
            long done = 0;
            byte[] buffer = new byte[1 << 20];
            Stopwatch clock = Stopwatch.StartNew();
            long lastReport = -1000;
            foreach (ZipArchiveEntry entry in entries)
            {
                cancel.ThrowIfCancellationRequested();
                string relative = entry.FullName.Replace('\\', '/').Substring(prefix.Length);
                if (relative.Length == 0)
                    continue;
                string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue; // never write outside the install folder
                if (relative.EndsWith("/"))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (Stream source = entry.Open())
                using (FileStream target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    // The game archives are single multi-GB files, so report progress while copying, not per file.
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        done += read;
                        if (clock.ElapsedMilliseconds - lastReport >= 200)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            progress.Report(new InstallProgress { Stage = InstallStage.Extracting, Fraction = (double)done / total, Detail = DescribeExtract(relative, done, total) });
                        }
                    }
                }
                progress.Report(new InstallProgress { Stage = InstallStage.Extracting, Fraction = (double)done / total, Detail = DescribeExtract(relative, done, total) });
            }
        }

        #region Config and credentials
        /// <summary>
        /// The placeholder values EchoRelay puts in generated configs; never shown to the player as their name/password.
        /// </summary>
        private static readonly string[] PlaceholderValues = { "AccountName", "AccountPassword" };

        private static readonly Regex PublisherLockPattern = new Regex(@"(""publisher_lock""\s*:\s*"")((?:[^""\\]|\\.)*)("")");

        private static readonly Regex LoginHostPattern = new Regex(@"(""(?:loginservice_host|login_host)""\s*:\s*"")((?:[^""\\]|\\.)*)("")");

        public static string ConfigPath(string installFolder) => Path.Combine(installFolder, "_local", "config.json");

        /// <summary>
        /// Reads the config of an existing install, if there is one.
        /// </summary>
        public static string? ReadInstalledConfig(string installFolder)
        {
            try
            {
                string path = ConfigPath(installFolder);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks that a config is a JSON object with a login service host. Returns an error message, or null if it's usable.
        /// </summary>
        public static string? ValidateConfig(string config)
        {
            try
            {
                using var reader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(
                    Encoding.UTF8.GetBytes(config), new System.Xml.XmlDictionaryReaderQuotas());
                System.Xml.Linq.XElement root = System.Xml.Linq.XElement.Load(reader);
                if ((string?)root.Attribute("type") != "object")
                    return "The config must be a JSON object ({ ... }).";
            }
            catch (Exception e)
            {
                return "The config isn't valid JSON: " + e.Message;
            }
            string? login = GetLoginHost(config);
            if (string.IsNullOrWhiteSpace(login))
                return "The config has no \"loginservice_host\", so the game couldn't log in.";
            if (!login!.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !login.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                return "\"loginservice_host\" must be a ws:// or wss:// address.";
            return null;
        }

        /// <summary>
        /// The login service URL in a config, or null.
        /// </summary>
        public static string? GetLoginHost(string config)
        {
            Match m = LoginHostPattern.Match(config);
            return m.Success ? Regex.Unescape(m.Groups[2].Value) : null;
        }

        /// <summary>
        /// The server a config points at (the login service's host:port), for display.
        /// </summary>
        public static string DescribeServer(string config)
        {
            string? login = GetLoginHost(config);
            return login != null && Uri.TryCreate(login, UriKind.Absolute, out Uri? uri) ? uri.Authority : "unknown server";
        }

        /// <summary>
        /// Reads the display name and password from a config's login URL (displayname= / auth=), ignoring placeholders.
        /// </summary>
        public static (string? displayName, string? password) ReadCredentials(string config)
        {
            string? login = GetLoginHost(config);
            if (login == null)
                return (null, null);
            string? Get(string key)
            {
                Match m = Regex.Match(login, @"[?&]" + key + @"=([^&#]*)");
                if (!m.Success)
                    return null;
                string value = Uri.UnescapeDataString(m.Groups[1].Value.Replace('+', ' '));
                return value.Length == 0 || Array.IndexOf(PlaceholderValues, value) >= 0 ? null : value;
            }
            return (Get("displayname"), Get("auth"));
        }

        /// <summary>
        /// Puts the display name and password into a config's login URL (displayname= and auth=, added if missing).
        /// </summary>
        public static string ApplyCredentials(string config, string displayName, string password)
        {
            return LoginHostPattern.Replace(config, m =>
            {
                string login = Regex.Unescape(m.Groups[2].Value);
                login = SetQueryParameter(login, "auth", password);
                login = SetQueryParameter(login, "displayname", displayName.Trim());
                // Encoded values only contain URL-safe characters, but keep the JSON string valid regardless.
                string escaped = login.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return m.Groups[1].Value + escaped + m.Groups[3].Value;
            }, 1);
        }

        private static string SetQueryParameter(string url, string key, string value)
        {
            string encoded = Uri.EscapeDataString(value);
            string fragment = "";
            int hash = url.IndexOf('#');
            if (hash >= 0)
            {
                fragment = url.Substring(hash);
                url = url.Substring(0, hash);
            }
            Regex existing = new Regex(@"([?&]" + Regex.Escape(key) + @"=)[^&]*");
            if (existing.IsMatch(url))
                url = existing.Replace(url, m => m.Groups[1].Value + encoded, 1);
            else
                url += (url.Contains("?") ? "&" : "?") + key + "=" + encoded;
            return url + fragment;
        }

        /// <summary>
        /// Sets a config's publisher_lock (added if missing), which tells EchoRelay which build is logging in.
        /// </summary>
        public static string ApplyPublisherLock(string config, string publisherLock)
        {
            if (PublisherLockPattern.IsMatch(config))
                return PublisherLockPattern.Replace(config, m => m.Groups[1].Value + publisherLock + m.Groups[3].Value, 1);
            int close = config.LastIndexOf('}');
            if (close < 0)
                return config;
            string before = config.Substring(0, close).TrimEnd();
            string separator = before.EndsWith("{") ? "" : ",";
            return before + separator + "\n  \"publisher_lock\": \"" + publisherLock + "\"\n" + config.Substring(close);
        }

        /// <summary>
        /// Writes a config to &lt;install&gt;\_local\config.json with the player's display name and password in the login
        /// URL and the build's publisher_lock, keeping a backup of a different existing one.
        /// </summary>
        public static void WriteConfig(string installFolder, string config, string displayName, string password, GameBuild build, string publisherLock)
        {
            config = ApplyPublisherLock(ApplyCredentials(config, displayName, password), publisherLock);
            if (build.UsesLegacyConfigKeys)
                config = AddLegacyConfigKeys(config);
            string path = ConfigPath(installFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && File.ReadAllText(path) != config)
                File.Copy(path, path + ".bak", true);
            File.WriteAllText(path, config, new UTF8Encoding(false));
        }

        /// <summary>
        /// Reads a string value from a flat config, or null.
        /// </summary>
        private static string? GetConfigValue(string config, string key)
        {
            Match m = new Regex(@"""" + Regex.Escape(key) + @"""\s*:\s*""((?:[^""\\]|\\.)*)""").Match(config);
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        /// <summary>
        /// Sets a string value in a flat config, adding the key if it is missing.
        /// </summary>
        private static string SetConfigValue(string config, string key, string value)
        {
            string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            Regex existing = new Regex(@"(""" + Regex.Escape(key) + @"""\s*:\s*"")((?:[^""\\]|\\.)*)("")");
            if (existing.IsMatch(config))
                return existing.Replace(config, m => m.Groups[1].Value + escaped + m.Groups[3].Value, 1);
            int close = config.LastIndexOf('}');
            if (close < 0)
                return config;
            string before = config.Substring(0, close).TrimEnd();
            string separator = before.EndsWith("{") ? "" : ",";
            return before + separator + "\n  \"" + key + "\": \"" + escaped + "\"\n" + config.Substring(close);
        }

        /// <summary>
        /// Adds the service keys the christmas 2017 build reads (login_host, matchmaker_host, serverdb_host and
        /// radserverdb_host) from the keys of the later builds' configs (loginservice_host, matchingservice_host,
        /// serverdb_host). Services a config doesn't name are assumed next to the login service, as EchoRelay serves them.
        /// </summary>
        public static string AddLegacyConfigKeys(string config)
        {
            string? login = GetConfigValue(config, "loginservice_host") ?? GetConfigValue(config, "login_host");
            if (login == null)
                return config;
            string SiblingService(string name)
            {
                if (!Uri.TryCreate(login, UriKind.Absolute, out Uri? uri))
                    return login;
                string path = uri.AbsolutePath;
                int slash = path.LastIndexOf('/');
                return uri.GetLeftPart(UriPartial.Authority) + (slash >= 0 ? path.Substring(0, slash) : "") + "/" + name;
            }
            string matching = GetConfigValue(config, "matchingservice_host") ?? GetConfigValue(config, "matchmaker_host") ?? SiblingService("matching");
            string serverDb = GetConfigValue(config, "serverdb_host") ?? SiblingService("serverdb");
            config = SetConfigValue(config, "login_host", login);
            config = SetConfigValue(config, "matchmaker_host", matching);
            config = SetConfigValue(config, "serverdb_host", serverDb);
            config = SetConfigValue(config, "radserverdb_host", serverDb);
            return config;
        }
        #endregion

        /// <summary>The desktop shortcut's name (EchoClassicLobbies.exe, not a game).</summary>
        public const string AppShortcutName = "Echo VR Classic Lobbies";

        /// <summary>
        /// Creates a desktop shortcut to this app (not the game), so players start through it and get their game file
        /// updates and the request buttons. The app is copied to %LOCALAPPDATA%\EchoClassicLobbies first, so the shortcut
        /// keeps working after the downloaded copy is deleted. Desktop shortcuts older versions made straight to a game are
        /// removed.
        /// </summary>
        public void CreateDesktopShortcut(string installFolder, GameBuild build)
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            string running = Process.GetCurrentProcess().MainModule!.FileName;
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoClassicLobbies");
            string app = Path.Combine(home, "EchoClassicLobbies.exe");
            if (!string.Equals(Path.GetFullPath(running), Path.GetFullPath(app), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(home);
                File.Copy(running, app, true);
            }

            dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, AppShortcutName + ".lnk"));
            shortcut.TargetPath = app;
            shortcut.WorkingDirectory = home;
            shortcut.IconLocation = app + ",0";
            shortcut.Description = "Play the Echo VR classic lobbies (updates, game server requests)";
            shortcut.Save();

            // Old shortcuts straight to a game skip the app (and its updates); remove the ones made by older versions.
            foreach (string name in _settings.Builds.Select(b => b.ShortcutName).Distinct())
            {
                string old = Path.Combine(desktop, name + ".lnk");
                try
                {
                    if (!File.Exists(old))
                        continue;
                    string target = (string)shell.CreateShortcut(old).TargetPath;
                    string file = Path.GetFileName(target);
                    if (file.Equals("echovr.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("EchoArena.exe", StringComparison.OrdinalIgnoreCase))
                        File.Delete(old);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Starts the game (from the install folder, like the Play shortcut).
        /// </summary>
        public static void Launch(string installFolder, GameBuild build)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(installFolder, build.Executable))
            {
                WorkingDirectory = installFolder,
                UseShellExecute = true,
            });
        }
        #endregion

        #region Formatting
        private static string DescribeExtract(string file, long done, long total)
        {
            return $"{FormatBytes(done)} of {FormatBytes(total)}  ·  {Path.GetFileName(file.TrimEnd('/'))}";
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
        }

        public static string FormatDuration(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours} h {time.Minutes} min";
            if (time.TotalMinutes >= 1)
                return $"{(int)time.TotalMinutes} min";
            return $"{Math.Max(1, time.Seconds)} s";
        }
        #endregion
    }
}
