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
        /// The version lock the 2017 halloween build (rad14, Echo Arena 1.76, publisher lock "release4_5") sends. It is
        /// christmas 2017's code two months earlier and speaks the same server messages, but its clients send the older
        /// SNSLobbyFindSessionRequestv4 / CreateSessionRequestv5 / JoinSessionRequestv4.
        /// </summary>
        public const long Halloween2017VersionLock = 0x5E017CAD91E350F7;

        /// <summary>
        /// The version lock Echo Arena 1.58 (rad14, September 2017, publisher lock "release4") sends: halloween 2017's code a
        /// month earlier, served the same way (it frames its messages with the packet header, though).
        /// </summary>
        public const long Lobby158VersionLock = unchecked((long)0x91A20C047C2DA2DA);

        /// <summary>
        /// The version lock Lone Echo's final patch (rad14, loneecho.exe, March 2019) sends with publisher lock "loneecho".
        /// Echo Arena's multiplayer code is still in it, but none of its levels: its sessions are held in Lone Echo's own
        /// levels (see <see cref="LevelLoneEchoBridge"/>).
        /// </summary>
        public const long LoneEchoVersionLock = unchecked((long)0xC041FDC214E41D7F);

        /// <summary>
        /// The version lock the 2018 christmas ("winter") lobby build (rad15_winter, goldmaster 268902) sends. It speaks the
        /// summer build's messages, except for its login settings (SNSLoginClientSettings, like halloween).
        /// </summary>
        public const long WinterVersionLock = unchecked((long)0xA1764C13F7D836C6);

        /// <summary>
        /// The version lock the April Fools 2019 build (goldmaster 298283, version 20.3.298283.1) sends. It speaks christmas 2018's
        /// messages, and its clients log in with that build's publisher lock (rad15_winter); this keeps them on their own servers.
        /// </summary>
        public const long AprilFoolsVersionLock = unchecked((long)0x8AFAD1A64DBBB64A);

        /// <summary>
        /// Checks whether a version lock belongs to the christmas build (under any publisher lock).
        /// </summary>
        public static bool IsChristmasVersionLock(long? versionLock)
        {
            return versionLock == ChristmasVersionLock || versionLock == ChristmasLiveVersionLock || versionLock == Halloween2017VersionLock
                || versionLock == Lobby158VersionLock
                || versionLock == LoneEchoVersionLock;
        }

        /// <summary>
        /// Checks whether a version lock belongs to one of the lobby builds (summer, halloween or christmas) rather than the final build.
        /// </summary>
        public static bool IsLobbyVersionLock(long versionLock)
        {
            return versionLock == VersionLock || versionLock == HalloweenVersionLock || versionLock == WinterVersionLock || versionLock == AprilFoolsVersionLock
                || IsChristmasVersionLock(versionLock);
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
        /// The publisher lock christmas 2018 ("winter") build clients log in with.
        /// </summary>
        public const string WinterPublisherLock = "rad15_winter";

        /// <summary>
        /// The PE header timestamp of the christmas 2018 ("winter") lobby build's echovr.exe.
        /// </summary>
        public const uint WinterExecutableTimestamp = 0x5C17F6B9;

        /// <summary>
        /// The PE header timestamp of the April Fools 2019 build's echovr.exe.
        /// </summary>
        public const uint AprilFoolsExecutableTimestamp = 0x5C9EA0A9;

        /// <summary>
        /// The lobby versions the rad15 lobby builds log in with (SNSLoginRequest's lobbyversion). Each build has its own, while a
        /// publisher lock can be shared (April Fools 2019 clients use christmas 2018's), so they identify a client's build.
        /// </summary>
        public const ulong SummerLobbyVersion = 1563819209;
        public const ulong HalloweenLobbyVersion = 1539037301;
        public const ulong WinterLobbyVersion = 1543524723;
        public const ulong AprilFoolsLobbyVersion = 1553705894;

        /// <summary>
        /// The publisher lock christmas 2017 build (rad14) clients log in with. Like halloween, the christmas client rejects
        /// login connection messages over 16 KiB, so it gets the smaller profile.
        /// </summary>
        public const string ChristmasPublisherLock = "rad15_live";

        /// <summary>
        /// The publisher lock halloween 2017 build (rad14, Echo Arena 1.76) clients log in with. The relay treats them as
        /// christmas 2017 clients (same profiles and saved loadouts); their version lock keeps them on their own servers.
        /// </summary>
        public const string Halloween2017PublisherLock = "release4_5";

        /// <summary>
        /// The publisher lock Echo Arena 1.58 (rad14, September 2017) clients log in with. Served as a christmas 2017 client.
        /// </summary>
        public const string Lobby158PublisherLock = "release4";

        /// <summary>
        /// Checks whether a publisher lock belongs to one of the rad14 builds (christmas or halloween 2017).
        /// </summary>
        public static bool IsRad14PublisherLock(string? publisherLock)
        {
            return publisherLock == ChristmasPublisherLock || publisherLock == Halloween2017PublisherLock || publisherLock == Lobby158PublisherLock
                || publisherLock == LoneEchoPublisherLock;
        }

        /// <summary>
        /// Checks whether a publisher lock belongs to a build whose login connection only takes messages up to 16 KiB.
        /// </summary>
        public static bool HasSmallMessageLimit(string? publisherLock)
        {
            return publisherLock == HalloweenPublisherLock || IsRad14PublisherLock(publisherLock);
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
        /// The halloween 2017 build's lobby (its data has mpl_lobby_a_spooky and mpl_lobby_a, no christmas lobby).
        /// </summary>
        public static readonly long LevelHalloween2017Lobby = Symbol.Hash("mpl_lobby_a_spooky");

        /// <summary>
        /// Echo Arena 1.58's lobby.
        /// </summary>
        public static readonly long LevelLobby158Lobby = Symbol.Hash("mpl_lobby_a");

        /// <summary>
        /// The publisher lock Lone Echo installs log in with (set in their _local\config.json; the build has none of its own).
        /// </summary>
        public const string LoneEchoPublisherLock = "loneecho";

        /// <summary>
        /// Kronos II's bridge (stn_int_itc_bridge), where Lone Echo's sessions are held: the build ships no multiplayer
        /// levels (no arena, lobby or global multiplayer level).
        /// </summary>
        public static readonly long LevelLoneEchoBridge = Symbol.Hash("stn_int_itc_bridge");

        /// <summary>
        /// Lone Echo's level archives (their names cracked from the archive hashes: lone_echo_blender/data/level_names_loneecho1.json).
        /// </summary>
        public static readonly IReadOnlyList<string> LoneEchoLevels = new[]
        {
            "aln_master_vista", "aln_rs1_ext_010_hull", "aln_rs1_ext_010_hull_airlock_powered", "aln_rs1_ext_010_hull_airlock_unpowered",
            "aln_rs1_int_010_enter", "aln_rs1_int_020_cannon", "aln_rs1_int_040_vents_three", "aln_rs1_int_060_pod_room_core_powered",
            "aln_rs2_ext_010_hull", "aln_rs2_int_010_cannon", "aln_rs2_int_020_life_support", "aln_rs2_int_030_conduit",
            "aln_rs2_int_030_conduit_powered", "aln_rs2_int_030_conduit_unpowered", "aln_rs2_int_040_reactor_powered",
            "aln_rs2_int_050_fury_ride", "aln_rs2_int_060_bridge", "aln_rs2_int_060_bridge_static", "aln_rs2_int_070_bridge_future",
            "aln_rs2_int_070_bridge_future_static", "gpr_060_damaged_exterior", "min_dmg_master", "mnu_load", "stn_ext_dmg_station",
            "stn_ext_itc_containers_a", "stn_ext_itc_containers_b", "stn_ext_itc_station_back", "stn_ext_itc_station_front",
            "stn_int_dmg_bridge_alert", "stn_int_itc_bridge", "stn_int_itc_greenhouse_day", "stn_int_itc_greenhouse_night",
            "stn_int_itc_master", "tut_cutter", "tut_dialogue", "tut_helmet", "tut_radiation", "tut_scanner",
        };

        private static readonly HashSet<long> _loneEchoLevelSymbols = new HashSet<long>(LoneEchoLevels.Select(Symbol.Hash));

        /// <summary>
        /// Checks whether a level symbol is one of Lone Echo's levels.
        /// </summary>
        public static bool IsLoneEchoLevel(long? level) => level != null && _loneEchoLevelSymbols.Contains(level.Value);

        /// <summary>
        /// The lobby builds' game types and levels, for naming symbols the final build's symbol cache doesn't know.
        /// </summary>
        private static readonly Lazy<Dictionary<long, string>> _knownNames = new Lazy<Dictionary<long, string>>(() =>
            new[]
            {
                "social_2.0", "social_2.0_private", "social_2.0_npe", "echo_arena", "echo_arena_private", "echo_combat", "echo_combat_private",
                "social", "arena",
                "mpl_lobby_b2", "mpl_lobby_b2_summer", "mpl_lobby_b2_spooky", "mpl_lobby_b2_xmas", "mpl_lobby_a", "mpl_lobby_a_xmas", "mpl_lobby_a_spooky", "mpl_arena_a", "mpl_combat_dyson", "stn_int_itc_bridge",
            }.ToDictionary(Symbol.Hash, name => name));

        /// <summary>
        /// The summer build's combat choices in the client profile, by symbol: weapons, grenades (ordnance) and abilities
        /// (tactical). The christmas client stores these as symbol hashes when it saves a profile it read them from.
        /// </summary>
        private static readonly Lazy<Dictionary<long, string>> _combatChoiceNames = new Lazy<Dictionary<long, string>>(() =>
            new[]
            {
                "assault", "blaster", "rocket", "scout", "magnum",
                "det", "stun", "arc", "burst",
                "buff", "heal", "sensor", "shield", "wraith",
            }.ToDictionary(Symbol.Hash, name => name));

        /// <summary>
        /// The client profile keys holding combat choices by name.
        /// </summary>
        private static readonly string[] _combatChoiceKeys = { "weapon", "grenade", "ability" };

        /// <summary>
        /// Turns combat choices saved as symbol hashes ("weapon": "4743087768721687050") back into the names the summer and
        /// halloween builds read ("rocket"). A client profile saved by the christmas build has them hashed, and the summer
        /// build then can't load its weapon settings ("Unable to load weapon settings in R15NetEquipment"): no combat.
        /// Unknown hashes are removed, so the game picks its default.
        /// </summary>
        /// <returns>True if the profile was changed.</returns>
        public static bool RepairCombatChoices(Newtonsoft.Json.Linq.JObject clientProfile)
        {
            bool changed = false;
            foreach (string key in _combatChoiceKeys)
            {
                if (clientProfile[key]?.Type is not (Newtonsoft.Json.Linq.JTokenType.String or Newtonsoft.Json.Linq.JTokenType.Integer))
                    continue;
                string value = clientProfile[key]!.ToString();
                if (!long.TryParse(value, out long symbol))
                    continue;
                if (_combatChoiceNames.Value.TryGetValue(symbol, out string? name))
                    clientProfile[key] = name;
                else
                    clientProfile.Remove(key);
                changed = true;
            }
            return changed;
        }

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

        /// <summary>
        /// The christmas 2018 ("winter") build's social lobby.
        /// </summary>
        public static readonly long LevelWinterLobby = Symbol.Hash("mpl_lobby_b2_xmas");
        public static readonly long LevelLobby = Symbol.Hash("mpl_lobby_b2");
        public static readonly long LevelArena = Symbol.Hash("mpl_arena_a");
        public static readonly long LevelCombatDyson = Symbol.Hash("mpl_combat_dyson");

        /// <summary>
        /// The level a summer game server should load when a matching request does not name one (level -1).
        /// Social lobbies go to the summer lobby, which is what this build is about.
        /// </summary>
        public static long? DefaultLevelForGameType(long? gameType, long versionLock = VersionLock)
        {
            if (versionLock == LoneEchoVersionLock)
                return LevelLoneEchoBridge;
            if (IsChristmasVersionLock(versionLock))
            {
                if (gameType == GameTypeArenaChristmas || gameType == GameTypeArena || gameType == Symbol.Hash("echo_arena_private"))
                    return LevelArena;
                if (!IsSocialGameType(gameType))
                    return null;
                return versionLock == Halloween2017VersionLock ? LevelHalloween2017Lobby : versionLock == Lobby158VersionLock ? LevelLobby158Lobby : LevelChristmasLobby;
            }
            // The April Fools 2019 build has no seasonal lobby, only mpl_lobby_b2.
            long lobby = versionLock == HalloweenVersionLock ? LevelHalloweenLobby : versionLock == WinterVersionLock ? LevelWinterLobby
                : versionLock == AprilFoolsVersionLock ? LevelLobby : LevelSummerLobby;
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
        /// The PE header timestamp of the halloween 2017 build's EchoArena.exe (Echo Arena 1.76).
        /// </summary>
        public const uint Halloween2017ExecutableTimestamp = 0x59E8F804;

        /// <summary>
        /// The PE header timestamp of Echo Arena 1.58's EchoArena.exe (September 2017).
        /// </summary>
        public const uint Lobby158ExecutableTimestamp = 0x59B81FFD;

        /// <summary>
        /// The PE header timestamp of Lone Echo's final patch (loneecho.exe).
        /// </summary>
        public const uint LoneEchoExecutableTimestamp = 0x5C9D6E49;

        /// <summary>
        /// Checks whether an EchoArena.exe timestamp is one of the rad14 builds (christmas or halloween 2017): the same
        /// command line, launch folder and game files (the patch loads as dbghelp.dll).
        /// </summary>
        public static bool IsRad14ExecutableTimestamp(uint? timestamp)
        {
            return timestamp == ChristmasExecutableTimestamp || timestamp == Halloween2017ExecutableTimestamp || timestamp == Lobby158ExecutableTimestamp
                || timestamp == LoneEchoExecutableTimestamp;
        }

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
                Halloween2017ExecutableTimestamp => "Halloween 2017",
                Lobby158ExecutableTimestamp => "Echo Arena 1.58",
                LoneEchoExecutableTimestamp => "Lone Echo",
                WinterExecutableTimestamp => "Christmas 2018",
                AprilFoolsExecutableTimestamp => "April Fools 2019",
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
