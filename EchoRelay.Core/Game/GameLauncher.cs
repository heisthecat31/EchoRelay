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
