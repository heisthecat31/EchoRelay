using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EchoRelay.App.Settings
{
    /// <summary>
    /// The JSON serializable settings used by the application.
    /// </summary>
    public class AppSettings
    {
        #region Properties
        /// <summary>
        /// The file path to the game executable.
        /// </summary>
        [JsonProperty("game_executable")]
        public string GameExecutableFilePath { get; set; } = "";

        /// <summary>
        /// Game executables saved for quick switching between Echo VR builds, keyed by build name (e.g. "Summer 2019").
        /// </summary>
        [JsonProperty("game_executables")]
        public Dictionary<string, string> GameExecutables { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Whether players may request game servers from the installer ("Request game server"). This PC then starts a headless
        /// server of the requested build, from its executable in <see cref="GameExecutables"/>.
        /// </summary>
        [JsonProperty("game_server_requests")]
        public bool GameServerRequestsEnabled { get; set; } = false;

        /// <summary>
        /// How many requested game servers of each game version a player can have running at once.
        /// </summary>
        [JsonProperty("game_server_requests_per_player")]
        public int GameServerRequestsPerPlayer { get; set; } = 2;

        /// <summary>
        /// How many requested game servers can run at once, from all players.
        /// </summary>
        [JsonProperty("game_server_requests_max")]
        public int GameServerRequestsMax { get; set; } = 6;

        /// <summary>
        /// The region this PC hosts requested game servers in (e.g. "EU", "US"), which players pick in the installer.
        /// </summary>
        [JsonProperty("game_server_requests_region")]
        public string GameServerRequestsRegion { get; set; } = "Main";

        /// <summary>
        /// The directory containing the game executable, derived from <see cref="GameExecutableFilePath"/>.
        /// </summary>
        [JsonIgnore]
        public string GameExecutableDirectory
        {
            get
            {
                return Directory.GetParent(GameExecutableFilePath)?.FullName ?? "";
            }
        }

        /// <summary>
        /// The TCP port which the server should bind to.
        /// </summary>
        [JsonProperty("port")]
        public ushort Port { get; set; }

        /// <summary>
        /// A path to the directory which should contain the filesystem-based database used by the application.
        /// Note: This must be null if <see cref="MongoDBConnectionString"/> is set.
        /// </summary>
        [JsonProperty("filesystem_db")]
        public string? FilesystemDatabaseDirectory { get; set; }

        /// <summary>
        /// The connection string to the MongoDB database used by the application.
        /// Note: This must be null if <see cref="FilesystemDatabaseDirectory"/> is set.
        /// </summary>
        [JsonProperty("mongo_db")]
        public string? MongoDBConnectionString { get; set; }

        /// <summary>
        /// Indicates whether the websocket server should be started when the process starts.
        /// </summary>
        [JsonProperty("start_server_on_startup")]
        public bool StartServerOnStartup { get; set; }

        /// <summary>
        /// An API key which if set (non-null), must be provided as a query parameter when 
        /// connecting to ServerDB to successfully pass game server registration.
        /// </summary>
        [JsonProperty("serverdb_api_key")]
        public string? ServerDBApiKey { get; set; }

        /// <summary>
        /// If true, performs raw ping requests to game servers before accepting registration, to ensure their ports are properly exposed.
        /// </summary>
        [JsonProperty("serverdb_validate_servers")]
        public bool? ServerDBValidateGameServers { get; set; }

        /// <summary>
        /// The timeout in milliseconds for each send/receive step of the raw ping request enabled by <see cref="ServerDBValidateGameServers"/>.
        /// </summary>
        [JsonProperty("serverdb_validate_servers_timeout_ms")]
        public int? ServerDBValidateGameServersTimeout { get; set; }


        /// <summary>
        /// Indicates whether the matching service should first prioritize populating game servers until full, or ping.
        /// </summary>
        [JsonProperty("matching_population_over_ping")]
        public bool MatchingPopulationOverPing { get; set; }


        /// <summary>
        /// Indicates whether the matching service should force a match with any available session if it could not match.
        /// This helps when there are no available game servers, but you would still like users to play with eachother.
        /// </summary>
        [JsonProperty("matching_force_into_any_session_on_failure")]
        public bool MatchingForceIntoAnySessionOnFailure { get; set; }

        /// <summary>
        /// The text on the summer build's lobby news board. Null uses the default.
        /// </summary>
        [JsonProperty("summer_news")]
        public string? SummerNews { get; set; }

        /// <summary>
        /// The service status message summer build clients receive. Null uses the default.
        /// </summary>
        [JsonProperty("summer_service_status")]
        public string? SummerServiceStatus { get; set; }

        /// <summary>
        /// Additional fields which are not caught explicitly are retained here.
        /// </summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> AdditionalData = new Dictionary<string, JToken>();
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="AppSettings"/>.
        /// </summary>
        public AppSettings()
        {

        }
        public AppSettings(string gameExecutableFilePath = "", ushort port = 0, string? filesystemDatabaseDirectory = null, string? mongoDbConnectionString = null, bool startServerOnStartup = true, string? serverDbApiKey = null, bool matchingPopulationOverPing = true, bool matchingForceIntoAny = true)
        {
            GameExecutableFilePath = gameExecutableFilePath;
            Port = port;
            FilesystemDatabaseDirectory = filesystemDatabaseDirectory;
            MongoDBConnectionString = mongoDbConnectionString;
            StartServerOnStartup = startServerOnStartup;
            ServerDBApiKey = serverDbApiKey;
            MatchingPopulationOverPing = matchingPopulationOverPing;
            MatchingForceIntoAnySessionOnFailure = matchingForceIntoAny;
        }
        #endregion

        #region Functions
        /// <summary>
        /// Ensures that the settings are appropriately configured for basic operation.
        /// </summary>
        /// <returns>Returns true if the settings are valid, false otherwise.</returns>
        public bool Validate()
        {
            // Must have a valid game executable path.
            if (string.IsNullOrEmpty(GameExecutableFilePath) || !File.Exists(GameExecutableFilePath))
                return false;

            // Must have some origin database to use.
            if (string.IsNullOrEmpty(FilesystemDatabaseDirectory) && string.IsNullOrEmpty(FilesystemDatabaseDirectory))
                return false;

            // If using a filesystem database, the path must be valid.
            if (!string.IsNullOrEmpty(FilesystemDatabaseDirectory) && !Directory.Exists(FilesystemDatabaseDirectory))
                return false;

            // Validation succeeded if we made it here.
            return true;
        }

        /// <summary>
        /// Loads the application settings from a JSON file.
        /// </summary>
        /// <param name="filePath">The file path to load the application settings from.</param>
        /// <returns>Returns the settings if they could be loaded, otherwise null.</returns>
        public static AppSettings? Load(string filePath)
        {
            // Check if the file exists.
            if (!File.Exists(filePath))
                return null;

            // Read the file as text.
            string jsonContents = File.ReadAllText(filePath);

            // Deserialize our settings from the file contents and return it.
            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(jsonContents);
            }
            catch (JsonException)
            {
                // A hand-edited path often has single backslashes ("C:\Users\..."), which JSON reads as escapes. Double
                // any backslash that doesn't start a valid escape and try again; if that fails too, report the original error.
                // Escapes are read in pairs, so an already doubled backslash stays as it is. \b and \f are taken as path
                // characters ("\bin\win7"), since settings never need those control characters.
                string repaired = System.Text.RegularExpressions.Regex.Replace(jsonContents, @"\\(u[0-9A-Fa-f]{4}|.)",
                    m => m.Groups[1].Value.Length == 5 || "\"\\/nrt".Contains(m.Groups[1].Value) ? m.Value : @"\\" + m.Groups[1].Value);
                try
                {
                    return JsonConvert.DeserializeObject<AppSettings>(repaired);
                }
                catch (JsonException)
                {
                }
                throw;
            }
        }

        /// <summary>
        /// Saves the application settings to a given filepath.
        /// </summary>
        /// <param name="filePath">The file path to save the settings to.</param>
        public void Save(string filePath) 
        {
            // Serialize the settings.
            string jsonContents = JsonConvert.SerializeObject(this);

            // Write it to file.
            File.WriteAllText(filePath, jsonContents);
        }
        #endregion
    }
}
