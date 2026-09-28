using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Common;
using EchoRelay.Core.Server.Messages.Matching;
using EchoRelay.Core.Server.Messages.Summer;
using EchoRelay.Core.Server.Services.ServerDB;
using EchoRelay.Core.Server.Storage.Types;
using EchoRelay.Core.Utils;
using System.Collections.Concurrent;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Services.Matching
{
    public class MatchingService : Service
    {
        public MatchingService(Server server) : base(server, "MATCHING")
        {
            OnPeerDisconnected += (service, peer) => _summerPeers.TryRemove(peer, out _);
        }

        /// <summary>
        /// Peers which have sent summer build (rad15_summer) matching requests.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, bool> _summerPeers = new ConcurrentDictionary<Peer, bool>();

        /// <summary>
        /// Handles a packet being received by a peer.
        /// This is called after all events have been fired for <see cref="OnPacketReceived"/>.
        /// </summary>
        /// <param name="sender">The peer which sent the packet.</param>
        /// <param name="packet">The packet sent by the peer.</param>
        protected override async Task HandlePacket(Peer sender, Packet packet)
        {
            // Loop for each message received in the packet
            foreach (Message message in packet)
            {
                switch (message)
                {
                    case LobbyCreateSessionRequestv9 createSessionRequestv9:
                        await ProcessCreateSessionRequestv9(sender, createSessionRequestv9);
                        break;
                    case LobbyFindSessionRequestv11 findSessionRequestv11:
                        await ProcessFindSessionRequestv11(sender, findSessionRequestv11);
                        break;
                    case LobbyJoinSessionRequestv7 joinSessionRequestv7:
                        await ProcessJoinSessionRequestv7(sender, joinSessionRequestv7);
                        break;
                    case LobbyPendingSessionCancel pendingSessionCancel:
                        await ProcessPendingSessionCancel(sender, pendingSessionCancel);
                        break;
                    case LobbyMatchmakerStatusRequest matchmakerStatusRequest:
                        // Summer lobby terminals poll this and wait for a status reply. Later builds get it with their session request.
                        if (_summerPeers.ContainsKey(sender))
                            await sender.Send(new LobbyMatchmakerStatus(0));
                        break;
                    case ChristmasLobbyFindSessionRequestv6 christmasFindSessionRequest:
                        await ProcessChristmasFindSessionRequestv6(sender, christmasFindSessionRequest);
                        break;
                    case SummerLobbyFindSessionRequestv8 summerFindSessionRequest:
                        await ProcessSummerFindSessionRequestv8(sender, summerFindSessionRequest);
                        break;
                    case SummerLobbyCreateSessionRequestv7 summerCreateSessionRequest:
                        await ProcessSummerCreateSessionRequestv7(sender, summerCreateSessionRequest);
                        break;
                    case SummerLobbyJoinSessionRequestv6 summerJoinSessionRequest:
                        await ProcessSummerJoinSessionRequestv6(sender, summerJoinSessionRequest);
                        break;
                    case ChristmasLobbyCreateSessionRequestv6 christmasCreateSessionRequest:
                        await ProcessChristmasCreateSessionRequestv6(sender, christmasCreateSessionRequest);
                        break;
                    case ChristmasLobbyJoinSessionRequestv5 christmasJoinSessionRequest:
                        await ProcessChristmasJoinSessionRequestv5(sender, christmasJoinSessionRequest);
                        break;
                    case SummerLobbyPlayerSessionsRequestv3 summerPlayerSessionsRequest:
                        await ProcessSummerPlayerSessionsRequestv3(sender, summerPlayerSessionsRequest);
                        break;
                    case SummerLobbyPendingSessionCancel:
                        _summerPeers[sender] = true;
                        sender.ClearSessionData();
                        break;
                    case LobbyPingResponse pingResponse:
                        await ProcessPingResponse(sender, pingResponse);
                        break;
                    case LobbyPlayerSessionsRequestv5 playerSessionsRequestv5:
                        await ProcessPlayerSessionsRequestv5(sender, playerSessionsRequestv5);
                        break;
                }
            }
        }

        /// <summary>
        /// Processes a <see cref="LobbyCreateSessionRequestv9"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessCreateSessionRequestv9(Peer sender, LobbyCreateSessionRequestv9 request)
        {
            // Set the matching data for our user to provide context to matching operations moving forward.
            sender.SetSessionData(MatchingSession.FromCreateSessionCriteria(request.UserId, request.ChannelUUID, request.GameTypeSymbol, request.LevelSymbol, request.LobbyType, (TeamIndex)request.TeamIndex, request.SessionSettings));

            // Process the underlying request.
            await ProcessMatchingSession(sender, request.Session, request.UserId);
        }

        /// <summary>
        /// Processes a <see cref="LobbyFindSessionRequestv11"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessFindSessionRequestv11(Peer sender, LobbyFindSessionRequestv11 request)
        {
            // Set the matching data for our user to provide context to matching operations moving forward.
            sender.SetSessionData(MatchingSession.FromFindSessionCriteria(request.UserId, request.ChannelUUID, request.GameTypeSymbol, (TeamIndex)request.TeamIndex, request.SessionSettings));

            // Process the underlying request.
            await ProcessMatchingSession(sender, request.Session, request.UserId);
        }

        /// <summary>
        /// Processes a <see cref="LobbyJoinSessionRequestv7"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessJoinSessionRequestv7(Peer sender, LobbyJoinSessionRequestv7 request)
        {
            // Set the matching data for our user to provide context to matching operations moving forward.
            sender.SetSessionData(MatchingSession.FromJoinSpecificSessionCriteria(request.UserId, request.LobbyUUID, (TeamIndex)request.TeamIndex, request.SessionSettings));

            // Process the underlying request.
            await ProcessMatchingSession(sender, request.Session, request.UserId);
        }

        /// <summary>
        /// Processes a summer build find session request ("Play" from the menu, or a lobby terminal's find match).
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerFindSessionRequestv8(Peer sender, SummerLobbyFindSessionRequestv8 request)
        {
            _summerPeers[sender] = true;
            long? gameType = request.GameTypeSymbol != -1 ? request.GameTypeSymbol : request.SessionSettings.GameType;
            long? level = request.LevelSymbol != -1 ? request.LevelSymbol : null;
            MatchingSession matchingSession = level != null
                ? MatchingSession.FromCreateSessionCriteria(request.UserId, request.Channel, gameType, level, LobbyType.Public, TeamIndex.Any, request.SessionSettings)
                : MatchingSession.FromFindSessionCriteria(request.UserId, request.Channel, gameType, TeamIndex.Any, request.SessionSettings);
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, request.UserId, summer: true);
        }

        /// <summary>
        /// Processes a christmas build find session request. Same as summer's v8, without a level.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessChristmasFindSessionRequestv6(Peer sender, ChristmasLobbyFindSessionRequestv6 request)
        {
            _summerPeers[sender] = true;
            long? gameType = request.GameTypeSymbol != -1 ? request.GameTypeSymbol : request.SessionSettings.GameType;
            MatchingSession matchingSession = MatchingSession.FromFindSessionCriteria(request.UserId, request.Channel, gameType, TeamIndex.Any, request.SessionSettings);
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, request.UserId, summer: true);
        }

        /// <summary>
        /// Processes a summer build create session request (a lobby terminal's create match).
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerCreateSessionRequestv7(Peer sender, SummerLobbyCreateSessionRequestv7 request)
        {
            _summerPeers[sender] = true;

            // The user id at the end of this message isn't reliable (its layout isn't fully mapped). If it isn't a logged in
            // player, use the player this matching connection already identified as (e.g. a party leader in the lobby).
            if (!Server.LoginService.CheckUserLoggedIn(request.UserId) && sender.UserId != null)
                request.UserId = sender.UserId;
            long? gameType = request.GameTypeSymbol != -1 ? request.GameTypeSymbol : request.SessionSettings.GameType;
            long? level = request.LevelSymbol != -1 ? request.LevelSymbol : request.SessionSettings.Level;
            // A private match's game type makes the session private: the request's lobby type byte isn't reliably mapped,
            // and read as public it put "private" matches in public sessions (and other players' private ones).
            LobbyType lobbyType = SummerBuild.IsPrivateGameType(gameType) ? LobbyType.Private : request.LobbyType;
            MatchingSession matchingSession = MatchingSession.FromCreateSessionCriteria(request.UserId, request.Channel, gameType, level, lobbyType, TeamIndex.Any, request.SessionSettings);
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, request.UserId, summer: true);
        }

        /// <summary>
        /// Processes a summer build request to join a specific session, e.g. a party member following their leader.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerJoinSessionRequestv6(Peer sender, SummerLobbyJoinSessionRequestv6 request)
        {
            _summerPeers[sender] = true;
            TeamIndex team = Enum.IsDefined(typeof(TeamIndex), request.TeamIndex) ? (TeamIndex)request.TeamIndex : TeamIndex.Any;
            MatchingSession matchingSession = MatchingSession.FromJoinSpecificSessionCriteria(request.UserId, request.LobbyId, team, request.SessionSettings);
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, request.UserId, summer: true);
        }

        /// <summary>
        /// Processes a christmas build create session request (a lobby terminal's private match). The party leader creates the
        /// session; the other members follow by lobby id (SNSLobbyJoinSessionRequestv5).
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessChristmasCreateSessionRequestv6(Peer sender, ChristmasLobbyCreateSessionRequestv6 request)
        {
            _summerPeers[sender] = true;
            XPlatformId userId = request.UserId;
            if (!Server.LoginService.CheckUserLoggedIn(userId) && sender.UserId != null)
                userId = sender.UserId;
            long? gameType = request.GameTypeSymbol != -1 ? request.GameTypeSymbol : request.SessionSettings.GameType;
            long? level = request.SessionSettings.Level is long settingsLevel && settingsLevel != -1 ? settingsLevel : null;
            LobbyType lobbyType = SummerBuild.IsPrivateGameType(gameType) ? LobbyType.Private : request.LobbyType;
            MatchingSession matchingSession = MatchingSession.FromCreateSessionCriteria(userId, null, gameType, level, lobbyType, TeamIndex.Any, request.SessionSettings);
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, userId, summer: true);
        }

        /// <summary>
        /// Processes a christmas build request to join a specific session: a party member following their leader. It carries
        /// no team or settings; the game server puts party members on their party's team.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessChristmasJoinSessionRequestv5(Peer sender, ChristmasLobbyJoinSessionRequestv5 request)
        {
            _summerPeers[sender] = true;
            MatchingSession matchingSession = MatchingSession.FromJoinSpecificSessionCriteria(request.UserId, request.LobbyId, TeamIndex.Any, new SessionSettings());
            matchingSession.IsSummer = true;
            matchingSession.VersionLock = request.VersionLock;
            sender.SetSessionData(matchingSession);
            await ProcessMatchingSession(sender, null, request.UserId, summer: true);
        }

        /// <summary>
        /// Processes a summer build player sessions request, sent after the client was matched to a session.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerPlayerSessionsRequestv3(Peer sender, SummerLobbyPlayerSessionsRequestv3 request)
        {
            // Obtain the user's matching session
            MatchingSession? matchingSession = sender.GetSessionData<MatchingSession>();
            if (matchingSession == null)
                return;

            // Verify the user is logged in (summer requests carry no session token).
            XPlatformId userId = request.UserId ?? matchingSession.UserId;
            if (userId != matchingSession.UserId || !Server.LoginService.CheckUserLoggedIn(userId))
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.BadRequest, "Unauthorized");
                return;
            }
            if (matchingSession.MatchedGameServer == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.InternalError, "Player sessions requested, but no matched game server exists");
                return;
            }

            // Coordinate the player session request with the game server.
            await matchingSession.MatchedGameServer.ProcessPlayerSessionRequest(sender, userId, matchingSession.Channel ?? new Guid());
            sender.ClearSessionData();
        }

        /// <summary>
        /// Processes the underlying data derived from <see cref="LobbyFindSessionRequestv11"/>, 
        /// <see cref="LobbyFindSessionRequestv11"/>, or <see cref="LobbyJoinSessionRequestv7"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        private async Task ProcessMatchingSession(Peer sender, Guid? session, XPlatformId userId, bool summer = false)
        {
            // Verify the session details provided
            // Summer clients send no session token with matching requests, so verify the user is logged in instead.
            bool authorized = summer ? Server.LoginService.CheckUserLoggedIn(userId) : (session != null && Server.LoginService.CheckUserSessionValid(session.Value, userId));
            if (!authorized)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.BadRequest, "Unauthorized");
                return;
            }

            // Verify the account behind the request.
            // Summer clients with a shared Revive id are mapped to the account they logged in with (by address, as matching
            // requests carry no session).
            AccountResource? account = Storage.Accounts.Get(summer ? Server.LoginService.ResolveSummerAccount(userId, null, sender.Address) : userId);
            if (account == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.BadRequest, "Failed to obtain profile");
                return;
            }

            // Check if the user is banned, if so, disallow them from matching.
            if (account.Banned)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.BannedFromLobbyGroup, $"Banned until: {account.BannedUntil!.Value:MM/dd/yyyy @ hh:mm:ss tt} (UTC)");
                return;
            }

            // Set the authenticated user information.
            sender.UpdateUserAuthentication(userId, account.Profile.Server.DisplayName);

            // Obtain the user's matching session
            MatchingSession? matchingSession = sender.GetSessionData<MatchingSession>();
            if (matchingSession == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.InternalError, "Cannot process session request, no matching session exists");
                return;
            }

            // Validate the user is not requesting to be a moderator when they are not one.
            if (matchingSession.TeamIndex == TeamIndex.Moderator && !account.IsModerator)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.NotALobbyGroupMod, "User is not a moderator");
                return;
            }

            // Send the status to the user.
            // TODO: This should be a response to LobbyMatchmakerStatusRequest and should be relocated.
            //  That request is sent along with this request we are currently processing, so it is technically fine to respond here (for the client), but it's just ugly in terms of code correctness.
            await sender.Send(new LobbyMatchmakerStatus(0));

            // If we were provided a lobby/session identifier (via LobbyJoinSessionRequestv7), search for the game server directly.
            // This uses a special lookup method using the lobby id, that is faster than filtering all game servers.
            if (matchingSession.LobbyId != null)
            {
                RegisteredGameServer? requestedGameServer = Server.ServerDBService.Registry.GetGameServer(matchingSession.LobbyId.Value);
                if (requestedGameServer == null || requestedGameServer.IsLobbyBuild != summer || (summer && requestedGameServer.VersionLock != matchingSession.VersionLock))
                {
                    await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.ServerDoesNotExist, "Could not find requested lobby id");
                    return;
                }

                // Obtain our session from the game server.
                await requestedGameServer.ProcessLobbySessionRequest(sender);
                return;
            }

            // This is a create lobby, or find lobby request. We will try to find an existing server that matches the request.
            // Filter game servers, produce ping request endpoint data.
            // We limit the amount to 100, to avoid the response hitting the max packet size.
            var gameServers = Server.ServerDBService.Registry.FilterGameServers(
                findMax: 100,
                sessionId: matchingSession.LobbyId,
                gameTypeSymbol: matchingSession.GameTypeSymbol,
                levelSymbol: matchingSession.LevelSymbol,
                channel: matchingSession.Channel,
                locked: false,
                lobbyTypes: matchingSession.SearchLobbyTypes,
                requestedTeam: matchingSession.TeamIndex,
                unfilledServerOnly: true,
                lobbyBuild: summer,
                versionLock: summer ? matchingSession.VersionLock : null
            );

            // Summer clients have no ping flow: prefer a server already running a matching session, then the fullest one.
            if (summer)
            {
                RegisteredGameServer? summerGameServer = gameServers
                    .OrderBy(x => x.SessionStarted ? 0 : 1)
                    .ThenByDescending(x => (float)x.SessionPlayerCount / Math.Max(1, (int)x.SessionPlayerLimits.TotalPlayerLimit))
                    .FirstOrDefault();
                if (summerGameServer == null)
                {
                    await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.ServerFindFailed, $"No game servers for this build (version lock 0x{matchingSession.VersionLock ?? 0:X16}) are available to serve the request.");
                    return;
                }
                await summerGameServer.ProcessLobbySessionRequest(sender);
                return;
            }

            // If we only have one game server, immediately connect the peer. Otherwise, perform a ping request to determine the lowest ping server.
            if (gameServers.Count() == 1)
            {
                // Process the new session request, to get the peer the information it needs to connect to the lobby.
                await gameServers.First().ProcessLobbySessionRequest(sender);
            }
            else
            {
                // Construct our endpoint data for the ping request, from the game servers we got in our previous query.
                var pingEndpoints = new LobbyPingRequestv3.EndpointData[gameServers.Count()];
                int current = 0;
                foreach (var gameServer in gameServers)
                {
                    pingEndpoints[current++] = new LobbyPingRequestv3.EndpointData(
                        gameServer.InternalAddressFor(sender),
                        gameServer.ExternalAddress,
                        gameServer.Port
                        );
                }

                // Send a ping request to the peer.
                await sender.Send(new LobbyPingRequestv3(0, 4, 100, pingEndpoints));
            }
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="LobbyPendingSessionCancel"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessPendingSessionCancel(Peer sender, LobbyPendingSessionCancel request)
        {
            // Clear the matching session data for this peer.
            sender.ClearSessionData();
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="LobbyPingResponse"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessPingResponse(Peer sender, LobbyPingResponse request)
        {
            // Obtain the user's matching session
            MatchingSession? matchingSession = sender.GetSessionData<MatchingSession>();
            if (matchingSession == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.InternalError, "Ping response given, but no matching session exists");
                return;
            }

            // Try to select a game server
            RegisteredGameServer? selectedGameServer = null;

            // If we have no results, there are likely no game servers available to serve the request.
            // See if the user specified that they wish to force users in this scenario to join any available server.
            if (request.Results.Length == 0)
            {
                if (Server.Settings.ForceIntoAnySessionIfCreationFails)
                {
                    // Resolve the most populated available game server with open space and select it.
                    selectedGameServer = Server.ServerDBService.Registry.FilterGameServers(locked: false, requestedTeam: matchingSession.TeamIndex, unfilledServerOnly: true, lobbyTypes: new LobbyType[] {LobbyType.Unassigned, LobbyType.Public})
                        .Where(x => !x.IsLobbyBuild)
                        .MaxBy(x => (float)x.SessionPlayerCount / x.SessionPlayerLimits.TotalPlayerLimit);
                } 
                else
                {
                    await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.ServerFindFailed, "Could not receive a ping response from any game servers");
                    return;
                }
            }
            else
            {
                // Convert the ping results to a lookup
                Dictionary<(uint InternalAddress, uint ExternalAddress), uint> pingResultLookup = request.Results.ToDictionary(x => (x.InternalAddress.ToUInt32(), x.ExternalAddress.ToUInt32()), x => x.PingMilliseconds);

                // Resolve game servers matching this address with any other provided lookup criteria.
                var gameServers = Server.ServerDBService.Registry.FilterGameServers(
                    addresses: pingResultLookup.Keys.ToHashSet(),
                    lobbyBuild: false,
                    sessionId: matchingSession.LobbyId,
                    gameTypeSymbol: matchingSession.GameTypeSymbol,
                    levelSymbol: matchingSession.LevelSymbol,
                    channel: matchingSession.Channel,
                    locked: false,
                    lobbyTypes: matchingSession.SearchLobbyTypes,
                    requestedTeam: matchingSession.TeamIndex,
                    unfilledServerOnly: true
                );
                
                // All servers should either have no session started, or match the criteria we filtered for.
                // Depending on our matching strategy, we will first sort by population or ping, followed by the latter.
                // The most optimal game server will be selected.
                if (Server.Settings.FavorPopulationOverPing)
                {
                    // Select the game server which is most full.
                    selectedGameServer = gameServers.MaxBy(x => (float)x.SessionPlayerCount / x.SessionPlayerLimits.TotalPlayerLimit);
                } 
                else
                {
                    // Sort the game servers with preference of filters: session started, lowest ping, highest player count.
                    var sortedGameServers = gameServers.Select(gameServer => {
                        uint? pingMilliseconds = pingResultLookup.TryGetValue((gameServer.InternalAddressFor(sender).ToUInt32(), gameServer.ExternalAddress.ToUInt32()), out uint p) ? p : uint.MaxValue;
                        return (gameServer, pingMilliseconds);
                    }).OrderBy(x => x.gameServer.SessionStarted ? 0 : 1).ThenBy(x => x.pingMilliseconds).ThenBy(x => (float)x.gameServer.SessionPlayerCount / x.gameServer.SessionPlayerLimits.TotalPlayerLimit);

                    // Select the first game server.
                    selectedGameServer = sortedGameServers.FirstOrDefault().gameServer;
                }
            }

            // Verify that a game server candidate was found.
            if (selectedGameServer == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.ServerFindFailed, "Could not obtain registered game server to serve request.");
                return;
            }

            // Process the new session request, to get the peer the information it needs to connect to the lobby.
            await selectedGameServer.ProcessLobbySessionRequest(sender);
        }

        /// <summary>
        /// Processes a <see cref="LobbyPlayerSessionsRequestv5"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessPlayerSessionsRequestv5(Peer sender, LobbyPlayerSessionsRequestv5 request)
        {
            // Verify the session details provided
            if (!Server.LoginService.CheckUserSessionValid(request.Session, request.UserId))
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.BadRequest, "Unauthorized");
                return;
            }

            // Obtain the user's matching session
            MatchingSession? matchingSession = sender.GetSessionData<MatchingSession>();
            if (matchingSession == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.InternalError, "Player sessions requested, but no matching session exists");
                return;
            }

            if (matchingSession.MatchedGameServer == null)
            {
                await SendLobbySessionFailure(sender, LobbySessionFailureErrorCode.InternalError, "Player sessions requested, but no matched game server exists");
                return;
            }

            // Coordinate the player session request with the game server.
            await matchingSession.MatchedGameServer.ProcessPlayerSessionRequest(sender, request.UserId, matchingSession.Channel ?? new Guid());
            sender.ClearSessionData();
        }

        /// <summary>
        /// Sends all versions of the lobby session failure message to a peer, indicating that
        /// a matching operation failed. This method does nothing if the peer has no active matching session.
        /// </summary>
        /// <param name="peer">The peer to send the message to.</param>
        /// <param name="errorCode">The error code to send for the failure.</param>
        /// <param name="errorMessage">The error message to send.</param>
        /// <returns></returns>
        internal async Task SendLobbySessionFailure(Peer peer, LobbySessionFailureErrorCode errorCode, string errorMessage)
        {
            // Obtain the peer's matching session data.
            MatchingSession? matchingSession = peer.GetSessionData<MatchingSession>();
            if (matchingSession == null)
                return;

            // Clear the matching session data.
            peer.ClearSessionData();

            // Define the arguments for our failure messages.
            long gameTypeSymbol = matchingSession.GameTypeSymbol ?? -1;
            Guid channel = matchingSession.Channel ?? matchingSession.LobbyId ?? new Guid();

            // Halloween and christmas clients have no v3 (their newest is v2).
            if (matchingSession.VersionLock == SummerBuild.HalloweenVersionLock || SummerBuild.IsChristmasVersionLock(matchingSession.VersionLock))
            {
                await peer.Send(new LobbySessionFailurev2(channel, errorCode));
                return;
            }

            // Summer clients only understand v3.
            if (matchingSession.IsSummer)
            {
                await peer.Send(new LobbySessionFailurev3(gameTypeSymbol, channel, errorCode, 0));
                return;
            }

            // Send the failure messages.
            await peer.Send(new LobbySessionFailurev1(errorCode));
            await peer.Send(new LobbySessionFailurev2(channel, errorCode));
            await peer.Send(new LobbySessionFailurev3(gameTypeSymbol, channel, errorCode, 0));
            await peer.Send(new LobbySessionFailurev4(gameTypeSymbol, channel, errorCode, 0, errorMessage));
        }
    }
}
