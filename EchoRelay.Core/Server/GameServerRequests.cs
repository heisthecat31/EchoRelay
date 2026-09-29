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
            ["christmas"] = "Christmas 2017",
            ["halloween2017"] = "Halloween 2017",
        };
    }
}
