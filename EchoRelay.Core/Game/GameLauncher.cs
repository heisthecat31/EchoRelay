using System.Diagnostics;

namespace EchoRelay.Core.Game
{
    /// <summary>
    /// Launches the game with different configurable roles/settings.
    /// </summary>
    public abstract class GameLauncher
    {
        public static void Launch(string executableFilePath, LaunchRole role = LaunchRole.Client, bool windowed = false, bool spectatorStream = false, bool moderator = false, bool noOVR = false, bool headless = false, uint? timeStep = null, List<string>? additionalArgs = null)
        {
            // Create a list of arguments
            List<string> args = additionalArgs ?? new List<string>();

            // The summer lobby build (rad15_summer) has different flags: our dbgcore.dll (EchoRelay.Patch) implements
            // -server/-noovr for it, the game itself knows -headless and -spectatorstream (windowed), and there is no
            // offline mode, moderator flag or timestep option.
            // The christmas 2017 build (EchoArena.exe) only knows -novr; EchoRelay.Patch (dbghelp.dll) adds -server and an
            // emulated -headless (no audio, window hidden). It runs from the game's root folder.
            uint? timestamp = SummerBuild.ReadPETimestamp(executableFilePath);
            if (timestamp == SummerBuild.ChristmasExecutableTimestamp)
            {
                if (role == LaunchRole.Server)
                    args.Add("-server");
                if (windowed || spectatorStream || noOVR)
                    args.Add("-novr");
                if (headless)
                    args.Add("-headless");
                ProcessStartInfo startInfo = new ProcessStartInfo(executableFilePath);
                foreach (string arg in args)
                    startInfo.ArgumentList.Add(arg);
                startInfo.WorkingDirectory = Directory.GetParent(executableFilePath)?.Parent?.Parent?.FullName ?? "";
                Process.Start(startInfo);
                return;
            }

            // The halloween lobby build takes the same flags as summer (EchoRelay.Patch emulates the ones it lacks).
            if (timestamp == SummerBuild.ExecutableTimestamp || timestamp == SummerBuild.HalloweenExecutableTimestamp)
            {
                if (role == LaunchRole.Server)
                    args.Add("-server");
                if (windowed || spectatorStream)
                    args.Add("-spectatorstream");
                if (noOVR)
                    args.Add("-noovr");
                if (headless)
                    args.Add("-headless");
                Process.Start(executableFilePath, args);
                return;
            }

            // Add any role related arguments (client role = no CLI argument here)
            switch(role)
            {
                case LaunchRole.Server:
                    args.Add("-server");
                    break;

                case LaunchRole.Offline:
                    args.Add("-offline");
                    break;
            }

            // Add our flags
            if (windowed)
                args.Add("-windowed");
            if (spectatorStream)
                args.Add("-spectatorstream");
            if (moderator)
                args.Add("-moderator");
            if (noOVR)
                args.Add("-noovr");
            if (headless)
                args.Add("-headless");
            if(timeStep.HasValue)
            {
                args.Add("-timestep");
                args.Add(timeStep.Value.ToString());
            }

            // Start the process with our provided arguments.
            Process.Start(executableFilePath, args);
        }

        /// <summary>
        /// The game executables of every build (final and lobby builds use echovr.exe, christmas 2017 EchoArena.exe).
        /// </summary>
        private static readonly string[] GameProcessNames = { "echovr", "EchoArena" };

        /// <summary>
        /// Force-closes every game server running on this machine, whatever build, folder or state (including hidden
        /// headless servers, which have no window to close). A game server is a game process with pnsradgameserver.dll
        /// loaded, which only happens in server mode; the command line can't tell, since EchoRelay.Patch rewrites -server.
        /// Game clients are left running.
        /// </summary>
        /// <returns>The number of servers closed, and the ones that couldn't be (e.g. run by another user or as administrator).</returns>
        public static (int Closed, List<string> Failed) CloseAllGameServers()
        {
            int closed = 0;
            List<string> failed = new List<string>();
            foreach (Process process in GameProcessNames.SelectMany(Process.GetProcessesByName))
            {
                using (process)
                {
                    bool isServer;
                    try
                    {
                        isServer = process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Equals("pnsradgameserver.dll", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{process.ProcessName} (pid {process.Id}): can't be inspected ({ex.Message})");
                        continue;
                    }
                    if (!isServer)
                        continue;
                    try
                    {
                        process.Kill(true);
                        process.WaitForExit(5000);
                        closed++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{process.ProcessName} (pid {process.Id}): {ex.Message}");
                    }
                }
            }
            return (closed, failed);
        }

        #region Enums
        /// <summary>
        /// Describes the type of launch that should occur. A client, server, or offline mode.
        /// </summary>
        public enum LaunchRole : int
        {
            Client = 0,
            Server = 1,
            Offline = 2,
        }
        #endregion
    }
}
