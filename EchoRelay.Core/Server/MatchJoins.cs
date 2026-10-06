using EchoRelay.Core.Game;
using System.Collections.Concurrent;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// Where an account's next matchmaking request (Play in the menu, a lobby terminal's find) should go, asked for over the
    /// API: a session to join ({api}/matches/join), or the game server it just requested ({api}/servers/request). Lobby
    /// builds can't be started into a session, so this is how a player gets there.
    /// </summary>
    public class MatchJoins
    {
        /// <summary>
        /// How long a join waits for the player's next matchmaking request.
        /// </summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<XPlatformId, (Guid Session, DateTime Until)> _pending = new();
        private readonly ConcurrentDictionary<XPlatformId, (string Build, DateTime Until)> _ownServers = new();

        public void Set(XPlatformId account, Guid session) => _pending[account] = (session, DateTime.UtcNow + Lifetime);

        /// <summary>
        /// The session the account asked to join, if it did in the last <see cref="Lifetime"/>; asked once.
        /// </summary>
        public Guid? Take(XPlatformId account)
        {
            if (_pending.TryRemove(account, out var join) && join.Until > DateTime.UtcNow)
                return join.Session;
            return null;
        }

        /// <summary>
        /// The account requested a game server of the build: its next request prefers a game server with no session yet.
        /// </summary>
        public void SetOwnServer(XPlatformId account, string build) => _ownServers[account] = (build, DateTime.UtcNow + Lifetime);

        /// <summary>
        /// The build the account requested a game server of, if it did in the last <see cref="Lifetime"/>. It stays until
        /// <see cref="UsedOwnServer"/> (the server may not have registered yet).
        /// </summary>
        public string? OwnServer(XPlatformId account)
        {
            if (_ownServers.TryGetValue(account, out var own) && own.Until > DateTime.UtcNow)
                return own.Build;
            _ownServers.TryRemove(account, out _);
            return null;
        }

        public void UsedOwnServer(XPlatformId account) => _ownServers.TryRemove(account, out _);
    }
}
