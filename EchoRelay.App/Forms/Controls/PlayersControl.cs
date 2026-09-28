using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Services.Login;

namespace EchoRelay.App.Forms.Controls
{
    /// <summary>
    /// Shows everyone logged in right now: their display name, the game version they play and how long they've been online.
    /// </summary>
    public class PlayersControl : UserControl
    {
        private readonly ListView _players;
        private readonly Label _summary;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private Server? _server;

        /// <summary>
        /// The number of players shown.
        /// </summary>
        public int PlayerCount { get; private set; }

        /// <summary>
        /// Fired after the view refreshes, e.g. so the tab title can show the player count.
        /// </summary>
        public event Action? OnRefreshed;

        public PlayersControl()
        {
            _players = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
            _players.Columns.Add("Player", 240);
            _players.Columns.Add("Version", 160);
            _players.Columns.Add("Online for", 120);
            _summary = new Label { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(3, 6, 3, 3) };
            Label help = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3, 3, 3, 6),
                Text = "Everyone logged in to this server right now, with the game version they play. Game servers that log in (christmas ones do) show under their name.",
            };
            Controls.Add(_players);
            Controls.Add(_summary);
            Controls.Add(help);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _refreshTimer.Tick += (_, _) => RefreshView();
            _refreshTimer.Start();
        }

        /// <summary>
        /// Sets the server whose players are shown (null while it's stopped).
        /// </summary>
        public void SetServer(Server? server)
        {
            _server = server;
            RefreshView();
        }

        private void RefreshView()
        {
            List<LoginService.OnlinePlayer> players = _server?.Running == true ? _server.LoginService.GetOnlinePlayers() : new List<LoginService.OnlinePlayer>();
            _players.BeginUpdate();
            _players.Items.Clear();
            foreach (LoginService.OnlinePlayer player in players)
                _players.Items.Add(new ListViewItem(new[] { player.Name, player.Version, FormatDuration(DateTime.UtcNow - player.Since) }));
            _players.EndUpdate();

            PlayerCount = players.Count;
            _summary.Text = players.Count == 0
                ? (_server?.Running == true ? "Nobody is online." : "The server isn't running.")
                : $"{players.Count} online: " + string.Join(", ", players.GroupBy(p => p.Version).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} on {g.Key}"));
            OnRefreshed?.Invoke();
        }

        private static string FormatDuration(TimeSpan time)
        {
            if (time.TotalMinutes < 1)
                return "just now";
            if (time.TotalHours < 1)
                return $"{(int)time.TotalMinutes} min";
            return $"{(int)time.TotalHours} h {time.Minutes} min";
        }
    }
}
