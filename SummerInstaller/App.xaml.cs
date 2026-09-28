using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace SummerInstaller
{
    public partial class App : Application
    {
        public App()
        {
            // The WebView2 assemblies (for Proton Drive downloads) are embedded, so the installer stays a single exe.
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                if (!name.StartsWith("Microsoft.Web.WebView2.", StringComparison.Ordinal))
                    return null;
                using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("webview2." + name + ".dll");
                if (resource == null)
                    return null;
                using MemoryStream bytes = new MemoryStream();
                resource.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Diagnostics: EchoClassicLobbies.exe --test-proton-download URL FILE downloads one Proton Drive link to FILE,
            // logging progress to FILE.log, then exits (0 on success).
            if (e.Args.Length == 3 && e.Args[0] == "--test-proton-download")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                _ = TestProtonDownload(e.Args[1], e.Args[2]);
                return;
            }
            base.OnStartup(e);
            new MainWindow().Show();
        }

        private async System.Threading.Tasks.Task TestProtonDownload(string url, string file)
        {
            using StreamWriter log = new StreamWriter(file + ".log", false) { AutoFlush = true };
            Window owner = new Window { Width = 400, Height = 300, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            owner.Show();
            string last = "";
            Progress<InstallProgress> progress = new Progress<InstallProgress>(p =>
            {
                string line = $"{p.Stage} {(p.Fraction >= 0 ? (p.Fraction * 100).ToString("0.0") + "%" : "")} {p.Detail}";
                if (line != last)
                    log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
                last = line;
            });
            int code = 0;
            try
            {
                await ProtonDownload.DownloadAsync(url, file, 0, owner, progress, System.Threading.CancellationToken.None,
                    message => log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] DONE {new FileInfo(file).Length} bytes");
            }
            catch (Exception ex)
            {
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAILED {ex}");
                code = 1;
            }
            Shutdown(code);
        }
    }
}
