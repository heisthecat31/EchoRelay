using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SummerInstaller
{
    /// <summary>
    /// Downloads a Proton Drive public link (https://drive.proton.me/urls/ID#PASSWORD). Proton encrypts shared files and only
    /// its web page can decrypt them, so the page is loaded in a WebView2 (Edge) window kept off screen and its Download
    /// button pressed. The page downloads and decrypts the file itself (its transfer panel shows how far it is, which is
    /// passed on as progress), then saves it, and that save is caught and written where the installer wants it. If the page
    /// needs the player (no download starts), the window is shown so they can press Download themselves.
    /// </summary>
    public static class ProtonDownload
    {
        public static bool IsProtonLink(string url) => Regex.IsMatch(url.Trim(), @"^https://drive\.proton\.me/urls/[A-Za-z0-9]+#.+", RegexOptions.IgnoreCase);

        /// <summary>
        /// Whether WebView2 (the Edge runtime, part of Windows 10/11) is available to download from Proton Drive.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                PrepareLoader();
                return !string.IsNullOrEmpty(GetRuntimeVersion());
            }
            catch
            {
                return false;
            }
        }

        // Kept separate so a missing WebView2 assembly only fails here, not in the caller.
        private static string GetRuntimeVersion() => CoreWebView2Environment.GetAvailableBrowserVersionString();

        private static string AppDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoClassicLobbies");

        private static bool _loaderPrepared;

        /// <summary>
        /// WebView2Loader.dll is embedded in the installer (so it stays a single exe); it's written next to the WebView2 data
        /// and pointed to before the first WebView2 is created. WebView2 only accepts that before any other call to it, so it's
        /// done once: IsAvailable's check runs first and a second SetLoaderDllFolderPath would throw ("The function should be
        /// called before any other API is called in CoreWebView2Environment class").
        /// </summary>
        private static void PrepareLoader()
        {
            if (_loaderPrepared)
                return;
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            };
            string folder = Path.Combine(AppDataFolder, "webview2", arch);
            string path = Path.Combine(folder, "WebView2Loader.dll");
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"webview2.{arch}.WebView2Loader.dll");
            if (resource == null)
                throw new FileNotFoundException("WebView2Loader.dll isn't embedded in the installer.");
            if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
            {
                Directory.CreateDirectory(folder);
                using FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write);
                resource.CopyTo(file);
            }
            CoreWebView2Environment.SetLoaderDllFolderPath(folder);
            _loaderPrepared = true;
        }

        // Presses the page's Download button: the big one under the file ("This file is too large to preview"), or the one in
        // the toolbar. Returns what it pressed, or "" if there's no button yet.
        private const string ClickDownloadScript = @"(() => {
            const visible = b => b.offsetParent !== null && !b.disabled;
            const buttons = [...document.querySelectorAll('button')].filter(b => visible(b) && /^\s*download\s*$/i.test(b.textContent));
            const main = buttons.find(b => b.classList.contains('button-solid-norm')) || buttons.find(b => b.getAttribute('data-testid') === 'dropdown-download-button') || buttons[0];
            if (!main) return '';
            main.click();
            return main.getAttribute('data-testid') || main.className;
        })()";

        // Reads Proton's transfer panel: the first row's status ("Downloading", "Failed", ...) and data ("172 MB / 2.2 GB"),
        // as "status|data", or "" if no download is listed.
        private const string TransfersScript = @"(() => {
            const row = document.querySelector('[data-testid=""transfer-item-row""]');
            if (!row) return '';
            const text = id => ((row.querySelector('[data-testid=""' + id + '""]') || {}).innerText || '').replace(/\|/g, ' ');
            return (text('transfer-row:status') || row.innerText.replace(/\|/g, ' ')) + '|' + text('transfer-row:transferred-data');
        })()";

        /// <summary>
        /// The string a script returned (ExecuteScriptAsync gives it JSON-encoded).
        /// </summary>
        private static string ScriptString(string json) => json.Length >= 2 && json[0] == '"' ? Regex.Unescape(json.Substring(1, json.Length - 2)) : "";

        /// <summary>
        /// Bytes from Proton's sizes ("172 MB", "2.2 GB", "900 bytes"); Proton counts in 1024s.
        /// </summary>
        private static long ParseSize(string text)
        {
            Match size = Regex.Match(text, @"([\d.,]+)\s*(bytes|B|kB|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            if (!size.Success || !double.TryParse(size.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return -1;
            int power = size.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, "TB" => 4, _ => 0 };
            return (long)(value * Math.Pow(1024, power));
        }

        /// <summary>
        /// Downloads the file behind a Proton Drive link to <paramref name="destination"/>. Must be called on the UI thread.
        /// </summary>
        /// <exception cref="ProtonDownloadException">The page didn't give the file (the caller can try another link).</exception>
        public static async Task DownloadAsync(string url, string destination, long expectedSize, Window owner, IProgress<InstallProgress> progress, CancellationToken cancel,
            Action<string>? log = null)
        {
            PrepareLoader();
            progress.Report(new InstallProgress { Stage = InstallStage.Connecting, Detail = "Opening Proton Drive…" });

            string temp = destination + ".proton";
            try { File.Delete(temp); } catch { }

            // Off screen rather than hidden: a hidden window's page is throttled, and the window must exist for WebView2.
            Window window = new Window
            {
                Title = "Proton Drive: press Download to get the game",
                Width = 960,
                Height = 720,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Owner = owner,
            };
            WebView2 web = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(AppDataFolder, "webview2", "data") },
            };
            window.Content = web;
            bool closedByPlayer = false, finishing = false;
            window.Closing += (_, _) => { if (!finishing) closedByPlayer = true; };

            TaskCompletionSource<bool> saving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CoreWebView2DownloadOperation? operation = null;
            CoreWebView2? core = null;
            try
            {
                window.Show();
                await web.EnsureCoreWebView2Async();
                core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
                // The page saves the file once it has downloaded and decrypted it: catch that save.
                core.DownloadStarting += (_, e) =>
                {
                    log?.Invoke($"saving: {e.DownloadOperation.TotalBytesToReceive} bytes -> {temp}");
                    if (operation != null)
                    {
                        e.Cancel = true;
                        return;
                    }
                    operation = e.DownloadOperation;
                    e.ResultFilePath = temp;
                    e.Handled = true; // no download bubble
                    operation.StateChanged += (_, _) =>
                    {
                        if (operation.State == CoreWebView2DownloadState.Completed)
                            finished.TrySetResult(true);
                        else if (operation.State == CoreWebView2DownloadState.Interrupted)
                            finished.TrySetException(new ProtonDownloadException("Saving the Proton Drive download failed (" + operation.InterruptReason + ")."));
                    };
                    saving.TrySetResult(true);
                    window.Left = window.Top = -32000;
                };

                core.Navigate(url.Trim());
                using (cancel.Register(() => { saving.TrySetCanceled(); finished.TrySetCanceled(); }))
                {
                    DateTime begin = DateTime.UtcNow, lastClick = DateTime.MinValue, lastGrowth = DateTime.UtcNow;
                    long lastDone = -1, total = expectedSize;
                    bool shown = false, transferSeen = false;
                    double speed = 0;
                    DateTime speedTime = DateTime.UtcNow;
                    while (!saving.Task.IsCompleted)
                    {
                        if (closedByPlayer)
                            throw new ProtonDownloadException("The Proton Drive window was closed before the download finished.");
                        DateTime now = DateTime.UtcNow;

                        string transfer = ScriptString(await core.ExecuteScriptAsync(TransfersScript));
                        if (transfer.Length > 0)
                        {
                            if (!transferSeen)
                                log?.Invoke("download listed: " + transfer.Replace('\n', ' '));
                            transferSeen = true;
                            int bar = transfer.IndexOf('|');
                            string status = transfer.Substring(0, bar);
                            string data = transfer.Substring(bar + 1);
                            if (Regex.IsMatch(status, @"fail|error|cancel", RegexOptions.IgnoreCase))
                                throw new ProtonDownloadException($"Proton Drive's download stopped ({status.Trim()}).");
                            string[] parts = data.Split('/');
                            long done = ParseSize(parts[0]);
                            if (parts.Length > 1 && expectedSize <= 0 && ParseSize(parts[1]) is long pageTotal && pageTotal > 0)
                                total = pageTotal;
                            if (done > lastDone)
                            {
                                double seconds = (now - speedTime).TotalSeconds;
                                if (lastDone >= 0 && seconds > 0)
                                {
                                    double instant = (done - lastDone) / seconds;
                                    speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3;
                                }
                                speedTime = now;
                                lastDone = done;
                                lastGrowth = now;
                            }
                            progress.Report(new InstallProgress
                            {
                                Stage = InstallStage.Downloading,
                                Fraction = total > 0 && done >= 0 ? Math.Min(1, (double)done / total) : -1,
                                Detail = (done >= 0 ? GameInstaller.DescribeDownload(done, total, speed) : "Starting") + "  ·  Proton Drive",
                            });
                            if (now - lastGrowth > TimeSpan.FromMinutes(3))
                                throw new ProtonDownloadException("Proton Drive's download stalled.");
                        }
                        else if (!transferSeen && now - lastClick > TimeSpan.FromSeconds(20))
                        {
                            // Press Download once the page has opened the file (it decrypts the link first); again only if no
                            // download appeared, so the file isn't downloaded twice.
                            string pressed = ScriptString(await core.ExecuteScriptAsync(ClickDownloadScript));
                            log?.Invoke($"page '{core.DocumentTitle}': pressed '{pressed}'");
                            if (pressed.Length > 0)
                                lastClick = now;
                        }

                        if (!transferSeen && !shown && now - begin > TimeSpan.FromSeconds(60))
                        {
                            // No download started: something needs the player (e.g. a check the page wants done). Show the page.
                            shown = true;
                            window.Left = owner.Left + (owner.ActualWidth - window.Width) / 2;
                            window.Top = Math.Max(0, owner.Top + (owner.ActualHeight - window.Height) / 2);
                            window.ShowInTaskbar = true;
                            window.Activate();
                            progress.Report(new InstallProgress { Stage = InstallStage.Connecting, Detail = "Press Download in the Proton Drive window…" });
                        }
                        if (!transferSeen && now - begin > TimeSpan.FromMinutes(5))
                            throw new ProtonDownloadException("Proton Drive didn't start the download.");
                        await Task.WhenAny(saving.Task, Task.Delay(1000));
                    }
                    await saving.Task;
                    progress.Report(new InstallProgress { Stage = InstallStage.Downloading, Fraction = 1, Detail = "Saving the download…" });
                    await finished.Task;
                }

                finishing = true;
                window.Close();
                if (!File.Exists(temp))
                    throw new ProtonDownloadException("Proton Drive said the download finished, but the file isn't there.");
                if (File.Exists(destination))
                    File.Delete(destination);
                File.Move(temp, destination);
            }
            catch
            {
                try { operation?.Cancel(); } catch { }
                throw;
            }
            finally
            {
                finishing = true;
                try { window.Close(); } catch { }
                try { web.Dispose(); } catch { }
                // Let WebView2 release the file before cleaning up.
                await Task.Delay(300);
                try { File.Delete(temp); } catch { }
            }
        }
    }

    public class ProtonDownloadException : Exception
    {
        public ProtonDownloadException(string message) : base(message) { }
    }
}
