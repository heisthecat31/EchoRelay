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
                        ProcessSummerUpdateProfileFromServer(summerProfileUpdate);
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
        private async Task<(AccountResource? account, Guid session)> AuthenticateLogin(Peer sender, XPlatformId userId, ulong? lobbyVersion, bool summer = false)
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

            // Try to obtain a user from the storage layer.
            // If the user doesn't exist, we create them.
            AccountResource? account = Storage.Accounts.Get(userId);
            if (account == null)
            {
                // Create a default username for this user.
                string displayName = userId.PlatformCode == PlatformCode.DMO ? "Anonymous [DEMO]" : $"User [{RandomNumberGenerator.GetInt32(int.MaxValue).ToString("X")}]";

                // Create an account for this user id. We use the platform identifier string as the display name.
                account = new AccountResource(userId, displayName, true, true, true);
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
            // If we have existing session data for this peer's connection, invalidate it.
            InvalidatePeerUserSession(sender);

            // Authenticate the user, creating their account if needed.
            var (account, session) = await AuthenticateLogin(sender, request.UserId, request.AccountInfo.LobbyVersion, summer: true);
            if (account == null)
                return;

            // Set the authenticated user identifier
            sender.UpdateUserAuthentication(request.UserId, account.Profile.Server.DisplayName);

            // Send login success, settings, then the profile result.
            await sender.Send(new LoginSuccess(request.UserId, session));
            LoginSettingsResource? loginSettings = Storage.LoginSettings.Get();
            if (loginSettings != null)
                await sender.Send(new LoginSettings(loginSettings));
            var (clientProfile, serverProfile) = BuildSummerProfiles(account, request.AccountInfo.LobbyVersion);
            await sender.Send(new SummerLoginProfileResult(session, request.UserId, SummerLoginProfileResult.RESULT_SUCCESS, clientProfile, serverProfile));
        }

        /// <summary>
        /// Builds the client and server profiles for a summer build client from a stored account.
        /// The summer build predates several profile fields, and keys unlocks as a flat item-name dictionary.
        /// </summary>
        /// <param name="account">The account to build profiles for.</param>
        /// <param name="lobbyVersion">The lobby version the client reported.</param>
        /// <returns>The client and server profile JSON objects.</returns>
        public (JObject client, JObject server) BuildSummerProfiles(AccountResource account, ulong? lobbyVersion)
        {
            JsonSerializer serializer = JsonSerializer.Create(StreamIO.JsonSerializerSettings);
            string xplatformId = account.AccountIdentifier.ToString();
            string displayName = account.Profile.Server.DisplayName ?? account.Profile.Client.DisplayName ?? xplatformId;
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Client profile: whatever the client last saved, with the fields the summer client requires.
            JObject client = JObject.FromObject(account.Profile.Client, serializer);
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

            // Server profile: only the fields the summer build understands (later builds nest stats/unlocks differently).
            JObject server = new JObject
            {
                ["displayname"] = displayName,
                ["xplatformid"] = xplatformId,
                ["_version"] = 1,
                ["publisher_lock"] = SummerBuild.PublisherLock,
                ["purchasedcombat"] = 1,
                ["npecompleted"] = true,
                ["lobbyversion"] = lobbyVersion ?? 0,
                ["modifytime"] = account.Profile.Server.ModifyTime ?? now,
                ["logintime"] = account.Profile.Server.LoginTime ?? now,
                ["updatetime"] = account.Profile.Server.UpdateTime ?? now,
                ["createtime"] = account.Profile.Server.CreateTime ?? now,
                ["stats"] = new JObject(),
                ["unlocks"] = new JObject(),
                ["dev"] = new JObject { ["xplatformid"] = xplatformId },
            };

            // Apply what the summer build saved to the server profile (loadout, stats, ...).
            server.Merge(GetSummerServerData(account), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });

            // Optionally unlock every cosmetic and max out the level used by level-gated items.
            if (Server.Settings.SummerUnlockAll)
            {
                JObject unlocks = new JObject();
                foreach (string item in SummerBuild.Unlockables)
                    unlocks[item] = true;
                JObject level = new JObject { ["Level"] = new JObject { ["op"] = "add", ["val"] = 50, ["cnt"] = 1 } };
                foreach (JObject profile in new[] { client, server })
                {
                    profile["unlocks"] = unlocks.DeepClone();
                    profile["unlocks_combat"] = unlocks.DeepClone();
                    profile["profile_stats"] = level.DeepClone();
                    profile["profile_stats_combat"] = level.DeepClone();
                }
            }
            return (client, server);
        }

        /// <summary>
        /// The server profile key under which summer build server profile updates are stored. They are kept apart from the
        /// final build's typed server profile fields, since the summer build's loadout items and stats differ.
        /// </summary>
        private const string SummerServerDataKey = "summer_server";

        /// <summary>
        /// Obtains the summer build server profile data stored for an account.
        /// </summary>
        /// <param name="account">The account to obtain the data for.</param>
        /// <returns>The stored data, or an empty object.</returns>
        private static JObject GetSummerServerData(AccountResource account)
        {
            return account.Profile.Server.AdditionalData.TryGetValue(SummerServerDataKey, out JToken? data) && data is JObject obj ? obj : new JObject();
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
            AccountResource? account = CheckUserSessionValid(request.Session, request.UserId) ? Storage.Accounts.Get(request.UserId) : null;
            if (account == null)
            {
                // 8 is a failure result that game servers recognize.
                await sender.Send(new SummerRefreshProfileResult(request.UserId, 8, new JObject()));
                return;
            }
            var (_, serverProfile) = BuildSummerProfiles(account, account.Profile.Server.LobbyVersion);
            await sender.Send(new SummerRefreshProfileResult(request.UserId, SummerRefreshProfileResult.RESULT_SUCCESS, serverProfile));
        }

        /// <summary>
        /// Processes a summer build server profile update (e.g. a loadout change), merging it into the stored summer server profile data.
        /// </summary>
        /// <param name="request">The request contents.</param>
        private void ProcessSummerUpdateProfileFromServer(SummerUpdateProfileFromServerv2 request)
        {
            if (!CheckUserSessionValid(request.Session, request.UserId))
                return;
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
                return;
            JObject data = GetSummerServerData(account);
            data.Merge(request.Update, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            account.Profile.Server.AdditionalData[SummerServerDataKey] = data;
            account.Profile.Server.ModifyTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Storage.Accounts.Set(account);
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

            // Top of the board, or a window around the first requested user.
            int count = request.Count > 0 ? (int)Math.Min(request.Count, 100) : 10;
            int start = 0;
            if (request.Scope == SummerLeaderboardRequest.SCOPE_USER && request.UserIds.Length > 0)
            {
                int userIndex = ranked.FindIndex(entry => entry.account.AccountIdentifier == request.UserIds[0]);
                start = Math.Max(0, Math.Min(userIndex - count / 2, ranked.Count - count));
            }

            JArray entries = new JArray();
            for (int i = start; i < ranked.Count && i < start + count; i++)
            {
                var (account, profile, score) = ranked[i];
                JArray related = new JArray();
                foreach (string statName in request.StatNames.Skip(1))
                    related.Add(new JArray(statName, FormatSummerStat(statName, GetSummerStat(profile, statName))));
                entries.Add(new JObject
                {
                    ["rank"] = i + 1,
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
                // Obtain the merged profile
                AccountResource.AccountServerProfile? mergedProfile = JsonUtils.MergeObjects(account.Profile.Server, request.UpdateInfo.Update);

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

            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
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
            account.Profile.Client = request.ClientProfile;

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