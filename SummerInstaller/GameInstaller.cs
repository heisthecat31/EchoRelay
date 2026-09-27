using System;
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
            // needs, so nothing else is downloaded.
            WriteConfig(installFolder, config, displayName, password, build, publisherLock);

            // The archive is no longer needed.
            try { Directory.Delete(DownloadFolder(installFolder), true); } catch { }
            progress.Report(new InstallProgress { Stage = InstallStage.Done, Fraction = 1 });
        }

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

        private async Task DownloadAsync(GameBuild build, string partial, IProgress<InstallProgress> progress, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(build.DownloadUrl))
                throw new InvalidOperationException($"This installer has no download link for the {build.Name} yet (Resources\\installer.json → {(build.Id == "summer" ? "downloadUrl" : build.Id + "DownloadUrl")}).");

            string url = ToDirectUrl(build.DownloadUrl.Trim());
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
                response.EnsureSuccessStatusCode();

                // Some hosts answer with an HTML confirmation page (e.g. Google Drive's large file warning); follow its form.
                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType != null && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    string html = await response.Content.ReadAsStringAsync();
                    string? next = FollowConfirmationPage(html, url);
                    if (next == null || attempt >= 3)
                        throw new InvalidOperationException("The download link returned a web page instead of the game files. Check that the link is a direct download link.");
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

        private static string DescribeDownload(long done, long total, double speed)
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
            if (!actual.Equals(build.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Creates a desktop shortcut that starts the game from the install folder.
        /// </summary>
        public void CreateDesktopShortcut(string installFolder, GameBuild build)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string link = Path.Combine(desktop, build.ShortcutName + ".lnk");
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = Path.Combine(installFolder, build.Executable);
            shortcut.WorkingDirectory = installFolder;
            shortcut.IconLocation = Path.Combine(installFolder, build.Executable) + ",0";
            shortcut.Description = _settings.Title + " " + build.Name;
            shortcut.Save();
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
