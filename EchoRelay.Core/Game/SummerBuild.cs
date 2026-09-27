using System.Reflection;

namespace EchoRelay.Core.Game
{
    /// <summary>
    /// Constants and helpers for the Echo VR "summer lobby" build (rad15_summer, echovr.exe built 2019-07-24,
    /// PE timestamp 0x5D388D3C). This build predates most of the message versions EchoRelay was written against,
    /// so it has its own login/matching message layouts (see Server/Messages/Summer).
    /// </summary>
    public static class SummerBuild
    {
        /// <summary>
        /// The PE header timestamp of the summer build's echovr.exe.
        /// </summary>
        public const uint ExecutableTimestamp = 0x5D388D3C;

        /// <summary>
        /// The PE header timestamp of the final (2023) echovr.exe that EchoRelay originally targets.
        /// </summary>
        public const uint FinalExecutableTimestamp = 0x6452DFF6;

        /// <summary>
        /// The version lock sent by summer clients in matching requests and by summer game servers when registering.
        /// </summary>
        public const long VersionLock = 0x5AAD93959C4F7822;

        /// <summary>
        /// The version lock the 2018 halloween lobby build (rad15_halloween, goldmaster 253636) sends. It speaks the same
        /// lobby messages as the summer build, but its clients and servers can't play with the summer build's.
        /// </summary>
        public const long HalloweenVersionLock = unchecked((long)0xECC998A3B5CA42F2);

        /// <summary>
        /// The version lock the 2017 christmas lobby build (rad14, ea_rel6_0, EchoArena.exe) sends. It is a generation
        /// older: its matching messages are SNSLobbyFindSessionRequestv6 / CreateSessionRequestv6 and it takes
        /// SNSLobbySessionSuccessv3 and SNSLobbySessionFailurev2.
        /// </summary>
        public const long ChristmasVersionLock = unchecked((long)0xDA2FCE47C3B8B9CC);

        /// <summary>
        /// The christmas build derives its version lock from the publisher_lock in _local\config.json. The lock above is what
        /// it sends with "rad15_xmas"; this is what it sends with "rad15_live" (the distributed install's config).
        /// Clients only match to game servers with the exact same lock.
        /// </summary>
        public const long ChristmasLiveVersionLock = unchecked((long)0xF178DD22E259B1C3);

        /// <summary>
        /// Checks whether a version lock belongs to the christmas build (under any publisher lock).
        /// </summary>
        public static bool IsChristmasVersionLock(long? versionLock)
        {
            return versionLock == ChristmasVersionLock || versionLock == ChristmasLiveVersionLock;
        }

        /// <summary>
        /// Checks whether a version lock belongs to one of the lobby builds (summer, halloween or christmas) rather than the final build.
        /// </summary>
        public static bool IsLobbyVersionLock(long versionLock)
        {
            return versionLock == VersionLock || versionLock == HalloweenVersionLock || IsChristmasVersionLock(versionLock);
        }

        /// <summary>
        /// The publisher/environment lock the summer build uses.
        /// </summary>
        public const string PublisherLock = "rad15_summer";

        /// <summary>
        /// The publisher lock halloween lobby build (rad15_halloween) clients log in with.
        /// </summary>
        public const string HalloweenPublisherLock = "rad15_halloween";

        /// <summary>
        /// The publisher lock christmas 2017 build (rad14) clients log in with. Like halloween, the christmas client rejects
        /// login connection messages over 16 KiB, so it gets the smaller profile.
        /// </summary>
        public const string ChristmasPublisherLock = "rad15_live";

        /// <summary>
        /// Checks whether a publisher lock belongs to a build whose login connection only takes messages up to 16 KiB.
        /// </summary>
        public static bool HasSmallMessageLimit(string? publisherLock)
        {
            return publisherLock == HalloweenPublisherLock || publisherLock == ChristmasPublisherLock;
        }

        /// <summary>
        /// The Oculus application id of Echo VR.
        /// </summary>
        public const string AppId = "1369078409873402";

        /// <summary>
        /// The platform symbol ("ovr") summer clients send in matching requests.
        /// </summary>
        public static readonly long PlatformSymbolOvr = Symbol.Hash("ovr");

        public static readonly long GameTypeSocial = Symbol.Hash("social_2.0");
        public static readonly long GameTypeArena = Symbol.Hash("echo_arena");
        public static readonly long GameTypeCombat = Symbol.Hash("echo_combat");

        public static readonly long LevelSummerLobby = Symbol.Hash("mpl_lobby_b2_summer");

        /// <summary>
        /// The christmas build's game types ("Social", "Arena"; hashed case-insensitively) and its christmas lobby.
        /// </summary>
        public static readonly long GameTypeSocialChristmas = Symbol.Hash("social");
        public static readonly long GameTypeArenaChristmas = Symbol.Hash("arena");
        public static readonly long LevelChristmasLobby = Symbol.Hash("mpl_lobby_a_xmas");

        /// <summary>
        /// The lobby builds' game types and levels, for naming symbols the final build's symbol cache doesn't know.
        /// </summary>
        private static readonly Lazy<Dictionary<long, string>> _knownNames = new Lazy<Dictionary<long, string>>(() =>
            new[]
            {
                "social_2.0", "social_2.0_private", "social_2.0_npe", "echo_arena", "echo_arena_private", "echo_combat", "echo_combat_private",
                "social", "arena",
                "mpl_lobby_b2", "mpl_lobby_b2_summer", "mpl_lobby_b2_spooky", "mpl_lobby_a", "mpl_lobby_a_xmas", "mpl_arena_a", "mpl_combat_dyson",
            }.ToDictionary(Symbol.Hash, name => name));

        /// <summary>
        /// Names a lobby build game type or level symbol, or null if it isn't one.
        /// </summary>
        public static string? GetKnownName(long symbol)
        {
            return _knownNames.Value.TryGetValue(symbol, out string? name) ? name : null;
        }

        private static readonly HashSet<long> _privateGameTypes = new HashSet<long>
        {
            Symbol.Hash("echo_arena_private"), Symbol.Hash("echo_combat_private"), Symbol.Hash("social_2.0_private"),
        };

        /// <summary>
        /// Checks whether a game type is a private match's (e.g. echo_arena_private, from a lobby terminal's private match).
        /// A session created for one must be private, whatever lobby type the request's (not fully mapped) fields read as.
        /// </summary>
        public static bool IsPrivateGameType(long? gameType)
        {
            return gameType != null && _privateGameTypes.Contains(gameType.Value);
        }

        /// <summary>
        /// Checks whether a game type is a social (lobby) game type of any of the lobby builds.
        /// </summary>
        public static bool IsSocialGameType(long? gameType)
        {
            return gameType == null || gameType == GameTypeSocial || gameType == Symbol.Hash("social_2.0_private")
                || gameType == Symbol.Hash("social_2.0_npe") || gameType == GameTypeSocialChristmas;
        }

        /// <summary>
        /// The halloween build's social lobby. It has no mpl_lobby_b2_summer; its data ships mpl_lobby_b2_spooky instead.
        /// </summary>
        public static readonly long LevelHalloweenLobby = Symbol.Hash("mpl_lobby_b2_spooky");
        public static readonly long LevelLobby = Symbol.Hash("mpl_lobby_b2");
        public static readonly long LevelArena = Symbol.Hash("mpl_arena_a");
        public static readonly long LevelCombatDyson = Symbol.Hash("mpl_combat_dyson");

        /// <summary>
        /// The level a summer game server should load when a matching request does not name one (level -1).
        /// Social lobbies go to the summer lobby, which is what this build is about.
        /// </summary>
        public static long? DefaultLevelForGameType(long? gameType, long versionLock = VersionLock)
        {
            if (IsChristmasVersionLock(versionLock))
            {
                if (gameType == GameTypeArenaChristmas || gameType == GameTypeArena || gameType == Symbol.Hash("echo_arena_private"))
                    return LevelArena;
                return IsSocialGameType(gameType) ? LevelChristmasLobby : null;
            }
            long lobby = versionLock == HalloweenVersionLock ? LevelHalloweenLobby : LevelSummerLobby;
            if (gameType == null)
                return lobby;
            if (gameType == GameTypeSocial || gameType == Symbol.Hash("social_2.0_private") || gameType == Symbol.Hash("social_2.0_npe"))
                return lobby;
            if (gameType == GameTypeArena || gameType == Symbol.Hash("echo_arena_private"))
                return LevelArena;
            if (gameType == GameTypeCombat || gameType == Symbol.Hash("echo_combat_private"))
                return LevelCombatDyson;
            return null;
        }

        /// <summary>
        /// Every cosmetic item name in the summer build's item_unlocks.json.
        /// </summary>
        public static IReadOnlyList<string> Unlockables => _unlockables.Value;
        private static readonly Lazy<string[]> _unlockables = new Lazy<string[]>(() =>
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EchoRelay.Core.Resources.summer_unlockables.txt");
            if (stream == null)
                return Array.Empty<string>();
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        });

        /// <summary>
        /// A copy of the summer build's own default server profile (sourcedb/rad15/json/r14/defaultprofile_ro.json).
        /// It defines the summer formats: "loadout" as {"instances": [{"name", "items": [{"itemslot", "item"}]}], "number"}
        /// with the general/blue/orange/social/blue_combat/orange_combat/social_combat instances, and "unlocks" /
        /// "unlocks_combat" as arrays of item names (not the final build's {"arena": {item: true}} objects).
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject DefaultServerProfile => (Newtonsoft.Json.Linq.JObject)_defaultServerProfile.Value.DeepClone();
        private static readonly Lazy<Newtonsoft.Json.Linq.JObject> _defaultServerProfile = new Lazy<Newtonsoft.Json.Linq.JObject>(() =>
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EchoRelay.Core.Resources.summer_defaultprofile_ro.json");
            if (stream == null)
                return new Newtonsoft.Json.Linq.JObject();
            using StreamReader reader = new StreamReader(stream);
            return Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
        });

        /// <summary>
        /// A copy of the halloween build's own default server profile (its sourcedb/rad15/json/r14/defaultprofile_ro.json).
        /// Same formats as summer's, but smaller, and with no "unlocks_combat". The halloween client rejects login connection
        /// messages over 16 KiB, so its profiles are built from this rather than the summer default.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject HalloweenDefaultServerProfile => (Newtonsoft.Json.Linq.JObject)_halloweenDefaultServerProfile.Value.DeepClone();
        private static readonly Lazy<Newtonsoft.Json.Linq.JObject> _halloweenDefaultServerProfile = new Lazy<Newtonsoft.Json.Linq.JObject>(() =>
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EchoRelay.Core.Resources.halloween_defaultprofile_ro.json");
            if (stream == null)
                return new Newtonsoft.Json.Linq.JObject();
            using StreamReader reader = new StreamReader(stream);
            return Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
        });

        /// <summary>
        /// The largest message the halloween client accepts on its login connection (its TCP broadcaster rejects the peer,
        /// disconnecting from the relay, beyond this).
        /// </summary>
        public const int HalloweenMaxMessageSize = 16384;

        /// <summary>
        /// Determines which build a given echovr.exe is, from its PE header timestamp.
        /// </summary>
        /// <param name="executableFilePath">The path to echovr.exe.</param>
        /// <returns>True if the executable is the summer build.</returns>
        public static bool IsSummerExecutable(string executableFilePath)
        {
            uint? timestamp = ReadPETimestamp(executableFilePath);
            return timestamp == ExecutableTimestamp;
        }

        /// <summary>
        /// The PE header timestamp of the halloween lobby build's echovr.exe.
        /// </summary>
        public const uint HalloweenExecutableTimestamp = 0x5BC7B897;

        /// <summary>
        /// The PE header timestamp of the christmas 2017 lobby build's EchoArena.exe.
        /// </summary>
        public const uint ChristmasExecutableTimestamp = 0x5A39494F;

        /// <summary>
        /// Names the Echo VR build an executable belongs to, from its PE header timestamp.
        /// </summary>
        /// <param name="executableFilePath">The path to the game executable.</param>
        /// <returns>The build's name, or null if it is not a known build.</returns>
        public static string? GetBuildName(string executableFilePath)
        {
            return ReadPETimestamp(executableFilePath) switch
            {
                FinalExecutableTimestamp => "Latest (final)",
                ExecutableTimestamp => "Summer 2019",
                HalloweenExecutableTimestamp => "Halloween 2018",
                ChristmasExecutableTimestamp => "Christmas 2017",
                _ => null,
            };
        }

        /// <summary>
        /// Reads the PE header timestamp of an executable.
        /// </summary>
        public static uint? ReadPETimestamp(string executableFilePath)
        {
            try
            {
                using FileStream fs = File.OpenRead(executableFilePath);
                using BinaryReader reader = new BinaryReader(fs);
                fs.Position = 0x3C;
                int peOffset = reader.ReadInt32();
                fs.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                    return null;
                fs.Position = peOffset + 8;
                return reader.ReadUInt32();
            }
            catch
            {
                return null;
            }
        }
    }
}
