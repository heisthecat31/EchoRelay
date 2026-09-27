using EchoRelay.Core.Game;

namespace EchoRelay.Core.Test.Game
{
    /// <summary>
    /// Tests for telling the lobby builds (summer, halloween) apart, which decides which game servers their clients get.
    /// </summary>
    public class LobbyBuildTests
    {
        [Fact]
        public void LobbyVersionLocksAreRecognised()
        {
            Assert.True(SummerBuild.IsLobbyVersionLock(SummerBuild.VersionLock));
            Assert.True(SummerBuild.IsLobbyVersionLock(SummerBuild.HalloweenVersionLock));
            Assert.False(SummerBuild.IsLobbyVersionLock(0));
            Assert.NotEqual(SummerBuild.VersionLock, SummerBuild.HalloweenVersionLock);
        }

        [Fact]
        public void HalloweenVersionLockMatchesACapturedClient()
        {
            // SummerLobbyFindSessionRequestv8 from a rad15_halloween client (goldmaster 253636).
            Assert.Equal(-1384407581542694158, SummerBuild.HalloweenVersionLock);
        }

        [Fact]
        public void SocialDefaultsToEachBuildsOwnLobby()
        {
            // The halloween build's data has mpl_lobby_b2_spooky and no mpl_lobby_b2_summer.
            Assert.Equal(Symbol.Hash("mpl_lobby_b2_summer"), SummerBuild.DefaultLevelForGameType(SummerBuild.GameTypeSocial, SummerBuild.VersionLock));
            Assert.Equal(Symbol.Hash("mpl_lobby_b2_spooky"), SummerBuild.DefaultLevelForGameType(SummerBuild.GameTypeSocial, SummerBuild.HalloweenVersionLock));
            Assert.Equal(Symbol.Hash("mpl_lobby_b2_spooky"), SummerBuild.DefaultLevelForGameType(null, SummerBuild.HalloweenVersionLock));
            // Arena exists in both builds.
            Assert.Equal(SummerBuild.LevelArena, SummerBuild.DefaultLevelForGameType(SummerBuild.GameTypeArena, SummerBuild.HalloweenVersionLock));
        }

        [Fact]
        public void KnownLevelSymbols()
        {
            Assert.Equal(unchecked((long)0xA9B30EF16760761C), Symbol.Hash("mpl_lobby_b2_summer"));
            Assert.Equal(unchecked((long)0xA9B30EF465627817), Symbol.Hash("mpl_lobby_b2_spooky"));
            Assert.Equal(unchecked((long)0xAC360E41E4EDE056), Symbol.Hash("mnu_master"));
        }
    }
}
