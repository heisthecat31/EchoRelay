using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Common;
using EchoRelay.Core.Server.Messages.Login;
using EchoRelay.Core.Server.Messages.Summer;
using EchoRelay.Core.Server.Storage.Types;
using EchoRelay.Core.Utils;
using Jitbit.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Web;

namespace EchoRelay.Core.Server.Services.Login
{
    /// <summary>
    /// The login service is used to sign in, obtain a session, obtain logged in/other user profiles, update logged in profile, etc.
    /// </summary>
    public class LoginService : Service
    {
        #region Fields
        /// <summary>
        /// A cache of user sessions, with expiry upon peer disconnect.
        /// </summary>
        private FastCache<Guid, XPlatformId> _userSessions;
        /// <summary>
        /// The most recent session issued to each user. Summer build matching requests carry no session token, so they
        /// are authorized by checking the user still holds a valid login session.
        /// </summary>
        private ConcurrentDictionary<XPlatformId, Guid> _latestUserSessions;

        /// <summary>
        /// Account ids that summer build clients report when Revive's Oculus platform emulation (LibRevive64 / Gammon) can't
        /// find its config: every such player reports the same user (0x4C01DB400B0C9, truncated to 0xB400B0C9 by the game).
        /// Accounts for these are keyed by the player's display name instead (see <see cref="GetSummerAccountId"/>).
        /// </summary>
        private static readonly HashSet<ulong> SharedSummerAccountIds = new HashSet<ulong> { 0xB400B0C9UL, 0x4C01DB400B0C9UL };

        /// <summary>
        /// The account each login session of a shared-id summer client belongs to.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, XPlatformId> _summerSessionAccounts = new ConcurrentDictionary<Guid, XPlatformId>();

        /// <summary>
        /// The account most recently logged in from each address by a shared-id summer client. Used for requests that
        /// carry neither a distinguishing user id nor a session (matching).
        /// </summary>
        private readonly ConcurrentDictionary<System.Net.IPAddress, XPlatformId> _summerAddressAccounts = new ConcurrentDictionary<System.Net.IPAddress, XPlatformId>();

        /// <summary>
        /// Accounts of summer players whose game reports their own (display-name derived) id truncated to 32 bits.
        /// EchoRelay.Patch replaces Revive's shared id with <see cref="GetSummerAccountId"/>'s id; if the game shortens it,
        /// requests carrying the short id still resolve to the player's account.
        /// </summary>
        private readonly ConcurrentDictionary<XPlatformId, XPlatformId> _summerShortIdAccounts = new ConcurrentDictionary<XPlatformId, XPlatformId>();

        /// <summary>
        /// Login connections of summer build clients, which are told about other players' profile changes.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, bool> _summerClientPeers = new ConcurrentDictionary<Peer, bool>();

        /// <summary>
        /// The subset of <see cref="_summerClientPeers"/> running the halloween build, which only understands the original
        /// (v1) profile messages.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, bool> _halloweenClientPeers = new ConcurrentDictionary<Peer, bool>();

        /// <summary>
        /// Login connections of christmas 2017 build (rad14) clients and game servers. They speak the summer messages but
        /// need the smaller (halloween sized) profiles.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, bool> _christmasClientPeers = new ConcurrentDictionary<Peer, bool>();

        /// <summary>The publisher lock each lobby build login connection logged in with (tells the rad14 builds apart).</summary>
        private readonly ConcurrentDictionary<Peer, string> _publisherLocks = new ConcurrentDictionary<Peer, string>();

        /// <summary>
        /// Login connections of christmas 2018 ("winter") build clients and game servers. They speak the summer messages but
        /// save loadouts with symbol hashes (like christmas 2017), so their server profile data is kept apart.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, bool> _winterClientPeers = new ConcurrentDictionary<Peer, bool>();

        /// <summary>
        /// When each login connection logged in, for the App's list of online players.
        /// </summary>
        private readonly ConcurrentDictionary<Peer, DateTime> _loginTimes = new ConcurrentDictionary<Peer, DateTime>();

        /// <summary>
        /// A player logged in right now: their display name, the game version they play and when they logged in.
        /// </summary>
        public record OnlinePlayer(string Name, string Version, DateTime Since);

        /// <summary>
        /// Everyone logged in right now (game servers that log in, like christmas ones, are listed under their name), by name.
        /// </summary>
        public List<OnlinePlayer> GetOnlinePlayers()
        {
            Peer[] peers;
            lock (PeersLock)
                peers = Peers.ToArray();
            return peers.Where(peer => peer.UserId != null)
                .Select(peer => new OnlinePlayer(peer.UserDisplayName ?? peer.UserId!.ToString(), GetVersionName(peer),
                    _loginTimes.TryGetValue(peer, out DateTime since) ? since : DateTime.UtcNow))
                .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Whether a logged in player plays a rad14 build (christmas 2017 or halloween 2017; also Lone Echo).
        /// </summary>
        public bool IsRad14Player(XPlatformId userId)
        {
            lock (PeersLock)
                return Peers.Any(peer => peer.UserId == userId && _christmasClientPeers.ContainsKey(peer));
        }

        /// <summary>
        /// The game version a login connection plays, from its login.
        /// </summary>
        private string GetVersionName(Peer peer)
        {
            if (_halloweenClientPeers.ContainsKey(peer))
                return "Halloween 2018";
            // Halloween 2017 clients are served as christmas 2017 ones; they're the only build that frames its messages
            // without the packet header.
            if (_publisherLocks.TryGetValue(peer, out string? publisherLock) && publisherLock == SummerBuild.Lobby158PublisherLock)
                return "Echo Arena 1.58";
            if (_christmasClientPeers.ContainsKey(peer))
                return peer.Headerless ? "Halloween 2017" : "Christmas 2017";
            if (_winterClientPeers.ContainsKey(peer))
                return "Christmas 2018";
            if (_summerClientPeers.ContainsKey(peer))
                return "Summer 2019";
            return "Latest";
        }
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="LoginService"/> with the provided arguments.
        /// </summary>
        /// <param name="server">The server which this service is bound to.</param>
        public LoginService(Server server) : base(server, "LOGIN")
        {
            _userSessions = new FastCache<Guid, XPlatformId>();
            _latestUserSessions = new ConcurrentDictionary<XPlatformId, Guid>();
            OnPeerDisconnected += LoginService_OnPeerDisconnected;
            Server.OnServerStopped += Server_OnServerStopped;
        }
        #endregion

        #region Functions
        /// <summary>
        /// Checks whether a user id is the shared placeholder that summer clients running through Revive report.
        /// </summary>
        public static bool IsSharedSummerUserId(XPlatformId userId)
        {
            return userId.PlatformCode == PlatformCode.OVR_ORG && SharedSummerAccountIds.Contains(userId.AccountId);
        }

        /// <summary>
        /// Derives the account id for a shared-id summer player from their display name (case-insensitive), in a range far
        /// above real Oculus ids so it can't collide with one.
        /// </summary>
        public static XPlatformId GetSummerAccountId(string identity)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("echorelay-summer-account:" + identity.Trim().ToLowerInvariant()));
            ulong accountId = (BitConverter.ToUInt64(hash, 0) & 0x3FFFFFFFFFFFFFFFUL) | 0x4000000000000000UL;
            return new XPlatformId(PlatformCode.OVR_ORG, accountId);
        }

        /// <summary>
        /// Resolves which stored account a summer request belongs to. Requests from clients reporting the shared Revive id
        /// are mapped through their login session, or failing that the address they last logged in from; any other id is
        /// its own account.
        /// </summary>
        /// <param name="userId">The user id the client reported.</param>
        /// <param name="session">The login session in the request, if any.</param>
        /// <param name="address">The address the request came from, if known.</param>
        /// <returns>The account id to use for storage.</returns>
        public XPlatformId ResolveSummerAccount(XPlatformId userId, Guid? session, System.Net.IPAddress? address)
        {
            if (_summerShortIdAccounts.TryGetValue(userId, out XPlatformId byShortId))
                return byShortId;
            if (!IsSharedSummerUserId(userId))
                return userId;
            if (session != null && _summerSessionAccounts.TryGetValue(session.Value, out XPlatformId bySession))
                return bySession;
            if (address != null && _summerAddressAccounts.TryGetValue(address, out XPlatformId byAddress))
                return byAddress;
            return userId;
        }

        /// <summary>
        /// Checks if a provided user session token is valid.
        /// </summary>
        /// <param name="session">The user session to verify.</param>
        /// <param name="userId">The account identifier of the user.</param>
        /// <returns>Returns true if the session for this user exists, false otherwise.</returns>
        public bool CheckUserSessionValid(Guid session, XPlatformId userId)
        {
            // If the session doesn't exist in cache and we can't obtain the associated user identifier,
            // it is not a valid session.
            if (!_userSessions.TryGet(session, out XPlatformId storedUserId))
                return false;

            // If the session exists, the user identifiers must match too.
            return userId == storedUserId;
        }

        /// <summary>
        /// Checks if a user currently holds a valid login session. Used for summer build clients, whose matching
        /// requests identify the user but carry no session token.
        /// </summary>
        /// <param name="userId">The account identifier of the user.</param>
        /// <returns>Returns true if the user's most recent login session is still valid, false otherwise.</returns>
        public bool CheckUserLoggedIn(XPlatformId userId)
        {
            return _latestUserSessions.TryGetValue(userId, out Guid session) && CheckUserSessionValid(session, userId);
        }

        /// <summary>
        /// Invalidates a connected peer's session token.
        /// </summary>
        /// <param name="peer">The peer to invalidate the token for.</param>
        private void InvalidatePeerUserSession(Peer peer)
        {
            // If the peer had a session token, remove it.
            Guid? session = peer.GetSessionData<Guid?>();
            if (session != null)
            {
                _userSessions.Remove(session.Value);
            }
            peer.ClearSessionData();
        }

        /// <summary>
        /// An event handler triggered when a peer disconnects from the service.
        /// </summary>
        /// <param name="service">The service the peer disconnected from.</param>
        /// <param name="peer">The peer that disconnected.</param>
        private void LoginService_OnPeerDisconnected(Service service, Peer peer)
        {
            _summerClientPeers.TryRemove(peer, out _);
            _halloweenClientPeers.TryRemove(peer, out _);
            _christmasClientPeers.TryRemove(peer, out _);
            _publisherLocks.TryRemove(peer, out _);
            _winterClientPeers.TryRemove(peer, out _);
            _loginTimes.TryRemove(peer, out _);

            // If the peer had a session token, update its expiry time.
            Guid? session = peer.GetSessionData<Guid?>();
            if (session != null && _userSessions.TryGet(session.Value, out XPlatformId userId))
            {
                _userSessions.AddOrUpdate(session.Value, userId, Server.Settings.SessionDisconnectedTimeout);
            }
        }

        /// <summary>
        /// An event handler which fires when the server is stopped.
        /// </summary>
        /// <param name="server">The server which has stopped.</param>
        private void Server_OnServerStopped(Server server)
        {
            // Clear all sessions on server stop.
            _userSessions.Clear();
            _latestUserSessions.Clear();
        }

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
                    case LoginRequest loginRequest:
                        await ProcessLoginRequest(sender, loginRequest);
                        break;
                    case SummerLoginRequest summerLoginRequest:
                        await ProcessSummerLoginRequest(sender, summerLoginRequest);
                        break;
                    case SummerRefreshProfile summerRefreshProfile:
                        await ProcessSummerRefreshProfile(sender, summerRefreshProfile);
                        break;
                    case SummerUpdateProfileFromServerv2 summerProfileUpdate:
                        await ProcessSummerUpdateProfileFromServer(summerProfileUpdate);
                        break;
                    case SummerProfileRequestv2 summerProfileRequest:
                        await ProcessSummerProfileRequest(sender, summerProfileRequest);
                        break;
                    case SummerLeaderboardRequest summerLeaderboardRequest:
                        await ProcessSummerLeaderboardRequest(sender, summerLeaderboardRequest);
                        break;
                    case LoggedInUserProfileRequest loggedInUserProfileRequest:
                        await ProcessLoggedInUserProfileRequest(sender, loggedInUserProfileRequest);
                        break;
                    case DocumentRequestv2 documentRequestv2:
                        await ProcessDocumentRequestv2(sender, documentRequestv2);
                        break;
                    case ChannelInfoRequest channelInfoRequest:
                        await ProcessChannelInfoRequest(sender, channelInfoRequest);
                        break;
                    case UpdateProfile updateProfileRequest:
                        await ProcessUpdateProfile(sender, updateProfileRequest);
                        break;
                    case OtherUserProfileRequest otherUserProfileRequest:
                        await ProcessOtherUserProfileRequest(sender, otherUserProfileRequest);
                        break;
                    case UserServerProfileUpdateRequest userServerProfileUpdateRequest:
                        await ProcessUserServerProfileUpdateRequest(sender, userServerProfileUpdateRequest);
                        break;
                    case ClientVersionRequest:
                        // Halloween 2017 clients check their version before going to the lobby: any version will do.
                        await sender.Send(new ClientVersionResponse(0));
                        break;
                }
            }
        }

        /// <summary>
        /// Processes a <see cref="LoginRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessLoginRequest(Peer sender, LoginRequest request)
        {
            // If we have existing session data for this peer's connection, invalidate it.
            // Note: The client may have multiple connections, represented as different peers.
            // This only invalidates the current connection prior to accepting a new login.
            InvalidatePeerUserSession(sender);

            // Authenticate the user, creating their account if needed.
            var (account, session) = await AuthenticateLogin(sender, request.UserId, request.AccountInfo.LobbyVersion);
            if (account == null)
                return;

            // Obtain the login settings
            LoginSettingsResource? loginSettings = Storage.LoginSettings.Get();

            // Set the authenticated user identifier
            sender.UpdateUserAuthentication(request.UserId, account.Profile.Server.DisplayName);
            _loginTimes[sender] = DateTime.UtcNow;

            // Send login success response.
            await sender.Send(new LoginSuccess(request.UserId, session));
            await sender.Send(new TcpConnectionUnrequireEvent());

            // Send login settings if we were able to obtain them.
            if (loginSettings != null)
            {
                await sender.Send(new LoginSettings(loginSettings));
            }
        }

        /// <summary>
        /// Sends a login failure to a peer.
        /// </summary>
        /// <returns>A null account and empty session, to signal the failed authentication to the caller.</returns>
        private async Task<(AccountResource? account, Guid session)> FailLogin(Peer sender, XPlatformId userId, string message, bool summer,
            byte summerResult = SummerLoginProfileResult.RESULT_AUTHENTICATION_FAILED)
        {
            // Summer clients don't know SNSLoginFailure (they'd wait on a black screen); they take a failure result code instead.
            if (summer)
                await sender.Send(new SummerLoginProfileResult(Guid.Empty, userId, summerResult, new JObject(), new JObject()));
            else
                await sender.Send(new LoginFailure(userId, HttpStatusCode.BadRequest, message));
            return (null, Guid.Empty);
        }

        /// <summary>
        /// Authenticates a login request (shared by the final and summer build login messages): validates the user,
        /// creates or loads their account, applies display name/password query parameters, checks bans, and issues a new session.
        /// </summary>
        /// <param name="sender">The peer logging in.</param>
        /// <param name="userId">The user identifier logging in.</param>
        /// <param name="lobbyVersion">The lobby version the client reported.</param>
        /// <returns>The account and new session, or a null account if authentication failed (a failure was already sent).</returns>
        private async Task<(AccountResource? account, Guid session)> AuthenticateLogin(Peer sender, XPlatformId userId, ulong? lobbyVersion, bool summer = false, string? summerFallbackIdentity = null)
        {
            // Validate the user identifier
            if(!userId.Valid())
            {
                return await FailLogin(sender, userId, "User identifier invalid", summer, SummerLoginProfileResult.RESULT_INVALID_REQUEST);
            }

            // Validate the user identifier
            // TODO: Revisit this, these are not the same values. Should AccountId be the one we actually index accounts by? Can Platform ID change with time..?
            if (false)
            {
                return await FailLogin(sender, userId, "Authentication failed", summer);
            }

            // Get the current timestamp
            ulong currentTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Summer clients running through Revive all report the same placeholder user, so their accounts are keyed by the
            // display name they log in with (or their headset serial if they give none), each with its own password lock.
            XPlatformId accountId = userId;
            if (summer && IsSharedSummerUserId(userId))
            {
                string? identity = HttpUtility.ParseQueryString(sender.RequestUri.Query).Get("displayname")?.Trim();
                if (string.IsNullOrEmpty(identity))
                    identity = string.IsNullOrWhiteSpace(summerFallbackIdentity) ? null : "hmd:" + summerFallbackIdentity;
                if (identity == null)
                    return await FailLogin(sender, userId, "Shared Oculus id without a display name", summer, SummerLoginProfileResult.RESULT_INVALID_REQUEST);
                accountId = GetSummerAccountId(identity);
            }
            else if (summer && userId.AccountId <= uint.MaxValue)
            {
                // A player's own id (from EchoRelay.Patch) that the game shortened to 32 bits: use their full account.
                string? identity = HttpUtility.ParseQueryString(sender.RequestUri.Query).Get("displayname")?.Trim();
                if (!string.IsNullOrEmpty(identity))
                {
                    XPlatformId derived = GetSummerAccountId(identity);
                    if ((derived.AccountId & uint.MaxValue) == userId.AccountId)
                    {
                        accountId = derived;
                        _summerShortIdAccounts[userId] = derived;
                    }
                }
            }

            // Try to obtain a user from the storage layer.
            // If the user doesn't exist, we create them.
            AccountResource? account = Storage.Accounts.Get(accountId);
            if (account == null)
            {
                // Create a default username for this user.
                string displayName = accountId.PlatformCode == PlatformCode.DMO ? "Anonymous [DEMO]" : $"User [{RandomNumberGenerator.GetInt32(int.MaxValue).ToString("X")}]";

                // Create an account for this user id. We use the platform identifier string as the display name.
                account = new AccountResource(accountId, displayName, true, true, true);
                account.Profile.Server.CreateTime = currentTimestamp;
            } 
            else
            {
                // Real authentication can't be performed here against Oculus API. We are given an Oculus access token and nonce from client.
                // Next, our server should be reaching out to Oculus servers with the access token and nonce to perform validation, however, this
                // requires an app secret that only the real server would have, and which we wouldn't.
                // Reference: https://developer.oculus.com/documentation/unity/ps-ownership/

                // Note: It would be an anti-goal of this project to integrate with Oculus services anyways, so this is just a note for research.
            }

            // Obtain our login service query parameters, so we can check for account display name overrides, authentication info, etc.
            NameValueCollection queryStrings = HttpUtility.ParseQueryString(sender.RequestUri.Query);
            string? displayNameOverride = queryStrings.Get("displayname");
            string? authPassword = queryStrings.Get("auth") ?? queryStrings.Get("password");

            // Authenticate to the account. If this is the first time an authentication lock/password
            // was provided, it will be set for future authentication.
            if(!account.Authenticate(authPassword))
            {
                return await FailLogin(sender, userId, $"Invalid account password/authentication lock", summer);
            }

            // Check if the user is banned
            if (account.Banned)
            {
                return await FailLogin(sender, userId, $"Banned until: {account.BannedUntil!.Value:MM/dd/yyyy @ hh:mm:ss tt} (UTC)", summer, SummerLoginProfileResult.RESULT_RESTRICTED);
            }
            else
            {
                account.BannedUntil = null;
            }

            // If we have a display name override, update the display name.
            if (displayNameOverride != null)
            {
                displayNameOverride = displayNameOverride.Trim();
                if (displayNameOverride.Length > 0)
                {
                    // Limit the maximum display name length.
                    if (displayNameOverride.Length > 20)
                        displayNameOverride = displayNameOverride.Substring(0, 20);

                    // If this is a demo account, wrap the name for distinction
                    if (account.AccountIdentifier.PlatformCode == PlatformCode.DMO)
                        displayNameOverride = $"{displayNameOverride} [DEMO]";

                    account.Profile.SetDisplayName(displayNameOverride);
                }
            }

            // Update the server profile's logintime and updatetime.
            account.Profile.Server.LobbyVersion = lobbyVersion;
            account.Profile.Server.LoginTime = currentTimestamp;
            account.Profile.Server.UpdateTime = currentTimestamp;
            account.Profile.Server.ModifyTime = currentTimestamp;

            // Store the account data
            Storage.Accounts.Set(account);

            // Create a session token that will practically not expire.
            // Set it for the peer. If they disconnect, an actual timeout will be set on the session before it expires.
            Guid session = SecureGuidGenerator.Generate();
            _userSessions.AddOrUpdate(session, userId, TimeSpan.FromDays(3000));
            sender.SetSessionData(session);
            _latestUserSessions[userId] = session;
            if (accountId != userId)
            {
                _summerSessionAccounts[session] = accountId;
                _summerAddressAccounts[sender.Address] = accountId;
            }

            return (account, session);
        }

        /// <summary>
        /// Processes a summer build (rad15_summer) login request. The summer client expects SNSLoginSuccess, SNSLoginSettings,
        /// then its profiles in a SNSLoginProfileResult, which is what moves it past the login screen.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerLoginRequest(Peer sender, SummerLoginRequest request)
        {
            _summerClientPeers[sender] = true;
            _publisherLocks[sender] = request.AccountInfo.PublisherLock ?? "";
            if (request.AccountInfo.PublisherLock == SummerBuild.HalloweenPublisherLock)
                _halloweenClientPeers[sender] = true;
            else
                _halloweenClientPeers.TryRemove(sender, out _);
            // Halloween 2017 clients are christmas 2017's code two months earlier: they're served as christmas clients.
            if (SummerBuild.IsRad14PublisherLock(request.AccountInfo.PublisherLock))
                _christmasClientPeers[sender] = true;
            else
                _christmasClientPeers.TryRemove(sender, out _);
            if (request.AccountInfo.PublisherLock == SummerBuild.WinterPublisherLock)
                _winterClientPeers[sender] = true;
            else
                _winterClientPeers.TryRemove(sender, out _);

            // If we have existing session data for this peer's connection, invalidate it.
            InvalidatePeerUserSession(sender);

            // Authenticate the user, creating their account if needed.
            var (account, session) = await AuthenticateLogin(sender, request.UserId, request.AccountInfo.LobbyVersion, summer: true,
                summerFallbackIdentity: request.AccountInfo.HMDSerialNumber);
            if (account == null)
                return;

            // Set the authenticated user identifier
            sender.UpdateUserAuthentication(request.UserId, account.Profile.Server.DisplayName);
            _loginTimes[sender] = DateTime.UtcNow;

            // Send login success, settings, then the profile result.
            await sender.Send(new LoginSuccess(request.UserId, session));
            LoginSettingsResource? loginSettings = Storage.LoginSettings.Get();
            if (loginSettings != null && GameServerBuilds.FromLogin(request.AccountInfo.PublisherLock, request.AccountInfo.LobbyVersion) == "aprilfools")
            {
                // The April Fools 2019 build: "super ultra hyper turbo mode" plays arena with combat players
                // (player_combat, with their weapons) when the client settings say arenacombat_unlocked.
                loginSettings = JsonConvert.DeserializeObject<LoginSettingsResource>(JsonConvert.SerializeObject(loginSettings))!;
                loginSettings.AdditionalData["arenacombat_unlocked"] = true;
            }
            if (loginSettings != null)
            {
                // The halloween and christmas 2018 builds only know the original name of this message.
                if (_halloweenClientPeers.ContainsKey(sender) || request.AccountInfo.PublisherLock == SummerBuild.WinterPublisherLock)
                    await sender.Send(new HalloweenLoginClientSettings(loginSettings));
                else
                    await sender.Send(new LoginSettings(loginSettings));
            }
            // The 2017 builds share christmas 2017's profile data (loadouts by symbol hash, its client profile), whichever
            // publisher lock they log in with (halloween 2017 and 1.58 send their own, through EchoRelay.Patch).
            string? profileLock = SummerBuild.IsRad14PublisherLock(request.AccountInfo.PublisherLock) ? SummerBuild.ChristmasPublisherLock : request.AccountInfo.PublisherLock;
            var (clientProfile, serverProfile) = BuildSummerProfiles(account, request.AccountInfo.LobbyVersion, request.UserId, profileLock);
            await sender.Send(new SummerLoginProfileResult(session, request.UserId, SummerLoginProfileResult.RESULT_SUCCESS, clientProfile, serverProfile));

            // If their version has no game server, start some (ServerSettings.AutoStartGameServers), without holding up the login.
            // A login without a publisher_lock is taken for christmas 2017 (the default) only if it came through pnsovr.dll,
            // as christmas 2017 clients' do (their account data always has hmdserialnumber). 1.58 and halloween 2017 game
            // servers log in through the RAD provider without either, and used to start christmas 2017 game servers.
            if ((request.AccountInfo.PublisherLockSent || request.AccountInfo.LobbyVersion != null || request.AccountInfo.HMDSerialNumber != null)
                && GameServerBuilds.FromLogin(request.AccountInfo.PublisherLock, request.AccountInfo.LobbyVersion) is string build)
            {
                string displayName = account.Profile.Server.DisplayName ?? request.UserId.ToString();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Server.GameServerHosts.StartForLogin(Server, build, displayName);
                    }
                    catch
                    {
                        // Starting game servers is a convenience; a failure must not affect the login.
                    }
                });
            }
        }

        /// <summary>
        /// Builds the client and server profiles for a summer build client from a stored account.
        /// The summer build predates several profile fields, and keys unlocks as a flat item-name dictionary.
        /// </summary>
        /// <param name="account">The account to build profiles for.</param>
        /// <param name="lobbyVersion">The lobby version the client reported.</param>
        /// <returns>The client and server profile JSON objects.</returns>
        /// <param name="reportedUserId">The user id the client itself reports, if it differs from the account's (shared Revive ids).</param>
        public (JObject client, JObject server) BuildSummerProfiles(AccountResource account, ulong? lobbyVersion, XPlatformId? reportedUserId = null, string? publisherLock = null)
        {
            JsonSerializer serializer = JsonSerializer.Create(StreamIO.JsonSerializerSettings);
            // The client checks profiles against its own user id, so they carry the id it reported.
            string xplatformId = (reportedUserId ?? account.AccountIdentifier).ToString();
            string displayName = account.Profile.Server.DisplayName ?? account.Profile.Client.DisplayName ?? xplatformId;
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Client profile: whatever the client last saved, with the fields the summer client requires. The christmas build
            // has its own (see ProcessUpdateProfile); the other builds get combat choices a christmas save hashed as names again.
            JObject client = publisherLock == SummerBuild.ChristmasPublisherLock
                && account.Profile.Server.AdditionalData.TryGetValue(ChristmasClientDataKey, out JToken? christmasClient) && christmasClient is JObject christmasClientObj
                ? (JObject)christmasClientObj.DeepClone()
                : JObject.FromObject(account.Profile.Client, serializer);
            if (publisherLock != SummerBuild.ChristmasPublisherLock)
                SummerBuild.RepairCombatChoices(client);
            client["displayname"] = displayName;
            client["xplatformid"] = xplatformId;
            client["modifytime"] = now;
            JObject npe = client["npe"] as JObject ?? new JObject();
            foreach (string stage in new[] { "lobby", "firstmatch", "movement", "arenabasics" })
                npe[stage] = new JObject { ["completed"] = true };
            client["npe"] = npe;
            // npecompleted skips the new player tutorial, sending "Play" straight to the social lobby.
            client["npecompleted"] = true;
            if (client["legal"] is not JObject legal || !legal.HasValues)
                client["legal"] = new JObject { ["points_policy_version"] = 1, ["eula_version"] = 1, ["game_admin_version"] = 1, ["splash_screen_version"] = 1 };
            // Unlocks and stats belong to the (read-only) server profile; drop copies older versions put in the client profile.
            foreach (string key in new[] { "unlocks", "unlocks_combat", "profile_stats", "profile_stats_combat", "newunlocks" })
                client.Remove(key);

            // Server profile: the summer build's own default server profile (its loadout/unlock formats differ from the final
            // build's), plus identity fields, then what the summer build saved for this account (e.g. its loadout).
            bool halloween = SummerBuild.HasSmallMessageLimit(publisherLock);
            JObject server = halloween ? SummerBuild.HalloweenDefaultServerProfile : SummerBuild.DefaultServerProfile;
            server["displayname"] = displayName;
            server["xplatformid"] = xplatformId;
            server["publisher_lock"] = publisherLock ?? SummerBuild.PublisherLock;
            server["purchasedcombat"] = 1;
            server["npecompleted"] = true;
            server["lobbyversion"] = lobbyVersion ?? 0;
            server["modifytime"] = account.Profile.Server.ModifyTime ?? now;
            server["logintime"] = account.Profile.Server.LoginTime ?? now;
            server["updatetime"] = account.Profile.Server.UpdateTime ?? now;
            server["createtime"] = account.Profile.Server.CreateTime ?? now;
            server["dev"] = new JObject { ["xplatformid"] = xplatformId };
            server.Merge(GetSummerServerData(account, publisherLock), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            // Stats are shared by the lobby builds (they replace any a build's own data still has).
            foreach (JProperty group in GetSharedStats(account).Properties())
                server[group.Name] = group.Value.DeepClone();

            // Optionally unlock every cosmetic (the summer build lists unlocked item names in arrays, separately for arena
            // and combat) and max out the level used by level-gated items.
            if (Server.Settings.SummerUnlockAll)
            {
                server["unlocks"] = new JArray(SummerBuild.Unlockables);
                server["unlocks_combat"] = new JArray(SummerBuild.Unlockables);
                // Only the level is set: the rest of the stats are the player's own. Game servers send each player's stats
                // after a match as the profile's stats combined with that match, so the profile must carry them, or every
                // match would start from zero (and the tablet would show none).
                foreach (string key in new[] { "profile_stats", "profile_stats_combat" })
                {
                    JObject stats = server[key] as JObject ?? new JObject();
                    stats["Level"] = new JObject { ["op"] = "add", ["val"] = 50, ["cnt"] = 1 };
                    server[key] = stats;
                }
            }

            // The halloween build has no combat unlock list, and a second copy of the unlocks would push the profile past
            // the halloween client's message size limit (it disconnects on a larger message).
            if (halloween)
                server.Remove("unlocks_combat");
            // The 2017 builds (rad14) reject any message over 16 KB and drop their login connection ("exceeding the max message
            // size"), and the shared stats grew their profile past it. They have no combat, so no combat stats, and stats at
            // zero are left out: a game server treats a missing stat as zero when it adds a match to the profile.
            if (SummerBuild.IsRad14PublisherLock(publisherLock))
            {
                server.Remove("profile_stats_combat");
                if (server["profile_stats"] is JObject arenaStats)
                    foreach (JProperty stat in arenaStats.Properties().ToList())
                        if (stat.Name != "Level" && stat.Value is JObject value && value["val"] is JValue val
                            && (val.Type == JTokenType.Integer || val.Type == JTokenType.Float) && val.Value<double>() == 0)
                            stat.Remove();
            }
            return (client, server);
        }

        /// <summary>
        /// The server profile key under which summer build server profile updates are stored. They are kept apart from the
        /// final build's typed server profile fields, since the summer build's loadout items and stats differ.
        /// </summary>
        private const string SummerServerDataKey = "summer_server";

        /// <summary>
        /// The christmas build's server profile updates are kept apart: it saves loadouts with symbol hashes (e.g. "name":
        /// 3445406071846753926 for "general") where the summer and halloween builds use names.
        /// </summary>
        private const string ChristmasServerDataKey = "christmas_server";

        /// <summary>
        /// The christmas build's client profile saves are kept apart too: it hashes the combat choices it doesn't use
        /// ("weapon": "rocket" becomes "4743087768721687050"), which left summer players with no weapons.
        /// </summary>
        private const string ChristmasClientDataKey = "christmas_client";

        /// <summary>
        /// The christmas 2018 ("winter") build's server profile updates are kept apart too: it also saves loadouts with symbol
        /// hashes, and each build's item hashes only mean something to that build.
        /// </summary>
        private const string WinterServerDataKey = "winter_server";

        /// <summary>
        /// The stats every lobby build shares (summer, halloween and christmas 2018, the 2017 builds), kept apart from each
        /// build's own data (whose loadouts only that build can read). The latest build's stats are its own.
        /// </summary>
        private const string SharedStatsDataKey = "lobby_stats";

        /// <summary>
        /// Whether a server profile key holds stats: profile_stats (arena) or profile_stats_combat.
        /// </summary>
        private static bool IsStatsKey(string key) => key.StartsWith("profile_stats", StringComparison.Ordinal);

        /// <summary>
        /// The stats the lobby builds share for an account. Accounts from before they were shared get the stats of the build
        /// they played the most games of (stat groups can't simply be added up: each stat has its own operation).
        /// </summary>
        public static JObject GetSharedStats(AccountResource account)
        {
            var stored = account.Profile.Server.AdditionalData;
            if (stored.TryGetValue(SharedStatsDataKey, out JToken? sharedStats) && sharedStats is JObject sharedStatsObj)
                return sharedStatsObj;
            JObject stats = new JObject();
            foreach (string dataKey in new[] { SummerServerDataKey, ChristmasServerDataKey, WinterServerDataKey })
            {
                if (!stored.TryGetValue(dataKey, out JToken? data) || data is not JObject dataObj)
                    continue;
                foreach (JProperty group in dataObj.Properties().Where(property => IsStatsKey(property.Name) && property.Value is JObject))
                {
                    if (stats[group.Name] is not JObject current || GamesPlayed((JObject)group.Value) > GamesPlayed(current))
                        stats[group.Name] = group.Value.DeepClone();
                }
            }
            return stats;
        }

        /// <summary>
        /// The games a stat group counts (ArenaGamesPlayed or CombatGamesPlayed), for picking an account's stats to keep.
        /// </summary>
        private static double GamesPlayed(JObject group)
        {
            foreach (string statName in new[] { "ArenaGamesPlayed", "CombatGamesPlayed" })
                if (group[statName] is JObject stat && stat["val"] is JValue value && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
                    return value.Value<double>();
            return 0;
        }

        /// <summary>
        /// Stores a lobby build game server's update to a player's server profile: stats go to the stats the lobby builds
        /// share, the rest (e.g. the loadout) to the build's own data.
        /// </summary>
        /// <param name="account">The player's account.</param>
        /// <param name="publisherLock">The build's publisher lock.</param>
        /// <param name="update">The update the game server sent.</param>
        public static void ApplySummerServerUpdate(AccountResource account, string? publisherLock, JObject update)
        {
            JObject data = GetSummerServerData(account, publisherLock);
            data.Merge(update, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            JObject sharedStats = GetSharedStats(account);
            foreach (JProperty group in data.Properties().Where(property => IsStatsKey(property.Name)).ToList())
            {
                if (update[group.Name] is JObject updated)
                {
                    JObject merged = sharedStats[group.Name] as JObject ?? new JObject();
                    merged.Merge(updated, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                    sharedStats[group.Name] = merged;
                }
                data.Remove(group.Name);
            }
            account.Profile.Server.AdditionalData[SharedStatsDataKey] = sharedStats;
            account.Profile.Server.AdditionalData[GetServerDataKey(publisherLock)] = data;
        }

        private static string GetServerDataKey(string? publisherLock) => publisherLock switch
        {
            SummerBuild.ChristmasPublisherLock => ChristmasServerDataKey,
            SummerBuild.WinterPublisherLock => WinterServerDataKey,
            _ => SummerServerDataKey,
        };

        /// <summary>
        /// Obtains the lobby build server profile data stored for an account.
        /// </summary>
        /// <param name="account">The account to obtain the data for.</param>
        /// <param name="publisherLock">The build's publisher lock (christmas has its own data).</param>
        /// <returns>The stored data, or an empty object.</returns>
        private static JObject GetSummerServerData(AccountResource account, string? publisherLock = null)
        {
            var stored = account.Profile.Server.AdditionalData;
            JObject shared = stored.TryGetValue(SummerServerDataKey, out JToken? sharedData) && sharedData is JObject sharedObj ? sharedObj : new JObject();
            if (publisherLock == SummerBuild.ChristmasPublisherLock || publisherLock == SummerBuild.WinterPublisherLock)
            {
                if (stored.TryGetValue(GetServerDataKey(publisherLock), out JToken? ownData) && ownData is JObject ownObj)
                    return ownObj;
                // Saves made before the build had its own data went to the shared data; take them from there.
                return HasSymbolLoadout(shared) ? shared : new JObject();
            }
            // The summer and halloween builds can't read a christmas (2017 or 2018) loadout.
            if (HasSymbolLoadout(shared))
            {
                shared = (JObject)shared.DeepClone();
                shared.Remove("loadout");
            }
            return shared;
        }

        /// <summary>
        /// Whether server profile data holds a christmas build loadout (instance names saved as symbol hashes, not strings).
        /// </summary>
        private static bool HasSymbolLoadout(JObject data)
        {
            return data["loadout"]?["instances"] is JArray instances && instances.FirstOrDefault()?["name"]?.Type == JTokenType.Integer;
        }

        /// <summary>
        /// Processes a summer build profile refresh. Clients request their own, game servers request each joining player's,
        /// and both expect the player's server profile (which carries their loadout).
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerRefreshProfile(Peer sender, SummerRefreshProfile request)
        {
            // Game servers pass the player's login session, so validate it against the player being refreshed.
            AccountResource? account = CheckUserSessionValid(request.Session, request.UserId) ? Storage.Accounts.Get(ResolveSummerAccount(request.UserId, request.Session, null)) : null;
            if (account == null)
            {
                // 8 is a failure result that game servers recognize.
                await sender.Send(new SummerRefreshProfileResult(request.UserId, 8, new JObject()));
                return;
            }
            // The game server asking runs the same build as the player's client.
            string? publisherLock = GetSessionPublisherLock(request.Session);
            var (_, serverProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, publisherLock);
            await sender.Send(new SummerRefreshProfileResult(request.UserId, SummerRefreshProfileResult.RESULT_SUCCESS, serverProfile));
        }

        /// <summary>
        /// Determines whether a login session belongs to a halloween build client.
        /// </summary>
        /// <param name="session">The login session.</param>
        /// <returns>True if a halloween client holds the session.</returns>
        private bool IsHalloweenSession(Guid session)
        {
            return _halloweenClientPeers.Keys.Any(peer => peer.GetSessionData<Guid?>() == session);
        }

        /// <summary>
        /// Determines the publisher lock of the build holding a login session, for the builds that need their own profile
        /// (halloween, christmas). Null for summer.
        /// </summary>
        private string? GetSessionPublisherLock(Guid session)
        {
            if (IsHalloweenSession(session))
                return SummerBuild.HalloweenPublisherLock;
            if (_christmasClientPeers.Keys.Any(peer => peer.GetSessionData<Guid?>() == session))
                return SummerBuild.ChristmasPublisherLock;
            if (_winterClientPeers.Keys.Any(peer => peer.GetSessionData<Guid?>() == session))
                return SummerBuild.WinterPublisherLock;
            return null;
        }

        /// <summary>
        /// The publisher lock to build a peer's profiles with (see <see cref="GetSessionPublisherLock"/>).
        /// </summary>
        private string? GetPeerPublisherLock(Peer peer)
        {
            if (_halloweenClientPeers.ContainsKey(peer))
                return SummerBuild.HalloweenPublisherLock;
            if (_christmasClientPeers.ContainsKey(peer))
                return SummerBuild.ChristmasPublisherLock;
            if (_winterClientPeers.ContainsKey(peer))
                return SummerBuild.WinterPublisherLock;
            return null;
        }

        /// <summary>
        /// Processes a summer build server profile update (e.g. a loadout change), merging it into the stored summer server profile data.
        /// </summary>
        /// <param name="request">The request contents.</param>
        private async Task ProcessSummerUpdateProfileFromServer(SummerUpdateProfileFromServerv2 request)
        {
            if (!CheckUserSessionValid(request.Session, request.UserId))
                return;
            AccountResource? account = Storage.Accounts.Get(ResolveSummerAccount(request.UserId, request.Session, null));
            if (account == null)
                return;
            // The game server runs the same build as the player it saves for.
            string? publisherLock = GetSessionPublisherLock(request.Session);
            ApplySummerServerUpdate(account, publisherLock, request.Update);
            account.Profile.Server.ModifyTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Storage.Accounts.Set(account);

            // Push the updated server profile to the player's own client (the login connection holding the session the game
            // server reported). Otherwise the client keeps the server profile it received at login, and presents the old
            // loadout until it logs in again.
            var (_, serverProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId);
            Peer[] clientPeers;
            lock (PeersLock)
                clientPeers = Peers.Where(peer => peer.GetSessionData<Guid?>() == request.Session).ToArray();
            foreach (Peer clientPeer in clientPeers)
            {
                string? ownLock = GetPeerPublisherLock(clientPeer);
                JObject ownProfile = ownLock != null
                    ? BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, ownLock).server
                    : serverProfile;
                await clientPeer.Send(new SummerRefreshProfileFromServer(request.UserId, ownProfile));
            }

            // Push it to everyone else's client too, so players already in a lobby with them re-dress their avatar.
            // Clients store and apply an SNSProfileResponsev2 whether or not they requested it.
            foreach (Peer otherPeer in _summerClientPeers.Keys.Except(clientPeers))
            {
                try
                {
                    if (_halloweenClientPeers.ContainsKey(otherPeer))
                        await otherPeer.Send(new HalloweenProfileResponse(request.UserId,
                            BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, SummerBuild.HalloweenPublisherLock).server));
                    else if (_christmasClientPeers.ContainsKey(otherPeer))
                        await otherPeer.Send(new ChristmasProfileResponse(0, request.UserId,
                            BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, SummerBuild.ChristmasPublisherLock).server));
                    else if (_winterClientPeers.ContainsKey(otherPeer))
                        continue; // The winter build has no profile response message (it gets others' loadouts from the game server).
                    else
                        await otherPeer.Send(new SummerProfileResponsev2(request.UserId, serverProfile));
                }
                catch
                {
                    // A client disconnecting mid-send shouldn't stop the others getting the update.
                }
            }
        }

        /// <summary>
        /// Processes a summer build request for another player's server profile, sent by clients for each player they see
        /// join. They dress that player's avatar (loadout) from the reply.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerProfileRequest(Peer sender, SummerProfileRequestv2 request)
        {
            AccountResource? account = Storage.Accounts.Get(ResolveSummerAccount(request.UserId, null, null));
            if (account == null)
                return;
            // Christmas build clients ask with SNSProfileRequest too, but their response echoes the request id.
            if (_christmasClientPeers.ContainsKey(sender))
            {
                var (_, christmasProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, SummerBuild.ChristmasPublisherLock);
                await sender.Send(new ChristmasProfileResponse(request.Unk0, request.UserId, christmasProfile));
                return;
            }
            // Halloween build clients ask with the original SNSProfileRequest, and only understand the matching response.
            if (request is HalloweenProfileRequest || _halloweenClientPeers.ContainsKey(sender))
            {
                var (_, halloweenProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, SummerBuild.HalloweenPublisherLock);
                await sender.Send(new HalloweenProfileResponse(request.UserId, halloweenProfile));
                return;
            }
            var (_, serverProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion, request.UserId, GetPeerPublisherLock(sender));
            await sender.Send(new SummerProfileResponsev2(request.UserId, serverProfile));
        }

        /// <summary>
        /// Obtains a stat value from a summer server profile, for leaderboards. Stats are stored as {"op", "val", "cnt"} objects
        /// under a stat group (e.g. profile_stats, or stats.&lt;group&gt;), so the first matching stat name is used.
        /// </summary>
        /// <param name="profile">The summer server profile to search.</param>
        /// <param name="statName">The name of the stat.</param>
        /// <returns>The stat value, or zero if the profile has no such stat.</returns>
        private static double GetSummerStat(JObject profile, string statName)
        {
            foreach (JToken token in profile.Descendants())
            {
                if (token is JProperty property && property.Name == statName)
                {
                    JToken value = property.Value is JObject stat ? stat["val"] ?? JValue.CreateNull() : property.Value;
                    if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
                        return value.Value<double>();
                }
            }
            return 0;
        }

        /// <summary>
        /// Formats a stat value for a leaderboard, which displays the text as is.
        /// </summary>
        private static string FormatSummerStat(string statName, double value)
        {
            if (statName.EndsWith("Percentage", StringComparison.OrdinalIgnoreCase) || statName.EndsWith("PerGame", StringComparison.OrdinalIgnoreCase))
                return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Processes a summer build leaderboard request (the lobby stat boards), ranking all accounts by the board's stat.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        /// <returns>None</returns>
        private async Task ProcessSummerLeaderboardRequest(Peer sender, SummerLeaderboardRequest request)
        {
            if (request.StatNames.Length == 0)
            {
                await sender.Send(new SummerLeaderboardResponse(request.Tag, new JArray()));
                return;
            }

            // Rank every account by the board's stat.
            string rankedStat = request.StatNames[0];
            var ranked = Storage.Accounts.Keys()
                .Select(id => Storage.Accounts.Get(id))
                .Where(account => account != null)
                .Select(account =>
                {
                    var (_, profile) = BuildSummerProfiles(account!, account!.Profile.Server.LobbyVersion);
                    return (account: account!, profile, score: GetSummerStat(profile, rankedStat));
                })
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.account.Profile.Server.DisplayName)
                .ToList();

            // A user board request only wants the user's place: every build reads just "[0]|rank" from that reply. The
            // 2017 builds before christmas (halloween 2017, 1.58) tell the two replies apart by that alone: a reply whose
            // first entry has an integer "rank" is a user's place, and its entries are ignored. So board entries carry no
            // rank (no build reads one; a board's rows are numbered in order).
            if (request.Scope == SummerLeaderboardRequest.SCOPE_USER)
            {
                XPlatformId? user = request.UserIds.Length > 0
                    ? ResolveSummerAccount(request.UserIds[0], sender.GetSessionData<Guid?>(), sender.Address)
                    : null;
                int userIndex = user == null ? -1 : ranked.FindIndex(entry => entry.account.AccountIdentifier == user);
                await sender.Send(new SummerLeaderboardResponse(request.Tag,
                    new JArray(new JObject { ["rank"] = userIndex >= 0 ? userIndex + 1 : ranked.Count + 1 })));
                return;
            }

            // The top of the board.
            int count = request.Count > 0 ? (int)Math.Min(request.Count, 100) : 10;
            JArray entries = new JArray();
            for (int i = 0; i < ranked.Count && i < count; i++)
            {
                var (account, profile, score) = ranked[i];
                JArray related = new JArray();
                foreach (string statName in request.StatNames.Skip(1))
                    related.Add(new JArray(statName, FormatSummerStat(statName, GetSummerStat(profile, statName))));
                entries.Add(new JObject
                {
                    ["displayname"] = account.Profile.Server.DisplayName ?? account.AccountIdentifier.ToString(),
                    ["score"] = new JArray(rankedStat, FormatSummerStat(rankedStat, score)),
                    ["related"] = related,
                });
            }
            await sender.Send(new SummerLeaderboardResponse(request.Tag, entries));
        }

        /// <summary>
        /// Processes a <see cref="LoggedInUserProfileRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessLoggedInUserProfileRequest(Peer sender, LoggedInUserProfileRequest request)
        {
            // Verify the session details provided
            if (!CheckUserSessionValid(request.Session, request.UserId))
            {
                await sender.Send(new LoggedInUserProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Authentication failed"));
                return;
            }

            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                await sender.Send(new LoggedInUserProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Failed to obtain profile"));
                return;
            }

            // Send the account profile to the user.
            await sender.Send(new LoggedInUserProfileSuccess(request.UserId, account.Profile));
        }

        /// <summary>
        /// Processes a <see cref="OtherUserProfileRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessOtherUserProfileRequest(Peer sender, OtherUserProfileRequest request)
        {
            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                await sender.Send(new OtherUserProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Failed to obtain profile"));
                return;
            }

            // Send the account profile to the user.
            await sender.Send(new OtherUserProfileSuccess(request.UserId, account.Profile.Server));
        }

        /// <summary>
        /// Processes a <see cref="UserServerProfileUpdateRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessUserServerProfileUpdateRequest(Peer sender, UserServerProfileUpdateRequest request)
        {
            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                // TODO: Failure message!
                return;
            }

            // Merge the update information with the user.
            if (request.UpdateInfo.Update != null)
            {
                // Obtain the merged profile. Stats are combined with the stored totals (by their op) rather than replaced.
                JObject update = LiveStats.CombineStats(JObject.FromObject(account.Profile.Server), request.UpdateInfo.Update);
                AccountResource.AccountServerProfile? mergedProfile = JsonUtils.MergeObjects(account.Profile.Server, update);

                // Verify we have an account and the identifier didn't change (avoids overwriting another profile in storage, as it is the storage key).
                if (mergedProfile == null || mergedProfile.XPlatformId != request.UserId.ToString())
                {
                    // TODO: Send UpdateProfileFailure(?)
                    return;
                }

                // Update the server profile in the account and set it in storage.
                account.Profile.Server = mergedProfile;
                Storage.Accounts.Set(account);
            }

            // Send the account profile to the user.
            await sender.Send(new UserServerUpdateProfileSuccess(request.UserId));
        }

        /// <summary>
        /// Processes a <see cref="UpdateProfile"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessUpdateProfile(Peer sender, UpdateProfile request)
        {
            // Verify the session details provided
            if (!CheckUserSessionValid(request.Session, request.UserId))
            {
                // TODO: Send UpdateProfileFailure(?)
                return;
            }

            // Obtain the account associated with the request (summer clients with a shared Revive id map through their session).
            AccountResource? account = Storage.Accounts.Get(ResolveSummerAccount(request.UserId, request.Session, null));
            if (account == null)
            {
                // TODO: Send UpdateProfileFailure(?)
                return;
            }

            // Verify the account identifier did not change (avoids overwriting another profile in storage, as it is the storage key).
            if (request.ClientProfile.XPlatformId != request.UserId.ToString())
            {
                // TODO: Send UpdateProfileFailure(?)
                return;
            }

            // Get the current timestamp
            ulong currentTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // TODO: For now, we just trust all the update data and merge it in. We should scrutinize it more.
            // A christmas client's save is kept apart, so it can't overwrite the client profile the other builds use.
            if (_christmasClientPeers.ContainsKey(sender))
            {
                JsonSerializer serializer = JsonSerializer.Create(StreamIO.JsonSerializerSettings);
                account.Profile.Server.AdditionalData[ChristmasClientDataKey] = JObject.FromObject(request.ClientProfile, serializer);
            }
            else
            {
                account.Profile.Client = request.ClientProfile;
                // Repair combat choices an earlier christmas save hashed, so they're stored as names again.
                JsonSerializer serializer = JsonSerializer.Create(StreamIO.JsonSerializerSettings);
                JObject saved = JObject.FromObject(account.Profile.Client, serializer);
                if (SummerBuild.RepairCombatChoices(saved))
                {
                    account.Profile.Client.Weapon = saved.Value<string>("weapon");
                    account.Profile.Client.Grenade = saved.Value<string>("grenade");
                    account.Profile.Client.Ability = saved.Value<string>("ability");
                }
            }

            // Update the account.
            account.Profile.Server.UpdateTime = currentTimestamp;
            account.Profile.Server.ModifyTime = currentTimestamp;
            Storage.Accounts.Set(account);

            // Send the account profile to the user.
            await sender.Send(new UpdateProfileSuccess(request.UserId));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="ChannelInfoRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessChannelInfoRequest(Peer sender, ChannelInfoRequest request)
        {
            // Try to obtain our channel info
            ChannelInfoResource? channelInfo = Storage.ChannelInfo.Get();
            if (channelInfo != null)
                await sender.Send(new ChannelInfoResponse(channelInfo));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="DocumentRequestv2"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessDocumentRequestv2(Peer sender, DocumentRequestv2 request)
        {
            // Obtain the symbols for the document name and language.
            long? nameSymbol = SymbolCache.GetSymbol(request.Name);
            long? languageSymbol = SymbolCache.GetSymbol(request.Language);

            // If we couldn't resolve the name or language, return a failure.
            if (nameSymbol == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not resolve symbol for document name"));
                return;
            }
            if (languageSymbol == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not resolve symbol for document language"));
                return;
            }

            // Fetch the document from storage
            DocumentResource? resource = Storage.Documents.Get((request.Name, request.Language));
            if (resource == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not find document"));
                return;
            }

            // Send the document in response.
            await sender.Send(new DocumentSuccess(nameSymbol.Value, resource));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }
        #endregion
    }
}