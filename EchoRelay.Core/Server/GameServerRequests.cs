using EchoRelay.Core.Game;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// A player's request (from the installer) for a game server of a lobby build, made to {api}/servers/request.
    /// </summary>
    /// <param name="Build">The build: "summer", "halloween", "winter" (christmas 2018), "christmas" (christmas 2017) or
    /// "halloween2017".</param>
    /// <param name="Requester">The account making the request (already authenticated with its password).</param>
    /// <param name="DisplayName">The requester's display name.</param>
    /// <param name="Address">Where the request came from.</param>
    public record GameServerRequest(string Build, XPlatformId Requester, string DisplayName, System.Net.IPAddress? Address);

    /// <summary>
    /// The answer to a <see cref="GameServerRequest"/>, shown to the player.
    /// </summary>
    /// <param name="Accepted">Whether a game server is being started.</param>
    /// <param name="Message">What happened, for the player.</param>
    public record GameServerRequestResult(bool Accepted, string Message);

    /// <summary>
    /// The lobby builds a game server can be requested for.
    /// </summary>
    public static class GameServerBuilds
    {
        /// <summary>
        /// Request build ids and their names (as <see cref="SummerBuild.GetBuildName"/> names an executable).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
        {
            ["summer"] = "Summer 2019",
            ["halloween"] = "Halloween 2018",
            ["winter"] = "Christmas 2018",
            ["aprilfools"] = "April Fools 2019",
            ["christmas"] = "Christmas 2017",
            ["halloween2017"] = "Halloween 2017",
            ["lobby158"] = "Echo Arena 1.58",
        };

        /// <summary>
        /// Who game servers started automatically (not on a player's request) are requested by, see
        /// <see cref="GameServerHosts.StartForLogin"/>. Hosts don't count them against any player.
        /// </summary>
        public static readonly XPlatformId AutomaticRequester = new XPlatformId(PlatformCode.BOT, 0);

        /// <summary>
        /// The build a lobby build client logs in as, from its publisher lock (null for one without requestable servers).
        /// </summary>
        public static string? FromPublisherLock(string? publisherLock) => publisherLock switch
        {
            SummerBuild.PublisherLock => "summer",
            SummerBuild.HalloweenPublisherLock => "halloween",
            SummerBuild.WinterPublisherLock => "winter",
            SummerBuild.ChristmasPublisherLock => "christmas",
            SummerBuild.Halloween2017PublisherLock => "halloween2017",
            SummerBuild.Lobby158PublisherLock => "lobby158",
            _ => null,
        };

        /// <summary>
        /// The build a lobby build client logs in as (null for one without requestable servers). Its lobby version decides, as
        /// publisher locks can be shared (April Fools 2019 clients log in with christmas 2018's); the 2017 builds send none, and
        /// are told apart by their publisher locks.
        /// </summary>
        public static string? FromLogin(string? publisherLock, ulong? lobbyVersion) => lobbyVersion switch
        {
            SummerBuild.SummerLobbyVersion => "summer",
            SummerBuild.HalloweenLobbyVersion => "halloween",
            SummerBuild.WinterLobbyVersion => "winter",
            SummerBuild.AprilFoolsLobbyVersion => "aprilfools",
            _ => FromPublisherLock(publisherLock),
        };

        /// <summary>
        /// The game version a game server registered with this version lock runs, e.g. "Christmas 2017" (for display).
        /// </summary>
        public static string VersionName(long versionLock)
        {
            foreach (var build in Names)
                if (ServesBuild(build.Key, versionLock))
                    return build.Value;
            if (versionLock == SummerBuild.LoneEchoVersionLock)
                return "Lone Echo";
            return SummerBuild.IsLobbyVersionLock(versionLock) ? $"Unknown (0x{versionLock:X16})" : "Latest";
        }

        /// <summary>
        /// Whether a game server registered with this version lock serves a build's players.
        /// </summary>
        public static bool ServesBuild(string build, long versionLock) => build switch
        {
            "summer" => versionLock == SummerBuild.VersionLock,
            "halloween" => versionLock == SummerBuild.HalloweenVersionLock,
            "winter" => versionLock == SummerBuild.WinterVersionLock,
            "aprilfools" => versionLock == SummerBuild.AprilFoolsVersionLock,
            "christmas" => versionLock == SummerBuild.ChristmasLiveVersionLock || versionLock == SummerBuild.ChristmasVersionLock,
            "halloween2017" => versionLock == SummerBuild.Halloween2017VersionLock,
            "lobby158" => versionLock == SummerBuild.Lobby158VersionLock,
            _ => false,
        };
    }
}
