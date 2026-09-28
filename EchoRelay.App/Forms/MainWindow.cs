using EchoRelay.App.Forms.Controls;
using EchoRelay.App.Forms.Dialogs;
using EchoRelay.App.Properties;
using EchoRelay.App.Settings;
using EchoRelay.App.Utils;
using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Storage;
using EchoRelay.Core.Server.Storage.Filesystem;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EchoRelay
{
    public partial class MainWindow : Form
    {
        #region Properties
        /// <summary>
        /// The original window title set for the app, to be restored if changed, e.g. when appending annotation for unsaved changes, then saving.
        /// </summary>
        private string OriginalWindowTitle { get; }
        /// <summary>
        /// The file path which the <see cref="AppSettings"/> are loaded from.
        /// </summary>
        public string SettingsFilePath { get; }
        /// <summary>
        /// The application settings loaded from the <see cref="SettingsFilePath"/>.
        /// </summary>
        public AppSettings Settings { get; }

        /// <summary>
        /// The websocket server used to power central game services.
        /// </summary>
        public Server Server { get; set; }

        /// <summary>
        /// The UI editors for different storage resources.
        /// </summary>
        public StorageEditorBase[] StorageEditors { get; }

        /// <summary>
        /// The Parties tab: every online player's party, with admin actions.
        /// </summary>
        private readonly PartiesControl partiesControl = new PartiesControl { Dock = DockStyle.Fill };
        private readonly TabPage tabParties = new TabPage("Parties") { Padding = new Padding(3), UseVisualStyleBackColor = true };

        /// <summary>
        /// The Players tab: everyone logged in, with the game version they play.
        /// </summary>
        private readonly PlayersControl playersControl = new PlayersControl { Dock = DockStyle.Fill };
        private readonly TabPage tabPlayers = new TabPage("Players") { Padding = new Padding(3), UseVisualStyleBackColor = true };
        #endregion

        #region Constructor
        public MainWindow()
        {
            InitializeComponent();

            // The server log is written in batches (see AppendLogText).
            _logTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _logTimer.Tick += (_, _) => FlushLog();
            _logTimer.Start();

            // The Parties tab goes after Game Servers.
            tabParties.Controls.Add(partiesControl);
            tabControlMain.TabPages.Insert(tabControlMain.TabPages.IndexOf(tabGameServers) + 1, tabParties);
            partiesControl.OnRefreshed += () => tabParties.Text = partiesControl.PartyCount > 0 ? $"Parties ({partiesControl.PartyCount})" : "Parties";

            // The Players tab goes before Parties.
            tabPlayers.Controls.Add(playersControl);
            tabControlMain.TabPages.Insert(tabControlMain.TabPages.IndexOf(tabParties), tabPlayers);
            playersControl.OnRefreshed += () => tabPlayers.Text = playersControl.PlayerCount > 0 ? $"Players ({playersControl.PlayerCount})" : "Players";

            // Append the assembly version to the window title.
            Version? assemblyVersion = this.GetType().Assembly.GetName().Version;
            if (assemblyVersion != null)
                Text += $" v{assemblyVersion.ToString(3)}";

            // Store the original window title
            OriginalWindowTitle = this.Text;

            // Set our settings file path to be within the current directory.
            SettingsFilePath = Path.Join(Environment.CurrentDirectory, "settings.json");

            // Try to load our application settings.
            AppSettings? settings;
            try
            {
                settings = AppSettings.Load(SettingsFilePath);
            }
            catch (Exception ex)
            {
                // Don't silently fail to open (or overwrite a file the user can still fix): say what's wrong.
                MessageBox.Show($"The settings file couldn't be read:\n{SettingsFilePath}\n\n{ex.Message}\n\n" +
                    "Fix the file (paths need double backslashes, e.g. \"C:\\\\Games\\\\echovr.exe\"), or delete it to set up again.", "Echo Relay: Settings");
                Environment.Exit(1);
                return;
            }

            // Validate the settings.
            if (settings == null || !settings.Validate())
            {
                // Show our initial message describing what is about to happen.
                MessageBox.Show("Application settings have not been correctly configured. Please configure them now.", "Echo Relay: Settings");

                // If the settings weren't initialized, do so with some default values.
                settings ??= new AppSettings(port: 777);

                // Display our settings dialog and expect an OK result (meaning the settings were saved, the dialog was not simply closed).
                SettingsDialog settingsDialog = new SettingsDialog(settings, SettingsFilePath);
                if (settingsDialog.ShowDialog() != DialogResult.OK)
                {
                    // Close this window and return.
                    Environment.Exit(1);
                    return;
                }
            }

            // Set our loaded/created settings
            Settings = settings;

            // Create our file system storage and open it.
            ServerStorage serverStorage = new FilesystemServerStorage(Settings.FilesystemDatabaseDirectory!);
            serverStorage.Open();

            // Perform initial deployment
            bool allCriticalResourcesExist = serverStorage.AccessControlList.Exists() && serverStorage.ChannelInfo.Exists() && serverStorage.LoginSettings.Exists() && serverStorage.SymbolCache.Exists();
            bool anyCriticalResourcesExist = serverStorage.AccessControlList.Exists() || serverStorage.ChannelInfo.Exists() || serverStorage.LoginSettings.Exists() || serverStorage.SymbolCache.Exists();
            bool performInitialSetup = !allCriticalResourcesExist;
            if (performInitialSetup && anyCriticalResourcesExist)
            {
                performInitialSetup = MessageBox.Show("Critical resources are missing from storage, but storage is non-empty.\n" +
                    "Would you like to re-deploy initial setup resources? Warning: this will clear all storage except accounts!", "Redeployment", MessageBoxButtons.YesNo) == DialogResult.Yes;
            }
            if (performInitialSetup)
                InitialDeployment.PerformInitialDeployment(serverStorage, Settings.GameExecutableDirectory, false);

            // Create our list of storage editors and set the storage for each.
            StorageEditors = new StorageEditorBase[] { accessControlListEditor, accountSelector, channelInfoEditor, loginSettingsEditor };
            foreach (StorageEditorBase storageEditor in StorageEditors)
            {
                storageEditor.Storage = serverStorage;
                storageEditor.OnUnsavedChangesStateChange += StorageEditor_OnUnsavedChangesStateChange;
            }

            // Create a server instance and set up our event handlers
            Server = new Server(serverStorage,
                new ServerSettings(
                    port: Settings.Port,
                    serverDbApiKey: Settings.ServerDBApiKey,
                    serverDBValidateServerEndpoint: Settings.ServerDBValidateGameServers ?? false,
                    serverDBValidateServerEndpointTimeout: Settings.ServerDBValidateGameServersTimeout ?? 3000,
                    favorPopulationOverPing: Settings.MatchingPopulationOverPing,
                    forceIntoAnySessionIfCreationFails: Settings.MatchingForceIntoAnySessionOnFailure,
                    summerNews: Settings.SummerNews,
                    summerServiceStatus: Settings.SummerServiceStatus
                    )
                );
            Server.OnServerStarted += Server_OnServerStarted;
            Server.OnServerStopped += Server_OnServerStopped;
            Server.OnAuthorizationResult += Server_OnAuthorizationResult;
            Server.OnServicePeerConnected += Server_OnServicePeerConnected;
            Server.OnServicePeerDisconnected += Server_OnServicePeerDisconnected;
            Server.OnServicePeerAuthenticated += Server_OnServicePeerAuthenticated;
            Server.OnServicePacketSent += Server_OnServicePacketSent;
            Server.OnServicePacketReceived += Server_OnServicePacketReceived;
            Server.ServerDBService.Registry.OnGameServerRegistered += Registry_OnGameServerRegistered;
            Server.ServerDBService.Registry.OnGameServerUnregistered += Registry_OnGameServerUnregistered;
            Server.ServerDBService.OnGameServerRegistrationFailure += ServerDBService_OnGameServerRegistrationFailure; ;

            // Game servers players request from the installer: this PC hosts them if allowed in Settings, and other PCs can
            // connect as hosts (EchoRelay.Host). Hosts and requests are logged.
            Server.GameServerHosts.OnLog += text => this.InvokeUIThread(() => AppendLogText(text));
            EchoRelay.App.Forms.GameServerRequestHost.Apply(Server, Settings);
        }
        #endregion

        #region Functions
        private string getPacketDisplayString(Packet packet)
        {
            string result = "";
            foreach (var message in packet)
            {
                result += $"\t{message}\n";
            }
            return result;
        }
        #endregion

        #region Event Handlers
        private async void Form1_Load(object sender, EventArgs e)
        {
            // Offer a newer release from GitHub, if there is one, then newer game files (EchoRelay DLLs) for the game folders
            // in the Version list, so the game servers this PC starts get fixes too (in the background; both ask first).
            _ = CheckForUpdatesAsync();

            // A "Check for updates" item in the File menu.
            ToolStripMenuItem checkForUpdates = new ToolStripMenuItem("Check for updates");
            checkForUpdates.Click += async (_, _) =>
            {
                if (!await AppUpdater.CheckAsync(this, reportUpToDate: true))
                    await GameFilesUpdater.CheckAsync(this, Settings.GameExecutables.Values.Append(Settings.GameExecutableFilePath));
            };
            fileToolStripMenuItem.DropDownItems.Insert(fileToolStripMenuItem.DropDownItems.IndexOf(exitToolStripMenuItem), checkForUpdates);

            // Start the server if it is configured to start on startup.
            if (Settings.StartServerOnStartup)
                await Server.Start();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (await AppUpdater.CheckAsync(this))
                return; // closing to update; the new version checks the game files
            List<string> executables = Settings.GameExecutables.Values.ToList();
            executables.Add(Settings.GameExecutableFilePath);
            await GameFilesUpdater.CheckAsync(this, executables);
        }

        private void Server_OnServerStarted(Server server)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                btnToggleRunningState.Checked = true;
                btnToggleRunningState.Image = Resources.stop_button_icon;
                lblStatus.Text = "Running";
                startServerToolStripMenuItem.Text = "Stop server";
                progressBarStatus.Style = ProgressBarStyle.Marquee;
                serverInfoControl.UpdateServerInfo(Server, true, SummerBuild.IsSummerExecutable(Settings.GameExecutableFilePath));
                partiesControl.SetServer(Server);
                playersControl.SetServer(Server);
            });
        }
        private void Server_OnServerStopped(Server server)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Reset our UI state
                btnToggleRunningState.Checked = false;
                btnToggleRunningState.Image = Resources.play_button_icon;
                lblStatus.Text = "Idle";
                startServerToolStripMenuItem.Text = "Start server";
                progressBarStatus.Style = ProgressBarStyle.Continuous;
                serverInfoControl.UpdateServerInfo(null, true);
                partiesControl.SetServer(null);
                playersControl.SetServer(null);
            });
        }

        private void Server_OnAuthorizationResult(Server server, IPEndPoint client, bool authorized)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Add to our log
                if (!authorized)
                    AppendLogText($"[SERVER] client({client.Address}:{client.Port}) failed authorization\n");
            });
        }

        private async void btnToggleRunningState_Click(object sender, EventArgs e)
        {
            // If the server is running
            if (Server.Running)
                Server.Stop();
            else
                await Server.Start();
        }

        private void StorageEditor_OnUnsavedChangesStateChange(StorageEditorBase storageEditor, bool hasUnsavedChanges)
        {
            RefreshUnsavedChangesState();
        }

        private void RefreshUnsavedChangesState()
        {
            // Evaluate whether there are unsaved changes.
            bool changed = false;
            foreach (StorageEditorBase storageEditor in StorageEditors)
            {
                changed |= storageEditor.Changed;
            }

            // Update the window title accordingly.
            if (changed)
                this.Text = OriginalWindowTitle + " [unsaved changes]**";
            else
                this.Text = OriginalWindowTitle;
        }

        private void btnSaveChanges_Click(object sender, EventArgs e)
        {
            // Save changes in all editors
            foreach (StorageEditorBase storageEditor in StorageEditors)
                storageEditor.SaveChanges();

            // Refresh our window indicators for unsaved changes.
            RefreshUnsavedChangesState();
        }

        private void btnRevertChanges_Click(object sender, EventArgs e)
        {
            // Provide a confirmation window.
            if (MessageBox.Show("You are about to undo all unsaved changes in all editors. Would you like to continue?", "Echo Relay: Warning", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                // Revert changes in all editors
                foreach (StorageEditorBase storageEditor in StorageEditors)
                    storageEditor.RevertChanges();

                // Refresh our window indicators for unsaved changes.
                RefreshUnsavedChangesState();
            }
        }

        private void Server_OnServicePeerConnected(Service service, Peer peer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Add to our log
                AppendLogText($"[{service.Name}] client({peer.Address}:{peer.Port}) connected\n");

                // Update server info
                serverInfoControl.UpdateServerInfo(Server, false);

                // Add our peer to the peers list
                peerConnectionsControl.AddOrUpdatePeer(peer);

                // Update peer count on the peers tab.
                tabPeers.Text = $"Peers ({peerConnectionsControl.PeerCount})";
            });
        }

        private void Server_OnServicePeerDisconnected(Service service, Peer peer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Add to our log
                AppendLogText($"[{service.Name}] client({peer.Address}:{peer.Port}) disconnected\n");

                // Update server info
                serverInfoControl.UpdateServerInfo(Server, false);

                // Remove a peer from the peers list.
                peerConnectionsControl.RemovePeer(peer);

                // Update peer count on the peers tab.
                tabPeers.Text = peerConnectionsControl.PeerCount > 0 ? $"Peers ({peerConnectionsControl.PeerCount})" : "Peers";
            });
        }

        private void Server_OnServicePeerAuthenticated(Service service, Peer peer, XPlatformId userId)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Update our peer.
                peerConnectionsControl.AddOrUpdatePeer(peer);
            });
        }

        // Packets are logged straight from the network threads: formatting and queueing them doesn't wait for the window.
        private void Server_OnServicePacketReceived(EchoRelay.Core.Server.Services.Service service, EchoRelay.Core.Server.Services.Peer sender, EchoRelay.Core.Server.Messages.Packet packet)
        {
            AppendLogText($"[{service.Name}] client({sender.Address}:{sender.Port})->server:\n" + getPacketDisplayString(packet));
        }

        private void Server_OnServicePacketSent(EchoRelay.Core.Server.Services.Service service, EchoRelay.Core.Server.Services.Peer sender, EchoRelay.Core.Server.Messages.Packet packet)
        {
            AppendLogText($"[{service.Name}] server->client({sender.Address}:{sender.Port}\n" + getPacketDisplayString(packet));
        }

        #region Server log
        /// <summary>
        /// Log text waiting to be shown. The server's threads only queue text here (they used to wait for the window to
        /// add each message, so a busy log stalled the whole server); the window adds it in batches (<see cref="FlushLog"/>).
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly System.Windows.Forms.Timer _logTimer;
        private int _pendingLogCount;
        private int _skippedLogCount;

        /// <summary>Queued messages beyond this are skipped (and counted) until the window catches up.</summary>
        private const int MaxPendingLogMessages = 5000;
        /// <summary>The log box keeps about this many characters, dropping the oldest lines.</summary>
        private const int MaxLogCharacters = 300_000;

        /// <summary>
        /// Adds text to the server log. Safe from any thread and never waits.
        /// </summary>
        private void AppendLogText(string text)
        {
            if (Interlocked.Increment(ref _pendingLogCount) > MaxPendingLogMessages)
            {
                Interlocked.Decrement(ref _pendingLogCount);
                Interlocked.Increment(ref _skippedLogCount);
                return;
            }
            _pendingLog.Enqueue(text);
        }

        /// <summary>
        /// Adds the queued log text to the log box in one go, trims the oldest text, and keeps it scrolled to the end if it was.
        /// </summary>
        private void FlushLog()
        {
            if (_pendingLog.IsEmpty && _skippedLogCount == 0)
                return;
            System.Text.StringBuilder batch = new System.Text.StringBuilder();
            while (batch.Length < MaxLogCharacters && _pendingLog.TryDequeue(out string? text))
            {
                Interlocked.Decrement(ref _pendingLogCount);
                batch.Append(text);
            }
            int skipped = Interlocked.Exchange(ref _skippedLogCount, 0);
            if (skipped > 0)
                batch.Append($"[LOG] {skipped} messages not shown (too many to display)\n");

            bool atEnd = rtbLog.SelectionStart >= rtbLog.TextLength - 1;
            rtbLog.AppendText(batch.ToString());
            if (rtbLog.TextLength > MaxLogCharacters)
            {
                // Drop whole lines from the start, down to about three quarters of the limit.
                int line = rtbLog.GetLineFromCharIndex(rtbLog.TextLength - MaxLogCharacters * 3 / 4);
                int cut = rtbLog.GetFirstCharIndexFromLine(line);
                if (cut > 0)
                {
                    bool readOnly = rtbLog.ReadOnly;
                    rtbLog.ReadOnly = false;
                    rtbLog.Select(0, cut);
                    rtbLog.SelectedText = "";
                    rtbLog.ReadOnly = readOnly;
                }
            }
            if (atEnd)
            {
                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.ScrollToCaret();
            }
        }
        #endregion

        private void Registry_OnGameServerRegistered(EchoRelay.Core.Server.Services.ServerDB.RegisteredGameServer gameServer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Unregister any existing events
                gameServer.OnPlayersAdded += GameServer_OnPlayersAdded;
                gameServer.OnPlayerRemoved += GameServer_OnPlayerRemoved;
                gameServer.OnSessionStateChanged += GameServer_OnSessionStateChanged;

                // Register our event handlers for any newly registered server.
                gameServer.OnPlayersAdded += GameServer_OnPlayersAdded;
                gameServer.OnPlayerRemoved += GameServer_OnPlayerRemoved;
                gameServer.OnSessionStateChanged += GameServer_OnSessionStateChanged;

                // Add the game server to our UI control
                gameServersControl.AddOrUpdateGameServer(gameServer);

                // Update server count on the game server tab.
                tabGameServers.Text = $"Game Servers ({gameServersControl.GameServerCount})";

                AppendLogText($"[{gameServer.Peer.Service.Name}] client({gameServer.Peer.Address}:{gameServer.Peer.Port}) registered game server (server_id={gameServer.ServerId}, region_symbol={gameServer.RegionSymbol}, version_lock={gameServer.VersionLock}, endpoint=<{gameServer.ExternalAddress}:{gameServer.Port}>)\n");
            });
        }

        private void Registry_OnGameServerUnregistered(EchoRelay.Core.Server.Services.ServerDB.RegisteredGameServer gameServer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Remove the game server from our UI control
                gameServersControl.RemoveGameServer(gameServer);

                // Update server count on the game server tab.
                tabGameServers.Text = gameServersControl.GameServerCount > 0 ? $"Game Servers ({gameServersControl.GameServerCount})" : "Game Servers";

                // Add to our log
                AppendLogText($"[{gameServer.Peer.Service.Name}] client({gameServer.Peer.Address}:{gameServer.Peer.Port}) unregistered game server (server_id={gameServer.ServerId}, region_symbol={gameServer.RegionSymbol}, version_lock={gameServer.VersionLock}, endpoint=<{gameServer.ExternalAddress}:{gameServer.Port}>)\n");
            });
        }

        private void ServerDBService_OnGameServerRegistrationFailure(Peer peer, Core.Server.Messages.ServerDB.ERGameServerRegistrationRequest registrationRequest, string failureMessage)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Add to our log
                AppendLogText($"[{peer.Service.Name}] client({peer.Address}:{peer.Port}) failed to register game server: \"{failureMessage}\"\n");
            });
        }

        private void GameServer_OnPlayersAdded(EchoRelay.Core.Server.Services.ServerDB.RegisteredGameServer gameServer, (Guid playerSession, Peer? peer)[] players)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Update the state of the game server in our UI control
                gameServersControl.AddOrUpdateGameServer(gameServer);
            });
        }

        private void GameServer_OnPlayerRemoved(EchoRelay.Core.Server.Services.ServerDB.RegisteredGameServer gameServer, Guid playerSession, Peer? peer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Update the state of the game server in our UI control
                gameServersControl.AddOrUpdateGameServer(gameServer);
            });
        }

        private void GameServer_OnSessionStateChanged(EchoRelay.Core.Server.Services.ServerDB.RegisteredGameServer gameServer)
        {
            // Invoke the UI thread to perform updates.
            this.InvokeUIThread(() =>
            {
                // Update the state of the game server in our UI control
                gameServersControl.AddOrUpdateGameServer(gameServer);
            });
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Begin closing this window.
            Close();
        }

        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Stop any server operations.
            Server.Stop();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("For personal education/research purposes only.");
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Display our settings dialog. If the user saved settings, we warn the application will be restarted.
            SettingsDialog settingsDialog = new SettingsDialog(Settings, SettingsFilePath);
            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                MessageBox.Show("Application settings have been changed, the application will now restart.");
                Application.Restart();
                return;
            }
        }

        private void serverHeadlessThrottledToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a headless, no OVR server at the settings' tick rate.
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Server, noOVR: true, headless: true, timeStep: (uint)Math.Max(0, Settings.GameServerTickRate));
        }

        private void serverheadlessUnthrottledHighCPUToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a headless, no OVR server with an unthrottled timestep (zero).
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Server, noOVR: true, headless: true, timeStep: 0);
        }

        private void serverToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a no OVR server.
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Server, noOVR: true, timeStep: (uint)Math.Max(0, Settings.GameServerTickRate));
        }

        private void clientWindowedNoOVRToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a windowed no OVR client.
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Client, windowed: true, noOVR: true);
        }

        private void clientWindowedOVRToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a windowed OVR client.
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Client, windowed: true);
        }

        private void clientOVRToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Launch game as a VR OVR client.
            GameLauncher.Launch(Settings.GameExecutableFilePath, GameLauncher.LaunchRole.Client);
        }

        private void closeAllGameServersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Force-close every Echo VR game server running on this PC (all builds, including hidden headless servers)? " +
                "Players in their matches will be disconnected. Game clients are left running.", "Echo Relay: Close All Game Servers",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            var (closed, failed) = GameLauncher.CloseAllGameServers();
            string message = closed == 0 && failed.Count == 0 ? "No game servers are running on this PC." : $"Closed {closed} game server{(closed == 1 ? "" : "s")}.";
            if (failed.Count > 0)
                message += $"\n\nCouldn't close {failed.Count} (try running Echo Relay as administrator):\n" + string.Join("\n", failed);
            MessageBox.Show(message, "Echo Relay: Close All Game Servers", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void customToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Create a game launcher dialog and show it
            GameLauncherDialog gameLauncherDialog = new GameLauncherDialog(Settings);
            gameLauncherDialog.ShowDialog();
        }

        private void controlRuntimeGeneral_Load(object sender, EventArgs e)
        {

        }

        private void clearToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbLog.Text = "";
        }
        #endregion
    }
}
