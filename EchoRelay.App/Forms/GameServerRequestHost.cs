using EchoRelay.App.Settings;
using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using System.Diagnostics;

namespace EchoRelay.App.Forms
{
    /// <summary>
    /// Starts game servers players request from the installer ("Request game server"), if the host allows it in Settings:
    /// a headless server of the requested build, from the executable saved for that build in the Version list. Each player
    /// can have a limited number running, and there is an overall limit.
    /// </summary>
    public class GameServerRequestHost
    {
        private readonly AppSettings _settings;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly List<(XPlatformId requester, string build, Process process)> _started = new List<(XPlatformId, string, Process)>();

        public GameServerRequestHost(AppSettings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
        }

        /// <summary>
        /// Decides a request (see <see cref="Server.GameServerRequestHandler"/>).
        /// </summary>
        public GameServerRequestResult Handle(GameServerRequest request)
        {
            lock (_lock)
            {
                GameServerRequestResult result = Decide(request);
                _log($"[REQUEST] {request.DisplayName} ({request.Address}) requested a {request.Build} game server: " +
                    $"{(result.Accepted ? "started" : "declined")} - {result.Message}\n");
                return result;
            }
        }

        private GameServerRequestResult Decide(GameServerRequest request)
        {
            if (!_settings.GameServerRequestsEnabled)
                return new GameServerRequestResult(false, "This server doesn't take game server requests.");

            string buildName = GameServerBuilds.Names[request.Build];
            if (!_settings.GameExecutables.TryGetValue(buildName, out string? executable) || !File.Exists(executable))
                return new GameServerRequestResult(false, $"This server doesn't host {buildName} game servers.");

            _started.RemoveAll(entry => HasExited(entry.process));
            int perPlayer = Math.Max(1, _settings.GameServerRequestsPerPlayer);
            int mine = _started.Count(entry => entry.requester == request.Requester);
            if (mine >= perPlayer)
                return new GameServerRequestResult(false, $"You already have {mine} requested game server{(mine == 1 ? "" : "s")} running (the limit is {perPlayer}).");
            int total = Math.Max(1, _settings.GameServerRequestsMax);
            if (_started.Count >= total)
                return new GameServerRequestResult(false, "This server is running as many requested game servers as it allows. Try again later.");

            Process? process = GameLauncher.Launch(executable, GameLauncher.LaunchRole.Server, noOVR: true, headless: true);
            if (process == null)
                return new GameServerRequestResult(false, "The game server didn't start.");
            _started.Add((request.Requester, request.Build, process));
            return new GameServerRequestResult(true, $"Starting a {buildName} game server. It takes about half a minute; then press Play in the game.");
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }
}
