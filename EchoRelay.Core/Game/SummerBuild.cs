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
        /// The publisher/environment lock the summer build uses.
        /// </summary>
        public const string PublisherLock = "rad15_summer";

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
        public static readonly long LevelLobby = Symbol.Hash("mpl_lobby_b2");
        public static readonly long LevelArena = Symbol.Hash("mpl_arena_a");
        public static readonly long LevelCombatDyson = Symbol.Hash("mpl_combat_dyson");

        /// <summary>
        /// The level a summer game server should load when a matching request does not name one (level -1).
        /// Social lobbies go to the summer lobby, which is what this build is about.
        /// </summary>
        public static long? DefaultLevelForGameType(long? gameType)
        {
            if (gameType == null)
                return LevelSummerLobby;
            if (gameType == GameTypeSocial || gameType == Symbol.Hash("social_2.0_private") || gameType == Symbol.Hash("social_2.0_npe"))
                return LevelSummerLobby;
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
