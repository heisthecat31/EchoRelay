using EchoRelay.App.Settings;
using System.Security.Cryptography;

namespace EchoRelay.App.Forms.Dialogs
{
    public partial class SettingsDialog : Form
    {
        /// <summary>
        /// The application settings that are presented for modification.
        /// </summary>
        public AppSettings Settings { get; }
        /// <summary>
        /// The file path to which the <see cref="Settings"/> should be saved.
        /// </summary>
        public string SettingsFilePath { get; }

        /// <summary>
        /// Initializes a new <see cref="SettingsDialog"/>.
        /// </summary>
        /// <param name="settings">The application settings to be presented for modification.</param>
        public SettingsDialog(AppSettings settings, string settingsFilePath)
        {
            InitializeComponent();

            // Load our settings into the UI
            Settings = settings;
            SettingsFilePath = settingsFilePath;

            txtExecutablePath.Text = Settings.GameExecutableFilePath;
            AddGameVersionSelector();
            AddGameServerRequestSettings();
            numericTCPPort.Value = Settings.Port;
            if (Settings.FilesystemDatabaseDirectory != null)
                txtDbFolder.Text = Settings.FilesystemDatabaseDirectory;
            chkStartServerOnStartup.Checked = Settings.StartServerOnStartup;
            chkPopulationOverPing.Checked = Settings.MatchingPopulationOverPing;
            chkForceIntoAnySession.Checked = Settings.MatchingForceIntoAnySessionOnFailure;
            chkValidateGameServers.Checked = Settings.ServerDBValidateGameServers ?? false;
            numValidateGameServersTimeout.Value = (int)(Settings?.ServerDBValidateGameServersTimeout ?? numValidateGameServersTimeout.Value);

            // Set the server DB api key
            txtServerDBApiKey.Text = Settings.ServerDBApiKey ?? "";
            chkUseServerDBApiKeys.Checked = !string.IsNullOrEmpty(Settings.ServerDBApiKey);

            // Set the dialog result to cancelled, this way only if we successfully save settings, do we return OK.
            DialogResult = DialogResult.Cancel;
        }

        #region Game server requests
        private CheckBox chkGameServerRequests = null!;
        private NumericUpDown numRequestsPerPlayer = null!;
        private NumericUpDown numRequestsMax = null!;
        private NumericUpDown numAutoStart = null!;
        private TextBox txtRequestsRegion = null!;
        private NumericUpDown numTickRate = null!;

        /// <summary>
        /// Adds a "Game Server Requests" box above the Save button: whether players may request game servers from the installer,
        /// and how many. Built here rather than in the designer, like the version row.
        /// </summary>
        private void AddGameServerRequestSettings()
        {
            const int boxHeight = 138;
            GroupBox box = new GroupBox
            {
                Text = "Game Servers",
                Location = new Point(btnSaveSettings.Left, btnSaveSettings.Top),
                Size = new Size(btnSaveSettings.Width, boxHeight),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            chkGameServerRequests = new CheckBox
            {
                Text = "Let players request game servers from the installer (started on this PC from the Version list)",
                AutoSize = true,
                Location = new Point(6, 22),
                Checked = Settings.GameServerRequestsEnabled,
            };
            Label lblPerPlayer = new Label { Text = "Per version:", AutoSize = true, Location = new Point(6, 51) };
            numRequestsPerPlayer = new NumericUpDown { Minimum = 1, Maximum = 10, Value = Math.Clamp(Settings.GameServerRequestsPerPlayer, 1, 10), Location = new Point(106, 48), Size = new Size(60, 23) };
            Label lblMax = new Label { Text = "At once, in total:", AutoSize = true, Location = new Point(190, 51) };
            numRequestsMax = new NumericUpDown { Minimum = 1, Maximum = 50, Value = Math.Clamp(Settings.GameServerRequestsMax, 1, 50), Location = new Point(300, 48), Size = new Size(60, 23) };
            Label lblRegion = new Label { Text = "Region:", AutoSize = true, Location = new Point(378, 51) };
            txtRequestsRegion = new TextBox { Text = Settings.GameServerRequestsRegion, Location = new Point(430, 48), Size = new Size(60, 23), MaxLength = 24 };
            Label lblTickRate = new Label { Text = "Server tick rate:", AutoSize = true, Location = new Point(6, 80) };
            numTickRate = new NumericUpDown { Minimum = 0, Maximum = 1000, Value = Math.Clamp(Settings.GameServerTickRate, 0, 1000), Location = new Point(106, 77), Size = new Size(60, 23) };
            Label lblTickRateHelp = new Label { Text = "frames a second, for all game servers this PC starts (0 = uncapped)", AutoSize = true, Location = new Point(172, 80), ForeColor = SystemColors.GrayText };
            Label lblAutoStart = new Label { Text = "On login:", AutoSize = true, Location = new Point(6, 109) };
            numAutoStart = new NumericUpDown { Minimum = 0, Maximum = 6, Value = Math.Clamp(Settings.AutoStartGameServers, 0, 6), Location = new Point(106, 106), Size = new Size(60, 23) };
            Label lblAutoStartHelp = new Label { Text = "game servers started when a player logs in to a version with none (0 = off)", AutoSize = true, Location = new Point(172, 109), ForeColor = SystemColors.GrayText };
            new ToolTip().SetToolTip(numAutoStart, "Started on this PC (if it hosts that version) or an EchoRelay.Host PC. They don't use up the player's own requests, and close after 5 minutes without players.");
            new ToolTip().SetToolTip(numTickRate, "A fixed tick rate for game servers started by this app (requests and the Launch menu). 120 is plenty; lower uses less CPU.");
            new ToolTip().SetToolTip(txtRequestsRegion, "The region players pick in the installer for this PC's game servers (e.g. EU, US).");
            new ToolTip().SetToolTip(numRequestsPerPlayer, "How many requested game servers of each game version a player can have at once. Requested servers close after 5 minutes without players.");
            void UpdateEnabled() => numRequestsPerPlayer.Enabled = numRequestsMax.Enabled = txtRequestsRegion.Enabled = chkGameServerRequests.Checked;
            chkGameServerRequests.CheckedChanged += (_, _) => UpdateEnabled();
            UpdateEnabled();
            box.Controls.AddRange(new Control[] { chkGameServerRequests, lblPerPlayer, numRequestsPerPlayer, lblMax, numRequestsMax, lblRegion, txtRequestsRegion, lblTickRate, numTickRate, lblTickRateHelp, lblAutoStart, numAutoStart, lblAutoStartHelp });
            Controls.Add(box);
        }
        #endregion

        /// <summary>
        /// Puts the Save button under the last settings box and sizes the dialog to fit, once the added rows and boxes are in
        /// and the dialog is scaled. (Growing the dialog while the button was anchored to its bottom moved the button twice,
        /// out of sight.) Scrolls if the screen is too short.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            btnSaveSettings.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            int bottom = Controls.OfType<GroupBox>().Max(box => box.Bottom);
            btnSaveSettings.Top = bottom + 6;
            int wanted = btnSaveSettings.Bottom + 10;
            int available = Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height);
            AutoScroll = wanted > available;
            ClientSize = new Size(ClientSize.Width, Math.Min(wanted, available));
        }

        #region Game version quick switch
        private ComboBox cmbGameVersion = null!;
        private Button btnForgetGameVersion = null!;

        /// <summary>
        /// The game executables known to the quick switch (build name -> path), saved with the settings.
        /// </summary>
        private readonly Dictionary<string, string> _gameExecutables = new Dictionary<string, string>();
        private bool _updatingGameVersion;

        /// <summary>
        /// Adds a "Version" row to the game settings, which remembers an executable for each Echo VR build and switches
        /// between them. It is built here rather than in the designer, and pushes the rest of the dialog down by one row.
        /// </summary>
        private void AddGameVersionSelector()
        {
            const int rowHeight = 29;
            foreach (Control control in Controls)
                if (control != groupBoxGame && control.Top > groupBoxGame.Top)
                    control.Top += rowHeight;
            groupBoxGame.Height += rowHeight;

            Label lblVersion = new Label { AutoSize = true, Location = new Point(6, 25), Text = "Version:" };
            cmbGameVersion = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(106, 22),
                Size = new Size(txtExecutablePath.Width - 76, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            btnForgetGameVersion = new Button
            {
                Text = "Forget",
                Location = new Point(cmbGameVersion.Right + 6, 21),
                Size = new Size(70, 25),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                UseVisualStyleBackColor = true,
            };
            foreach (Control control in groupBoxGame.Controls)
                control.Top += rowHeight;
            groupBoxGame.Controls.Add(lblVersion);
            groupBoxGame.Controls.Add(cmbGameVersion);
            groupBoxGame.Controls.Add(btnForgetGameVersion);
            new ToolTip().SetToolTip(cmbGameVersion, "Switch between the Echo VR builds you have set up. Pick an executable with \"...\" to add a build.");

            foreach (var entry in Settings.GameExecutables)
                _gameExecutables[entry.Key] = entry.Value;
            RememberGameExecutable(Settings.GameExecutableFilePath);
            RefreshGameVersions();

            cmbGameVersion.SelectedIndexChanged += (_, _) =>
            {
                if (_updatingGameVersion || cmbGameVersion.SelectedItem is not string version)
                    return;
                if (_gameExecutables.TryGetValue(version, out string? path))
                {
                    txtExecutablePath.Text = path;
                    SaveGameVersions();
                }
            };
            btnForgetGameVersion.Click += (_, _) =>
            {
                if (cmbGameVersion.SelectedItem is not string version)
                    return;
                _gameExecutables.Remove(version);
                RefreshGameVersions();
                if (cmbGameVersion.Items.Count > 0)
                    cmbGameVersion.SelectedIndex = 0;
                else
                    SaveGameVersions();
            };
        }

        /// <summary>
        /// Saves the game executable and the version list straight away, so a version switch applies (and survives)
        /// without pressing Save Settings. The game executable is only used when launching, so no restart is needed.
        /// </summary>
        private void SaveGameVersions()
        {
            if (File.Exists(txtExecutablePath.Text))
                Settings.GameExecutableFilePath = txtExecutablePath.Text;
            Settings.GameExecutables = new Dictionary<string, string>(_gameExecutables);
            try
            {
                Settings.Save(SettingsFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't save the game version: " + ex.Message, "Echo Relay: Settings");
            }
        }

        /// <summary>
        /// Adds an executable to the quick switch, under its build's name (or its folder, for an unknown build).
        /// </summary>
        private string? RememberGameExecutable(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            string name = Core.Game.SummerBuild.GetBuildName(path) ?? $"Other ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))) ?? path)})";
            _gameExecutables[name] = path;
            return name;
        }

        /// <summary>
        /// Refills the version list, selecting the build of the current executable.
        /// </summary>
        private void RefreshGameVersions()
        {
            _updatingGameVersion = true;
            cmbGameVersion.Items.Clear();
            string? selected = null;
            foreach (var entry in _gameExecutables.OrderBy(x => x.Key))
            {
                cmbGameVersion.Items.Add(entry.Key);
                if (string.Equals(entry.Value, txtExecutablePath.Text, StringComparison.OrdinalIgnoreCase))
                    selected = entry.Key;
            }
            cmbGameVersion.SelectedItem = selected;
            btnForgetGameVersion.Enabled = cmbGameVersion.Items.Count > 0;
            _updatingGameVersion = false;
        }
        #endregion

        private void btnOpenGameFolder_Click(object sender, EventArgs e)
        {
            // Create a folder browser dialog to let the user select the game executable.
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Executable Files (.exe)|*.exe";
            openFileDialog.Multiselect = false;
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                // Set the executable path, and remember it for the version quick switch.
                txtExecutablePath.Text = openFileDialog.FileName;
                RememberGameExecutable(openFileDialog.FileName);
                RefreshGameVersions();
                SaveGameVersions();
            }
        }

        private void btnOpenDbFolder_Click(object sender, EventArgs e)
        {
            // Create a folder browser dialog to let the user select the database folder.
            FolderBrowserDialog folderBrowser = new FolderBrowserDialog();
            if (folderBrowser.ShowDialog() == DialogResult.OK)
            {
                // Set the folder path.
                txtDbFolder.Text = folderBrowser.SelectedPath;
            }
        }

        private void btnSaveSettings_Click(object sender, EventArgs e)
        {
            // Validate the settings provided.
            if (!Directory.Exists(txtDbFolder.Text))
            {
                MessageBox.Show("The provided filesystem database folder could not be found.", "Error");
                return;
            }
            if (!File.Exists(txtExecutablePath.Text))
            {
                MessageBox.Show("The provided game executable could not be found.", "Error");
                return;
            }
            if (!File.Exists(txtExecutablePath.Text))
            {
                MessageBox.Show("The specified executable path does not exist.", "Error");
                return;
            }

            // Obtain the new server DB API key.
            string? newServerDbApiKey = chkUseServerDBApiKeys.Checked ? txtServerDBApiKey.Text.Trim() : null;

            // Display a confirmation dialog if we're about to change an existing API key.
            if (newServerDbApiKey != null && Settings.ServerDBApiKey != null && newServerDbApiKey != Settings.ServerDBApiKey)
            {
                if (MessageBox.Show("Resetting the API key used to access ServerDB may invalidate authentication for game servers which do not update their service configs, would you like to continue?", "Echo Relay: Warning", MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    return;
                }
            }

            // Set the provided in our settings object.
            Settings.Port = (ushort)numericTCPPort.Value;
            Settings.GameExecutableFilePath = txtExecutablePath.Text;
            RememberGameExecutable(txtExecutablePath.Text);
            Settings.GameExecutables = new Dictionary<string, string>(_gameExecutables);
            Settings.GameServerRequestsEnabled = chkGameServerRequests.Checked;
            Settings.GameServerRequestsPerPlayer = (int)numRequestsPerPlayer.Value;
            Settings.GameServerRequestsMax = (int)numRequestsMax.Value;
            Settings.GameServerRequestsRegion = string.IsNullOrWhiteSpace(txtRequestsRegion.Text) ? "Main" : txtRequestsRegion.Text.Trim();
            Settings.GameServerTickRate = (int)numTickRate.Value;
            Settings.AutoStartGameServers = (int)numAutoStart.Value;
            Settings.FilesystemDatabaseDirectory = txtDbFolder.Text;
            Settings.MongoDBConnectionString = null; // TODO: currently unsupported
            Settings.StartServerOnStartup = chkStartServerOnStartup.Checked;
            Settings.ServerDBApiKey = newServerDbApiKey;
            Settings.ServerDBValidateGameServers = chkValidateGameServers.Checked;
            Settings.ServerDBValidateGameServersTimeout = (int)numValidateGameServersTimeout.Value;
            Settings.MatchingPopulationOverPing = chkPopulationOverPing.Checked;
            Settings.MatchingForceIntoAnySessionOnFailure = chkForceIntoAnySession.Checked;

            // Save the settings.
            Settings.Save(SettingsFilePath);

            // Set our result and close the dialog.
            DialogResult = DialogResult.OK;
            Close();
        }

        private void RegenerateServerDBApiKey()
        {
            txtServerDBApiKey.Text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(0x20));
        }

        private void RefreshServerDBApiKey()
        {
            if (!chkUseServerDBApiKeys.Checked)
            {
                chkUseServerDBApiKeys.Checked = false;
                txtServerDBApiKey.ReadOnly = true;
            }
            else
            {
                chkUseServerDBApiKeys.Checked = true;
                if (string.IsNullOrEmpty(txtServerDBApiKey.Text.Trim()))
                    RegenerateServerDBApiKey();
                txtServerDBApiKey.ReadOnly = false;
            }
        }
        private void chkUseServerDBApiKeys_CheckedChanged(object sender, EventArgs e)
        {
            RefreshServerDBApiKey();
        }

        private void btnRegenerateAPIKey_Click(object sender, EventArgs e)
        {
            // Regenerate the key.
            RegenerateServerDBApiKey();
        }

        private void chkValidateGameServers_CheckedChanged(object sender, EventArgs e)
        {
            numValidateGameServersTimeout.Enabled = chkValidateGameServers.Checked;
        }
    }
}
