using EchoRelay.App.Settings;
using EchoRelay.Core.Server;

namespace EchoRelay.App.Forms
{
    /// <summary>
    /// Makes this PC a game server host for players' requests from the installer ("Request server"), if allowed in Settings:
    /// in the region set there, with the builds that have an executable in the Version list. Other PCs can host too, with
    /// EchoRelay.Host.
    /// </summary>
    public static class GameServerRequestHost
    {
        private static readonly GameServerLauncher Launcher = new GameServerLauncher();

        /// <summary>
        /// Registers (or, if requests are off, removes) this PC as a game server host of a server, from the settings.
        /// </summary>
        public static void Apply(Server server, AppSettings settings)
        {
            Dictionary<string, string> executables = new Dictionary<string, string>();
            foreach (var build in GameServerBuilds.Names)
                if (settings.GameExecutables.TryGetValue(build.Value, out string? path))
                    executables[build.Key] = path;
            Launcher.Executables = executables;
            Launcher.PerPlayer = settings.GameServerRequestsPerPlayer;
            Launcher.Max = settings.GameServerRequestsMax;
            server.GameServerHosts.SetLocalHost(settings.GameServerRequestsRegion, Environment.MachineName,
                settings.GameServerRequestsEnabled ? Launcher.Builds : Array.Empty<string>(), Launcher.Start);
        }
    }
}
