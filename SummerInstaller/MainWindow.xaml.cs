using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace SummerInstaller
{
    public partial class MainWindow : Window
    {
        private readonly InstallerSettings _settings;
        private readonly GameInstaller _installer;
        private CancellationTokenSource? _cancel;
        private double _fraction;
        private bool _installing;

        /// <summary>
        /// The config the game will use (before the player's name and password are applied): the embedded default,
        /// one the player chose, or the one already in an existing install.
        /// </summary>
        private string _config = "{}";
        private string? _loadedConfigFolder;

        private static readonly string[] StepNames = { "Download", "Verify", "Extract", "Configure" };

        public MainWindow()
        {
            InitializeComponent();
            _settings = InstallerSettings.Load();
            // Optional overrides: --url <download link> (e.g. a mirror) and --folder <install folder>.
            string[] args = Environment.GetCommandLineArgs();
            string? folderOverride = null;
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == "--url")
                    _settings.DownloadUrl = args[i + 1];
                else if (args[i] == "--folder")
                    folderOverride = args[i + 1];
            }
            _installer = new GameInstaller(_settings);

            TitleText.Text = _settings.Title.ToUpperInvariant();
            SubtitleText.Text = _settings.Subtitle;
            TaglineText.Text = _settings.Tagline;
            TaglineText.Visibility = string.IsNullOrEmpty(_settings.Tagline) ? Visibility.Collapsed : Visibility.Visible;
            Title = $"{_settings.Title} {_settings.Subtitle}";

            SetConfig(_settings.GameConfig, adoptCredentials: true);
            if (string.IsNullOrEmpty(NameBox.Text))
                NameBox.Text = Environment.UserName.Length > 20 ? Environment.UserName.Substring(0, 20) : Environment.UserName;

            FolderBox.Text = folderOverride ?? _settings.ExpandedDefaultInstallFolder;
            BuildSteps();
            StartLogoSpin();
            RefreshSetupState();
        }

        #region Setup view
        private string InstallFolder => FolderBox.Text.Trim();

        private void RefreshSetupState()
        {
            HideBanner();
            string folder = InstallFolder;
            bool validPath = IsValidFolder(folder);
            bool installed = validPath && GameInstaller.IsInstalled(folder);

            // An existing install keeps its server config, name and password.
            if (installed && _loadedConfigFolder != folder)
            {
                _loadedConfigFolder = folder;
                string? existing = GameInstaller.ReadInstalledConfig(folder);
                if (existing != null && GameInstaller.ValidateConfig(existing) == null)
                    SetConfig(existing, adoptCredentials: true);
            }
            bool partial = validPath && GameInstaller.HasPartialDownload(folder);

            if (installed)
            {
                PrimaryAction.Content = "Play";
                SecondaryAction.Content = "Reinstall";
                SecondaryAction.Visibility = Visibility.Visible;
                ShowBanner("Echo VR is already installed here. Play it, or reinstall to download it again.", info: true);
            }
            else
            {
                PrimaryAction.Content = partial ? "Resume download" : "Install";
                SecondaryAction.Visibility = Visibility.Collapsed;
            }
            PrimaryAction.IsEnabled = validPath;
            UpdateSpaceText(folder);
        }

        private static bool IsValidFolder(string folder)
        {
            try
            {
                return folder.Length > 3 && Path.IsPathRooted(folder) && Path.GetFullPath(folder).Length > 3;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateSpaceText(string folder)
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
                string free = GameInstaller.FormatBytes(drive.AvailableFreeSpace) + $" free on {drive.Name.TrimEnd('\\')}";
                if (_settings.DownloadSizeBytes > 0)
                {
                    // The archive and the extracted game exist side by side until the install finishes.
                    long needed = _settings.DownloadSizeBytes * 2;
                    SpaceText.Text = $"Needs about {GameInstaller.FormatBytes(needed)} while installing  ·  {free}";
                    SpaceText.Foreground = drive.AvailableFreeSpace < needed ? (Brush)FindResource("Danger") : (Brush)FindResource("TextFaint");
                }
                else
                {
                    SpaceText.Text = free;
                }
            }
            catch
            {
                SpaceText.Text = "Choose a folder on a local drive.";
            }
        }

        private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
                RefreshSetupState();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where to install Echo VR (a new folder is fine)",
                ShowNewFolderButton = true,
            };
            string current = InstallFolder;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
                current = Path.GetDirectoryName(current) ?? "";
            if (!string.IsNullOrEmpty(current))
                dialog.SelectedPath = current;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            // Picking an empty or unrelated folder installs into a subfolder, unless the game is already there.
            string chosen = dialog.SelectedPath;
            bool empty = !Directory.Exists(chosen) || Directory.GetFileSystemEntries(chosen).Length == 0;
            if (!GameInstaller.IsInstalled(chosen) && !GameInstaller.HasPartialDownload(chosen) && !empty)
                chosen = System.IO.Path.Combine(chosen, "Echo VR Summer");
            FolderBox.Text = chosen;
        }

        private void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateAccount())
                return;
            if (GameInstaller.IsInstalled(InstallFolder))
            {
                // Keep the config current (a changed name, password or server) before playing.
                try
                {
                    GameInstaller.WriteConfig(InstallFolder, _config, DisplayName, Password);
                }
                catch (Exception ex)
                {
                    ShowBanner("Couldn't update the game config: " + ex.Message);
                    return;
                }
                LaunchGame();
                return;
            }
            _ = RunInstallAsync();
        }

        private void SecondaryAction_Click(object sender, RoutedEventArgs e)
        {
            if (ValidateAccount())
                _ = RunInstallAsync();
        }

        #region Account
        private string DisplayName => NameBox.Text.Trim();

        private string Password => PasswordVisible.Visibility == Visibility.Visible ? PasswordVisible.Text : PasswordInput.Password;

        private void SetPassword(string password)
        {
            PasswordInput.Password = password;
            PasswordVisible.Text = password;
        }

        private bool ValidateAccount()
        {
            if (DisplayName.Length == 0)
            {
                ShowBanner("Enter a display name. It's the name other players see above your head.");
                NameBox.Focus();
                return false;
            }
            if (Password.Length == 0)
            {
                ShowBanner("Enter a password. Your account on the server is locked to it the first time you log in.");
                (PasswordVisible.Visibility == Visibility.Visible ? (Control)PasswordVisible : PasswordInput).Focus();
                return false;
            }
            return true;
        }

        private void ShowPassword_Click(object sender, RoutedEventArgs e)
        {
            bool show = PasswordVisible.Visibility != Visibility.Visible;
            if (show)
            {
                PasswordVisible.Text = PasswordInput.Password;
                PasswordVisible.Visibility = Visibility.Visible;
                PasswordInput.Visibility = Visibility.Collapsed;
            }
            else
            {
                PasswordInput.Password = PasswordVisible.Text;
                PasswordInput.Visibility = Visibility.Visible;
                PasswordVisible.Visibility = Visibility.Collapsed;
            }
            ShowPasswordGlyph.Text = show ? "\uED1A" : "\uE7B3";
            ShowPasswordButton.ToolTip = show ? "Hide password" : "Show password";
        }
        #endregion

        #region Server config
        /// <summary>
        /// Switches to a config, optionally taking the name/password already in its login URL.
        /// </summary>
        private void SetConfig(string config, bool adoptCredentials)
        {
            _config = config;
            if (adoptCredentials)
            {
                var (name, password) = GameInstaller.ReadCredentials(config);
                if (name != null)
                    NameBox.Text = name;
                if (password != null)
                    SetPassword(password);
            }
            // Credentials don't make a config custom; only the rest of it does.
            bool isDefault = Normalize(GameInstaller.ApplyCredentials(config, "", "")) == Normalize(GameInstaller.ApplyCredentials(_settings.GameConfig, "", ""));
            ConfigBadgeText.Text = isDefault ? "Default" : "Custom";
            ConfigBadge.Background = new SolidColorBrush(isDefault ? Color.FromArgb(0x22, 0x3F, 0xD0, 0xFF) : Color.FromArgb(0x26, 0xFF, 0x8A, 0x3D));
            ConfigBadgeText.Foreground = (Brush)FindResource(isDefault ? "Blue" : "Orange");
            ConfigServerText.Text = GameInstaller.DescribeServer(config);
        }

        private static string Normalize(string json) => System.Text.RegularExpressions.Regex.Replace(json, @"\s+", "");

        private void ChangeConfig_Click(object sender, RoutedEventArgs e)
        {
            // Show the config with placeholders instead of the player's credentials; they're applied on install/play.
            ConfigEditor.Text = GameInstaller.ApplyCredentials(_config, "AccountName", "AccountPassword");
            HideBanner();
            ShowView(ConfigView);
            ConfigEditor.Focus();
        }

        private void ConfigEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            string? error = GameInstaller.ValidateConfig(ConfigEditor.Text);
            UseConfigButton.IsEnabled = error == null;
            ConfigStatus.Text = error ?? $"Server: {GameInstaller.DescribeServer(ConfigEditor.Text)}";
            ConfigStatus.Foreground = (Brush)FindResource(error == null ? "TextFaint" : "Danger");
        }

        private void LoadConfigFile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a server config",
                Filter = "Config (*.json)|*.json|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                ConfigEditor.Text = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex)
            {
                ConfigStatus.Text = "Couldn't read that file: " + ex.Message;
                ConfigStatus.Foreground = (Brush)FindResource("Danger");
            }
        }

        private void ResetConfig_Click(object sender, RoutedEventArgs e)
        {
            ConfigEditor.Text = _settings.GameConfig;
        }

        private void CancelConfig_Click(object sender, RoutedEventArgs e)
        {
            ShowView(SetupView);
        }

        private void UseConfig_Click(object sender, RoutedEventArgs e)
        {
            if (GameInstaller.ValidateConfig(ConfigEditor.Text) != null)
                return;
            // Keep the name and password the player typed; the config only chooses the server.
            SetConfig(ConfigEditor.Text, adoptCredentials: false);
            ShowView(SetupView);
            if (GameInstaller.IsInstalled(InstallFolder))
                ShowBanner("Server config changed. It's saved when you press Play.", info: true);
        }
        #endregion

        private void ShowBanner(string text, bool info = false)
        {
            BannerText.Text = text;
            Banner.Background = new SolidColorBrush(info ? Color.FromArgb(0x22, 0x3F, 0xD0, 0xFF) : Color.FromArgb(0x26, 0xFF, 0x6B, 0x6B));
            Banner.BorderBrush = new SolidColorBrush(info ? Color.FromArgb(0x55, 0x3F, 0xD0, 0xFF) : Color.FromArgb(0x66, 0xFF, 0x6B, 0x6B));
            BannerIcon.Text = info ? "" : "";
            BannerIcon.Foreground = (Brush)FindResource(info ? "Blue" : "Danger");
            Banner.Visibility = Visibility.Visible;
        }

        private void HideBanner() => Banner.Visibility = Visibility.Collapsed;
        #endregion

        #region Install
        private async Task RunInstallAsync()
        {
            string folder = InstallFolder;
            string config = _config, name = DisplayName, password = Password;
            _cancel = new CancellationTokenSource();
            _installing = true;
            ShowView(ProgressView);
            PauseButton.IsEnabled = true;
            SetStage(InstallStage.Connecting);
            SetProgress(-1, "Connecting…");

            Progress<InstallProgress> progress = new Progress<InstallProgress>(p =>
            {
                SetStage(p.Stage);
                SetProgress(p.Fraction, p.Detail);
            });

            try
            {
                await _installer.InstallAsync(folder, config, name, password, progress, _cancel.Token);
                if (ShortcutCheck.IsChecked == true)
                {
                    try { _installer.CreateDesktopShortcut(folder); } catch { }
                }
                DonePath.Text = folder;
                ShowView(DoneView);
            }
            catch (OperationCanceledException)
            {
                ShowView(SetupView);
                RefreshSetupState();
                ShowBanner("Paused. Press Resume download to continue where it stopped.", info: true);
            }
            catch (Exception ex)
            {
                ShowView(SetupView);
                RefreshSetupState();
                ShowBanner(Describe(ex));
            }
            finally
            {
                _installing = false;
                _cancel.Dispose();
                _cancel = null;
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is UnauthorizedAccessException)
                return "Windows didn't allow writing to that folder. Choose a folder in your user folder or on another drive (not Program Files).";
            if (ex is IOException io && (io.HResult & 0xFFFF) == 112)
                return "The drive is full. Free some space or choose another drive, then press Resume download.";
            if (ex is System.Net.Http.HttpRequestException)
                return "Couldn't download the game: " + (ex.InnerException?.Message ?? ex.Message) + " Check your internet connection and press Resume download.";
            return ex.Message;
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            PauseButton.IsEnabled = false;
            _cancel?.Cancel();
        }
        #endregion

        #region Progress display
        private void BuildSteps()
        {
            Steps.Children.Clear();
            foreach (string name in StepNames)
            {
                StackPanel step = new StackPanel { Orientation = Orientation.Horizontal };
                Grid dot = new Grid { Width = 22, Height = 22 };
                dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) });
                dot.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x10, 0x1F)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                step.Children.Add(dot);
                step.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                Steps.Children.Add(step);
            }
        }

        private void SetStage(InstallStage stage)
        {
            int current = stage switch
            {
                InstallStage.Connecting => 0,
                InstallStage.Downloading => 0,
                InstallStage.Verifying => 1,
                InstallStage.Extracting => 2,
                InstallStage.Configuring => 3,
                _ => 4,
            };
            StageText.Text = stage switch
            {
                InstallStage.Connecting => "Connecting",
                InstallStage.Downloading => "Downloading",
                InstallStage.Verifying => "Verifying",
                InstallStage.Extracting => "Installing files",
                InstallStage.Configuring => "Configuring",
                _ => "Done",
            };
            // Pausing only makes sense while downloading.
            PauseButton.Visibility = current == 0 ? Visibility.Visible : Visibility.Hidden;
            PauseHint.Visibility = PauseButton.Visibility;

            for (int i = 0; i < Steps.Children.Count; i++)
            {
                StackPanel step = (StackPanel)Steps.Children[i];
                Grid dot = (Grid)step.Children[0];
                Ellipse circle = (Ellipse)dot.Children[0];
                TextBlock tick = (TextBlock)dot.Children[1];
                TextBlock label = (TextBlock)step.Children[1];
                if (i < current)
                {
                    circle.Fill = (Brush)FindResource("AccentGradient");
                    tick.Text = "";
                    label.Foreground = (Brush)FindResource("TextDim");
                }
                else if (i == current)
                {
                    circle.Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x3F, 0xD0, 0xFF));
                    tick.Text = "";
                    label.Foreground = (Brush)FindResource("Text");
                }
                else
                {
                    circle.Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                    tick.Text = "";
                    label.Foreground = (Brush)FindResource("TextFaint");
                }
            }
        }

        private void SetProgress(double fraction, string detail)
        {
            DetailText.Text = detail;
            if (fraction < 0)
            {
                PercentText.Text = "";
                Fill.Visibility = Visibility.Collapsed;
                if (Shimmer.Visibility != Visibility.Visible)
                {
                    Shimmer.Visibility = Visibility.Visible;
                    DoubleAnimation slide = new DoubleAnimation(-140, Math.Max(200, Track.ActualWidth), TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever };
                    ShimmerOffset.BeginAnimation(TranslateTransform.XProperty, slide);
                }
                return;
            }
            Shimmer.Visibility = Visibility.Collapsed;
            ShimmerOffset.BeginAnimation(TranslateTransform.XProperty, null);
            Fill.Visibility = Visibility.Visible;
            _fraction = Math.Max(0, Math.Min(1, fraction));
            PercentText.Text = $"{Math.Floor(_fraction * 100)}%";
            Fill.BeginAnimation(WidthProperty, new DoubleAnimation(Track.ActualWidth * _fraction, TimeSpan.FromMilliseconds(250)));
        }

        private void Track_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Fill.BeginAnimation(WidthProperty, null);
            Fill.Width = Track.ActualWidth * _fraction;
        }

        private void ShowView(FrameworkElement view)
        {
            foreach (FrameworkElement v in new FrameworkElement[] { SetupView, ConfigView, ProgressView, DoneView })
                v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
            view.Opacity = 0;
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }

        private void StartLogoSpin()
        {
            LogoRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(24)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        #endregion

        #region Done view / window
        private async void LaunchGame()
        {
            try
            {
                // Pick up newer EchoRelay game files (dbgcore.dll etc.) before starting; quietly skipped if offline.
                IsEnabled = false;
                ShowBanner("Checking for game file updates…", info: true);
                string? updated = await _installer.UpdateGameFilesAsync(InstallFolder, CancellationToken.None);
                if (updated != null)
                    ShowBanner($"Updated EchoRelay game files to {updated}.", info: true);
                GameInstaller.Launch(InstallFolder);
                Close();
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                ShowBanner("Couldn't start the game: " + ex.Message);
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e) => LaunchGame();

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(InstallFolder) { UseShellExecute = true }); } catch { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_installing)
            {
                MessageBoxResult answer = MessageBox.Show(this, "The install is still running. Stop it and close? A download can be resumed later.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancel?.Cancel();
            }
            base.OnClosing(e);
        }
        #endregion
    }
}
