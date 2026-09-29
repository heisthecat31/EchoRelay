using EchoRelay.Core.Game;
using Newtonsoft.Json.Linq;
using System.IO.Compression;

namespace EchoRelay.App.Utils
{
    /// <summary>
    /// Keeps the EchoRelay DLLs (the patch and the game server plugin) in the game folders of the App's Version list up to
    /// date from the latest GitHub release, like the installer does for players and EchoRelay-Host.bat for host PCs, so the
    /// game servers this PC starts get fixes too. It asks first; saying no leaves the files alone until the next start.
    /// </summary>
    public static class GameFilesUpdater
    {
        /// <summary>A lobby build install: its name, root folder and whether it's a rad14 build (christmas or halloween 2017,
        /// which load dbghelp.dll).</summary>
        private record Install(string Name, string Folder, bool Christmas);

        /// <summary>
        /// The lobby build installs among the App's game executables (the final build has no EchoRelay DLLs).
        /// </summary>
        private static List<Install> FindInstalls(IEnumerable<string> executables)
        {
            List<Install> installs = new List<Install>();
            foreach (string exe in executables.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                uint? timestamp = SummerBuild.ReadPETimestamp(exe);
                bool christmas = SummerBuild.IsRad14ExecutableTimestamp(timestamp);
                if (!christmas && timestamp != SummerBuild.ExecutableTimestamp && timestamp != SummerBuild.HalloweenExecutableTimestamp
                    && timestamp != SummerBuild.WinterExecutableTimestamp)
                    continue;
                string? folder = Directory.GetParent(exe)?.Parent?.Parent?.FullName; // <folder>\bin\win7\<exe>
                if (folder != null && !installs.Any(i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    installs.Add(new Install(SummerBuild.GetBuildName(exe) ?? Path.GetFileName(folder), folder, christmas));
            }
            return installs;
        }

        private static string TagPath(string folder) => Path.Combine(folder, "bin", "win7", "echorelay_gamefiles.txt");

        private static string? InstalledTag(string folder)
        {
            try { return File.Exists(TagPath(folder)) ? File.ReadAllText(TagPath(folder)).Trim() : null; }
            catch { return null; }
        }

        /// <summary>
        /// Checks the latest release's GameFiles zip against the installs' and, if the person agrees, installs it. Quiet when
        /// offline or up to date.
        /// </summary>
        public static async Task CheckAsync(Form owner, IEnumerable<string> executables)
        {
            List<Install> installs = FindInstalls(executables);
            if (installs.Count == 0)
                return;
            string tag;
            string? asset;
            try
            {
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoRelay.App/" + AppUpdater.CurrentVersion.ToString(3));
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                JObject release = JObject.Parse(await http.GetStringAsync($"https://api.github.com/repos/{AppUpdater.Repository}/releases/latest"));
                tag = release.Value<string>("tag_name") ?? "";
                asset = (release["assets"] as JArray)?.Select(a => a.Value<string>("browser_download_url") ?? "")
                    .FirstOrDefault(url => url.EndsWith("-GameFiles.zip", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return;
            }
            if (tag.Length == 0 || asset == null)
                return;
            List<Install> outdated = installs.Where(install => InstalledTag(install.Folder) != tag).ToList();
            if (outdated.Count == 0)
                return;

            string list = string.Join("\n", outdated.Select(install => $"  • {install.Name}: {install.Folder}"));
            if (MessageBox.Show(owner,
                $"New EchoRelay game files ({tag}: the patch and game server DLLs) are out for the game versions this PC starts game servers of:\n\n" +
                $"{list}\n\nInstall them now? Game servers already running keep their old files until they're next started.\n\n" +
                "(No leaves the files as they are; you'll be asked again next time EchoRelay starts.)",
                "Echo Relay: Game file updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            List<string> updated = new List<string>(), failed = new List<string>();
            try
            {
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoRelay.App/" + AppUpdater.CurrentVersion.ToString(3));
                byte[] zip = await http.GetByteArrayAsync(asset);
                using ZipArchive archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                foreach (Install install in outdated)
                {
                    try
                    {
                        InstallFiles(archive, install);
                        File.WriteAllText(TagPath(install.Folder), tag);
                        updated.Add(install.Name);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{install.Name} ({ex.Message})");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Couldn't download the game files: " + ex.Message, "Echo Relay: Game file updates");
                return;
            }
            MessageBox.Show(owner,
                (updated.Count > 0 ? $"Updated to {tag}: {string.Join(", ", updated)}." : "") +
                (failed.Count > 0 ? $"\n\nNot updated: {string.Join(", ", failed)}." : ""),
                "Echo Relay: Game file updates");
        }

        /// <summary>
        /// Installs a build's DLLs from a GameFiles zip (christmas 2017 loads the patch as dbghelp.dll; the zip keeps its copies
        /// under christmas/). A DLL a running game server has loaded can't be overwritten but can be renamed, so it's moved aside.
        /// </summary>
        private static void InstallFiles(ZipArchive archive, Install install)
        {
            (string entry, string target)[] files = install.Christmas
                ? new[] { ("christmas/bin/win7/dbghelp.dll", @"bin\win7\dbghelp.dll"), ("christmas/bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") }
                : new[] { ("bin/win7/dbgcore.dll", @"bin\win7\dbgcore.dll"), ("bin/win7/pnsradgameserver.dll", @"bin\win7\pnsradgameserver.dll") };
            foreach (var (entryName, file) in files)
            {
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry == null)
                    continue;
                string target = Path.Combine(install.Folder, file);
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
    }
}
