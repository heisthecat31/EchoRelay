using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Services.Social;

namespace EchoRelay.App.Forms.Controls
{
    /// <summary>
    /// Shows every online player's party (EchoRelay's social service, which stands in for Oculus rooms on the lobby builds),
    /// and lets an admin remove players from parties, make them leader, add them to a party (by invite) or disband a party.
    /// </summary>
    public class PartiesControl : UserControl
    {
        private readonly ListView _players;
        private readonly Button _remove, _promote, _add, _disband, _refresh;
        private readonly ComboBox _partyChoice;
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private Server? _server;
        private volatile bool _refreshPending;

        /// <summary>
        /// The number of parties shown.
        /// </summary>
        public int PartyCount { get; private set; }

        /// <summary>
        /// Fired after the view refreshes, e.g. so the tab title can show the party count.
        /// </summary>
        public event Action? OnRefreshed;

        public PartiesControl()
        {
            _players = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            _players.Columns.Add("Player", 220);
            _players.Columns.Add("User ID", 170);
            _players.Columns.Add("Party leader", 220);
            _players.Columns.Add("Role", 80);
            _players.Columns.Add("Party size", 80);
            _players.Columns.Add("Locked", 60);
            _players.SelectedIndexChanged += (_, _) => UpdateButtons();

            _remove = new Button { Text = "Remove from party", AutoSize = true };
            _promote = new Button { Text = "Make leader", AutoSize = true };
            _disband = new Button { Text = "Disband party", AutoSize = true };
            _partyChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            _add = new Button { Text = "Add to party", AutoSize = true };
            _refresh = new Button { Text = "Refresh", AutoSize = true };
            _status = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            _remove.Click += async (_, _) => await Run(service => service.AdminRemoveFromParty(SelectedPlayer!.Id));
            _promote.Click += async (_, _) => await Run(service => service.AdminPromote(SelectedPlayer!.Id));
            _disband.Click += async (_, _) =>
            {
                if (SelectedParty() is SocialService.PartyInfo party &&
                    MessageBox.Show($"Disband {party.Leader.Name}'s party ({party.Members.Count} players)?", "Echo Relay: Parties", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    await Run(service => service.AdminDisband(party.Id));
            };
            _add.Click += async (_, _) =>
            {
                if (_partyChoice.SelectedItem is PartyChoice choice)
                    await Run(service => service.AdminInviteToParty(SelectedPlayer!.Id, choice.Party.Id),
                        $"Sent {SelectedPlayer!.Name} an invite to {choice.Party.Leader.Name}'s party: they join by accepting it in game.");
            };
            _refresh.Click += (_, _) => RefreshView();

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(3) };
            buttons.Controls.AddRange(new Control[] { _remove, _promote, _disband, new Label { Text = "  Add to:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _partyChoice, _add, _refresh, _status });
            Label help = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3, 3, 3, 6),
                Text = "Parties of players connected to the social service (lobby builds). Every player normally leads a party of their own; " +
                       "\"Make leader\" fixes a player who gets \"reserved for party leader\". Adding sends the player an invite they accept in game.",
            };
            Controls.Add(_players);
            Controls.Add(buttons);
            Controls.Add(help);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _refreshTimer.Tick += (_, _) => RefreshView();
            _refreshTimer.Start();
            UpdateButtons();
        }

        private record PartyChoice(SocialService.PartyInfo Party)
        {
            public override string ToString() => $"{Party.Leader.Name}'s party ({Party.Members.Count}/{Party.MaxMembers}{(Party.Locked ? ", locked" : "")})";
        }

        private record PlayerRow(ulong Id, string Name, SocialService.PartyInfo? Party);

        private PlayerRow? SelectedPlayer => _players.SelectedItems.Count > 0 ? _players.SelectedItems[0].Tag as PlayerRow : null;

        private SocialService.PartyInfo? SelectedParty() => SelectedPlayer?.Party;

        /// <summary>
        /// Sets the running server (or null when stopped).
        /// </summary>
        public void SetServer(Server? server)
        {
            if (_server != null)
                _server.SocialService.OnPartiesChanged -= SocialService_OnPartiesChanged;
            _server = server;
            if (_server != null)
                _server.SocialService.OnPartiesChanged += SocialService_OnPartiesChanged;
            RefreshView();
        }

        private void SocialService_OnPartiesChanged()
        {
            // Changes come in bursts (a join updates every member); refresh once, on the UI thread.
            if (_refreshPending || !IsHandleCreated)
                return;
            _refreshPending = true;
            BeginInvoke(new Action(() => { _refreshPending = false; RefreshView(); }));
        }

        private async Task Run(Func<SocialService, Task<string?>> action, string? success = null)
        {
            if (_server == null || SelectedPlayer == null)
                return;
            string? error = await action(_server.SocialService);
            if (error != null)
                MessageBox.Show(error, "Echo Relay: Parties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = error ?? success ?? "Done.";
            RefreshView();
        }

        public void RefreshView()
        {
            ulong? selectedId = SelectedPlayer?.Id;
            _players.BeginUpdate();
            _players.Items.Clear();
            _partyChoice.Items.Clear();
            PartyCount = 0;
            if (_server != null)
            {
                var (parties, players) = _server.SocialService.GetPartySnapshot();
                PartyCount = parties.Count;
                Dictionary<ulong, SocialService.PartyInfo> partyOf = new Dictionary<ulong, SocialService.PartyInfo>();
                foreach (var party in parties)
                    foreach (var member in party.Members)
                        partyOf[member.Id] = party;
                foreach (var player in players)
                {
                    partyOf.TryGetValue(player.Id, out var party);
                    bool leader = party != null && party.Leader.Id == player.Id;
                    ListViewItem item = new ListViewItem(new[]
                    {
                        player.Name, player.Id.ToString(),
                        party?.Leader.Name ?? "(no party)",
                        party == null ? "-" : leader ? "Leader" : "Member",
                        party == null ? "-" : $"{party.Members.Count}/{party.MaxMembers}",
                        party == null ? "-" : party.Locked ? "Yes" : "No",
                    })
                    { Tag = new PlayerRow(player.Id, player.Name, party) };
                    // A player in someone else's party is the one who sees "reserved for party leader".
                    if (party != null && !leader)
                        item.ForeColor = Color.DarkOrange;
                    _players.Items.Add(item);
                    if (player.Id == selectedId)
                        item.Selected = true;
                }
                foreach (var party in parties.OrderBy(p => p.Leader.Name))
                    _partyChoice.Items.Add(new PartyChoice(party));
                if (_partyChoice.Items.Count > 0)
                    _partyChoice.SelectedIndex = 0;
            }
            _players.EndUpdate();
            UpdateButtons();
            OnRefreshed?.Invoke();
        }

        private void UpdateButtons()
        {
            PlayerRow? player = SelectedPlayer;
            bool running = _server != null;
            _remove.Enabled = running && player?.Party != null;
            _promote.Enabled = running && player?.Party != null && player.Party.Leader.Id != player.Id;
            _disband.Enabled = running && player?.Party != null;
            _add.Enabled = running && player != null && _partyChoice.Items.Count > 0;
            _partyChoice.Enabled = running && _partyChoice.Items.Count > 0;
            _refresh.Enabled = running;
        }
    }
}
