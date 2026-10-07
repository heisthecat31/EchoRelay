using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Utils;
using System.Collections.Concurrent;
using System.Net;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Services.ServerDB
{
    public class GameServerRegistry
    {
        #region Properties
        public static readonly Guid ZeroGuid = new Guid("00000000-0000-0000-0000-000000000000");
        public ConcurrentDictionary<ulong, RegisteredGameServer> RegisteredGameServers { get; }
        public ConcurrentDictionary<Guid, RegisteredGameServer> RegisteredGameServersBySessionId { get; }
        /// <summary>
        /// How long a kicked game server is refused when it registers again (its plugin reconnects on its own within seconds).
        /// </summary>
        public static readonly TimeSpan KickBlockDuration = TimeSpan.FromMinutes(5);
        /// <summary>
        /// Kicked game servers, by the address they connect from and their game port, and when they may register again.
        /// </summary>
        private readonly ConcurrentDictionary<(string Address, ushort Port), DateTime> _kicked = new ConcurrentDictionary<(string, ushort), DateTime>();
        #endregion

        #region Events
        /// <summary>
        /// Event of a game server being registered/unregistered with the central server.
        /// </summary>
        /// <param name="gameServer">The <see cref="RegisteredGameServer"/> which was registered/unregistered with the central server.</param>
        public delegate void GameServerRegistrationChangedEventHandler(RegisteredGameServer gameServer);
        /// <summary>
        /// Event of a game server being registered.
        /// </summary>
        public event GameServerRegistrationChangedEventHandler? OnGameServerRegistered;
        /// <summary>
        /// Event of a game server being unregistered.
        /// </summary>
        public event GameServerRegistrationChangedEventHandler? OnGameServerUnregistered;
        #endregion

        #region Constructor
        public GameServerRegistry()
        {
            RegisteredGameServers = new ConcurrentDictionary<ulong, RegisteredGameServer>();
            RegisteredGameServersBySessionId = new ConcurrentDictionary<Guid, RegisteredGameServer>();
        }
        #endregion

        #region Functions
        public RegisteredGameServer AddGameServer(RegisteredGameServer registeredGameServer)
        {
            // Add the game server to our lookup
            RegisteredGameServers[registeredGameServer.ServerId] = registeredGameServer;

            // Fire the relevant event for the game server being registered.
            OnGameServerRegistered?.Invoke(registeredGameServer);
            return registeredGameServer;
        }

        public RegisteredGameServer? GetGameServer(ulong serverId)
        {
            RegisteredGameServers.TryGetValue(serverId, out RegisteredGameServer? registeredGameServer);
            return registeredGameServer;
        }

        public RegisteredGameServer? GetGameServer(Guid sessionId)
        {
            RegisteredGameServersBySessionId.TryGetValue(sessionId, out RegisteredGameServer? registeredGameServer);
            return registeredGameServer;
        }

        public void RemoveGameServer(RegisteredGameServer registeredGameServer)
        {
            RemoveGameServer(registeredGameServer.ServerId);
        }
        public void RemoveGameServer(ulong serverId)
        {
            // Try to remove any registered game server with this server identifier.
            RegisteredGameServers.Remove(serverId, out var unregisteredGameServer);

            // Fire the relevant event for the game server being registered.
            if (unregisteredGameServer != null)
                OnGameServerUnregistered?.Invoke(unregisteredGameServer);
        }

        /// <summary>
        /// Kicks a game server: drops its ServerDB connection (which unregisters it) and refuses it registering again for
        /// <see cref="KickBlockDuration"/>. A session it is hosting keeps running on the game server itself.
        /// </summary>
        public void KickGameServer(RegisteredGameServer registeredGameServer)
        {
            _kicked[(registeredGameServer.Peer.Address.ToString(), registeredGameServer.Port)] = DateTime.UtcNow + KickBlockDuration;
            registeredGameServer.Peer.Disconnect();
        }

        /// <summary>
        /// Checks whether a game server registering from this address with this game port was kicked recently.
        /// </summary>
        public bool IsKicked(IPAddress address, ushort port)
        {
            var key = (address.ToString(), port);
            if (!_kicked.TryGetValue(key, out DateTime until))
                return false;
            if (DateTime.UtcNow < until)
                return true;
            _kicked.TryRemove(key, out _);
            return false;
        }

        /// <param name="lobbyBuild">If non-null, only game servers running (or not running) a lobby build (summer or halloween) are returned.</param>
        /// <param name="versionLock">If non-null, only game servers registered with this exact version lock are returned.</param>
        public IEnumerable<RegisteredGameServer> FilterGameServers(int? findMax = null, ulong? serverId = null, Guid? sessionId = null,
            HashSet<(uint InternalAddr, uint ExternalAddr)>? addresses = null, ushort? port = null,
            long? gameTypeSymbol = null, long? levelSymbol = null, Guid? channel = null, bool? locked = null, LobbyType[]? lobbyTypes = null, TeamIndex? requestedTeam = null, bool unfilledServerOnly = true, bool? lobbyBuild = null, long? versionLock = null)
        {
            // Filter through all game servers
            List<RegisteredGameServer> filteredGameServers = new List<RegisteredGameServer>();
            foreach(RegisteredGameServer gameServer in RegisteredGameServers.Values)
            {
                // If we hit any set limit for game servers found, stop.
                if (findMax != null && filteredGameServers.Count >= findMax)
                    break;

                // Filter for each field supplied, skip to the next game server if any filter doesn't match.
                if (serverId != null && gameServer.ServerId != serverId)
                    continue;
                // A server on EchoRelay's PC is given to remote players with its external address as the internal one too.
                else if (addresses != null && !addresses.Contains((gameServer.InternalAddress.ToUInt32(), gameServer.ExternalAddress.ToUInt32()))
                    && !(System.Net.IPAddress.IsLoopback(gameServer.InternalAddress) && addresses.Contains((gameServer.ExternalAddress.ToUInt32(), gameServer.ExternalAddress.ToUInt32()))))
                    continue;
                else if (port != null && gameServer.Peer.Port != port)
                    continue;
                // Lobby build (summer/halloween) servers only host lobby build clients and vice versa, and only of the same build.
                else if (lobbyBuild != null && gameServer.IsLobbyBuild != lobbyBuild)
                    continue;
                else if (versionLock != null && gameServer.VersionLock != versionLock)
                    continue;

                // If the session is started, filter on that criteria.
                if (gameServer.SessionStarted)
                {
                    if (gameTypeSymbol != null && gameTypeSymbol != gameServer.SessionGameTypeSymbol)
                        continue;
                    else if (levelSymbol != null && levelSymbol != gameServer.SessionLevelSymbol)
                        continue;
                    else if (channel != null && channel != ZeroGuid && gameServer.SessionChannel != ZeroGuid && channel != gameServer.SessionChannel) // zero guid means it is not a social lobby, etc, so we do not filter.
                        continue;
                    else if (locked != null && locked != gameServer.SessionLocked)
                        continue;
                    else if (lobbyTypes != null && !lobbyTypes.Contains(gameServer.SessionLobbyType))
                        continue;
                    else if (unfilledServerOnly && gameServer.SessionPlayerCount >= gameServer.SessionPlayerLimits.TotalPlayerLimit)
                        continue;
                    else if (unfilledServerOnly && requestedTeam != null && !gameServer.CheckTeamAvailability(requestedTeam.Value))
                        continue;
                }

                // The game server matched all filters, add it to the list
                filteredGameServers.Add(gameServer);
            }
            return filteredGameServers;
        }
        #endregion
    }
}
