using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Social;
using EchoRelay.Core.Server.Storage.Types;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

namespace EchoRelay.Core.Server.Services.Social
{
    /// <summary>
    /// The social service backs the summer build's parties and friends list. The game does those through the Oculus Platform
    /// SDK (Oculus Rooms, room invites, room packets and the Oculus friends list); EchoRelay.Patch (dbgcore.dll) answers those
    /// SDK calls and forwards them here as JSON (<see cref="SocialMessage"/>).
    ///
    /// Friends: every other player connected to this service (i.e. online) is a friend.
    /// Parties: private rooms with an owner, members, a key/value data store (the game keeps its party state there), a lock,
    /// and invites. Room members exchange packets through the service.
    /// </summary>
    public class SocialService : Service
    {
        #region Classes
        /// <summary>
        /// A connected player.
        /// </summary>
        private class SocialUser
        {
            public ulong Id;
            public string Name = "";
            public Peer Peer = null!;
            public ulong? RoomId;
        }

        /// <summary>
        /// A party (an emulated Oculus room).
        /// </summary>
        private class Room
        {
            public ulong Id;
            public ulong OwnerId;
            public List<ulong> Members = new List<ulong>();
            public int MaxUsers;
            public bool Locked;
            public Dictionary<string, string> Data = new Dictionary<string, string>();
        }

        /// <summary>
        /// A pending room invite.
        /// </summary>
        private class Invite
        {
            public ulong Id;
            public ulong RoomId;
            public ulong FromId;
            public ulong ToId;
            public long SentTime;
        }
        #endregion

        #region Fields
        private readonly object _lock = new object();
        private readonly Dictionary<Peer, SocialUser> _users = new Dictionary<Peer, SocialUser>();
        private readonly Dictionary<ulong, Room> _rooms = new Dictionary<ulong, Room>();
        private readonly List<Invite> _invites = new List<Invite>();

        /// <summary>
        /// Oculus error codes the game shows (e.g. "room is full"), used for failed requests.
        /// </summary>
        private const int ERROR_NOT_FOUND = 10;
        private const int ERROR_FULL = 11;
        private const int ERROR_LOCKED = 12;
        private const int ERROR_NOT_ALLOWED = 13;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="SocialService"/> with the provided arguments.
        /// </summary>
        /// <param name="server">The server which this service is bound to.</param>
        public SocialService(Server server) : base(server, "SOCIAL")
        {
            OnPeerDisconnected += SocialService_OnPeerDisconnected;
        }
        #endregion

        #region Functions
        /// <summary>
        /// Obtains the other members of a player's party (by user id), e.g. to keep a party on one team.
        /// </summary>
        /// <param name="userId">The player's user id.</param>
        /// <returns>The user ids of the other party members, or none if the player isn't in a party.</returns>
        public ulong[] GetPartyMembers(ulong userId)
        {
            lock (_lock)
            {
                Room? room = _rooms.Values.FirstOrDefault(r => r.Members.Contains(userId));
                return room == null ? Array.Empty<ulong>() : room.Members.Where(id => id != userId).ToArray();
            }
        }

        #region Administration
        /// <summary>
        /// A player online on the social service, for administration.
        /// </summary>
        public record PartyPlayer(ulong Id, string Name, bool Online);

        /// <summary>
        /// A party, for administration.
        /// </summary>
        public record PartyInfo(ulong Id, PartyPlayer Leader, IReadOnlyList<PartyPlayer> Members, int MaxMembers, bool Locked);

        /// <summary>
        /// Fired when parties change (a player joins, leaves, is promoted, ...), so an admin view can refresh.
        /// </summary>
        public event Action? OnPartiesChanged;

        /// <summary>
        /// Obtains every party and the online players, for administration.
        /// </summary>
        public (IReadOnlyList<PartyInfo> Parties, IReadOnlyList<PartyPlayer> Players) GetPartySnapshot()
        {
            lock (_lock)
            {
                PartyPlayer Player(ulong id)
                {
                    SocialUser? user = _users.Values.FirstOrDefault(u => u.Id == id);
                    return new PartyPlayer(id, user?.Name ?? id.ToString(), user != null);
                }
                List<PartyInfo> parties = _rooms.Values
                    .Select(room => new PartyInfo(room.Id, Player(room.OwnerId), room.Members.Select(Player).ToList(), room.MaxUsers, room.Locked))
                    .ToList();
                List<PartyPlayer> players = _users.Values.Select(u => new PartyPlayer(u.Id, u.Name, true)).OrderBy(p => p.Name).ToList();
                return (parties, players);
            }
        }

        /// <summary>
        /// Removes a player from their party (as the party leader's kick does). Their game gets the party without them; a
        /// removed leader hands leadership to the next member.
        /// </summary>
        /// <returns>An error message, or null on success.</returns>
        public async Task<string?> AdminRemoveFromParty(ulong userId)
        {
            List<(Peer, JObject)> outgoing = new List<(Peer, JObject)>();
            lock (_lock)
            {
                Room? room = _rooms.Values.FirstOrDefault(r => r.Members.Contains(userId));
                if (room == null)
                    return "That player isn't in a party.";
                SocialUser? user = _users.Values.FirstOrDefault(u => u.Id == userId);
                room.Members.Remove(userId);
                if (user != null)
                {
                    user.RoomId = null;
                    outgoing.Add((user.Peer, new JObject { ["t"] = "note", ["kind"] = "roomupdate", ["room"] = EmptyRoomJson(room.Id) }));
                }
                if (room.Members.Count == 0)
                {
                    _rooms.Remove(room.Id);
                    _invites.RemoveAll(invite => invite.RoomId == room.Id);
                }
                else
                {
                    if (room.OwnerId == userId)
                        room.OwnerId = room.Members[0];
                    NotifyRoomUpdate(room, userId, outgoing);
                }
            }
            await SendAll(outgoing);
            OnPartiesChanged?.Invoke();
            return null;
        }

        /// <summary>
        /// Makes a party member the party's leader.
        /// </summary>
        /// <returns>An error message, or null on success.</returns>
        public async Task<string?> AdminPromote(ulong userId)
        {
            List<(Peer, JObject)> outgoing = new List<(Peer, JObject)>();
            lock (_lock)
            {
                Room? room = _rooms.Values.FirstOrDefault(r => r.Members.Contains(userId));
                if (room == null)
                    return "That player isn't in a party.";
                room.OwnerId = userId;
                NotifyRoomUpdate(room, 0, outgoing);
            }
            await SendAll(outgoing);
            OnPartiesChanged?.Invoke();
            return null;
        }

        /// <summary>
        /// Adds a player to a party. The game only moves between parties through its own join, so the player gets an invite
        /// from the party (shown in game, from its leader) that works even if the party is locked or full.
        /// </summary>
        /// <returns>An error message, or null on success.</returns>
        public async Task<string?> AdminInviteToParty(ulong userId, ulong partyId)
        {
            List<(Peer, JObject)> outgoing = new List<(Peer, JObject)>();
            lock (_lock)
            {
                if (!_rooms.TryGetValue(partyId, out Room? room))
                    return "That party no longer exists.";
                SocialUser? user = _users.Values.FirstOrDefault(u => u.Id == userId);
                if (user == null)
                    return "That player isn't online.";
                if (room.Members.Contains(userId))
                    return "That player is already in the party.";
                // An invite gets past the lock; make room for them too.
                room.MaxUsers = Math.Max(room.MaxUsers, room.Members.Count + 1);
                Invite invite = new Invite { Id = NewId(), RoomId = partyId, FromId = room.OwnerId, ToId = userId, SentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                _invites.RemoveAll(existing => existing.RoomId == partyId && existing.ToId == userId);
                _invites.Add(invite);
                outgoing.Add((user.Peer, new JObject { ["t"] = "note", ["kind"] = "invite", ["invite"] = InviteJson(invite) }));
            }
            await SendAll(outgoing);
            return null;
        }

        /// <summary>
        /// Disbands a party: every member is removed from it.
        /// </summary>
        /// <returns>An error message, or null on success.</returns>
        public async Task<string?> AdminDisband(ulong partyId)
        {
            ulong[] members;
            lock (_lock)
            {
                if (!_rooms.TryGetValue(partyId, out Room? room))
                    return "That party no longer exists.";
                members = room.Members.ToArray();
            }
            foreach (ulong member in members)
                await AdminRemoveFromParty(member);
            return null;
        }
        #endregion

        private void SocialService_OnPeerDisconnected(Service service, Peer peer)
        {
            // A disconnected player leaves their party and stops being online.
            List<(Peer, JObject)> outgoing = new List<(Peer, JObject)>();
            lock (_lock)
            {
                if (!_users.TryGetValue(peer, out SocialUser? user))
                    return;
                if (user.RoomId != null)
                    LeaveRoom(user, outgoing);
                _users.Remove(peer);
                _invites.RemoveAll(invite => invite.ToId == user.Id);
            }
            _ = SendAll(outgoing);
            OnPartiesChanged?.Invoke();
        }

        protected override async Task HandlePacket(Peer sender, Packet packet)
        {
            foreach (Message message in packet)
            {
                if (message is not SocialMessage socialMessage)
                    continue;

                List<(Peer, JObject)> outgoing = new List<(Peer, JObject)>();
                try
                {
                    lock (_lock)
                    {
                        HandleSocialMessage(sender, socialMessage.Data, outgoing);
                    }
                }
                catch (Exception ex)
                {
                    // Reply with an error rather than dropping the request, so the game doesn't wait forever.
                    ulong? rid = socialMessage.Data.Value<ulong?>("rid");
                    if (rid != null)
                        outgoing.Add((sender, Error(rid.Value, ERROR_NOT_ALLOWED, ex.Message)));
                }
                await SendAll(outgoing);
                if (socialMessage.Data.Value<string>("t") != "pkt")
                    OnPartiesChanged?.Invoke();
            }
        }

        private async Task SendAll(List<(Peer peer, JObject data)> outgoing)
        {
            foreach (var (peer, data) in outgoing)
            {
                try
                {
                    await peer.Send(new SocialMessage(data));
                }
                catch
                {
                    // The peer disconnected; its disconnect handler cleans up.
                }
            }
        }

        private void HandleSocialMessage(Peer sender, JObject data, List<(Peer, JObject)> outgoing)
        {
            string type = data.Value<string>("t") ?? "";

            // Identify the player first.
            if (type == "hello")
            {
                ulong id = data.Value<ulong>("id");
                string name = ResolveName(sender, id);
                _users[sender] = new SocialUser { Id = id, Name = name, Peer = sender };
                sender.UpdateUserAuthentication(new XPlatformId(PlatformCode.OVR_ORG, id), name);
                outgoing.Add((sender, new JObject { ["t"] = "welcome", ["id"] = id, ["name"] = name }));
                return;
            }
            if (!_users.TryGetValue(sender, out SocialUser? user))
                return;

            if (type == "pkt")
            {
                RelayPacket(user, data, outgoing);
                return;
            }
            if (type != "req")
                return;

            ulong rid = data.Value<ulong>("rid");
            string op = data.Value<string>("op") ?? "";
            JObject result;
            switch (op)
            {
                case "friends":
                    // Everyone else online is a friend.
                    result = Ok(rid);
                    result["users"] = new JArray(_users.Values.Where(other => other.Id != user.Id).Select(UserJson));
                    break;

                case "invitable":
                    // Online players who aren't already in the requester's party.
                    Room? current = user.RoomId != null && _rooms.TryGetValue(user.RoomId.Value, out Room? r) ? r : null;
                    result = Ok(rid);
                    result["users"] = new JArray(_users.Values.Where(other => other.Id != user.Id && (current == null || !current.Members.Contains(other.Id))).Select(UserJson));
                    break;

                case "create":
                {
                    if (user.RoomId != null)
                        LeaveRoom(user, outgoing);
                    Room room = new Room { Id = NewId(), OwnerId = user.Id, MaxUsers = Math.Max(1, data.Value<int?>("max") ?? 8) };
                    room.Members.Add(user.Id);
                    _rooms[room.Id] = room;
                    user.RoomId = room.Id;
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                case "join":
                {
                    ulong roomId = data.Value<ulong>("room");
                    if (!_rooms.TryGetValue(roomId, out Room? room))
                    {
                        result = Error(rid, ERROR_NOT_FOUND, "Party not found");
                        break;
                    }
                    if (!room.Members.Contains(user.Id))
                    {
                        bool invited = _invites.Any(invite => invite.RoomId == roomId && invite.ToId == user.Id);
                        if (room.Locked && !invited)
                        {
                            result = Error(rid, ERROR_LOCKED, "Party is locked");
                            break;
                        }
                        if (room.Members.Count >= room.MaxUsers)
                        {
                            result = Error(rid, ERROR_FULL, "Party is full");
                            break;
                        }
                        if (user.RoomId != null && user.RoomId != roomId)
                            LeaveRoom(user, outgoing);
                        room.Members.Add(user.Id);
                        user.RoomId = room.Id;
                        _invites.RemoveAll(invite => invite.RoomId == roomId && invite.ToId == user.Id);
                        NotifyRoomUpdate(room, user.Id, outgoing);
                    }
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                case "leave":
                {
                    ulong roomId = data.Value<ulong>("room");
                    Room? room = _rooms.GetValueOrDefault(roomId);
                    bool member = room != null && user.RoomId == roomId;
                    if (member)
                        LeaveRoom(user, outgoing);
                    result = Ok(rid);
                    // Someone who was kicked (or removed) and then leaves gets an empty party, not the one they're out of.
                    result["room"] = room != null && member ? RoomJson(room) : EmptyRoomJson(roomId);
                    break;
                }

                case "get":
                {
                    ulong roomId = data.Value<ulong>("room");
                    result = _rooms.TryGetValue(roomId, out Room? room) ? Ok(rid) : Error(rid, ERROR_NOT_FOUND, "Party not found");
                    if (room != null)
                        result["room"] = RoomJson(room);
                    break;
                }

                case "invite":
                {
                    ulong roomId = data.Value<ulong>("room");
                    ulong.TryParse(data.Value<string>("token"), out ulong toId);
                    SocialUser? target = _users.Values.FirstOrDefault(other => other.Id == toId);
                    if (!_rooms.TryGetValue(roomId, out Room? room) || target == null)
                    {
                        result = Error(rid, ERROR_NOT_FOUND, "Player is not online");
                        break;
                    }
                    Invite invite = new Invite { Id = NewId(), RoomId = roomId, FromId = user.Id, ToId = toId, SentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    _invites.RemoveAll(existing => existing.RoomId == roomId && existing.ToId == toId);
                    _invites.Add(invite);
                    outgoing.Add((target.Peer, new JObject { ["t"] = "note", ["kind"] = "invite", ["invite"] = InviteJson(invite) }));
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                case "invites":
                    result = Ok(rid);
                    result["invites"] = new JArray(_invites.Where(invite => invite.ToId == user.Id && _rooms.ContainsKey(invite.RoomId)).Select(InviteJson));
                    break;

                case "markread":
                {
                    ulong inviteId = data.Value<ulong>("id");
                    _invites.RemoveAll(invite => invite.Id == inviteId && invite.ToId == user.Id);
                    result = Ok(rid);
                    break;
                }

                case "kick":
                {
                    ulong roomId = data.Value<ulong>("room");
                    ulong kickId = data.Value<ulong>("user");
                    if (!_rooms.TryGetValue(roomId, out Room? room) || room.OwnerId != user.Id)
                    {
                        result = Error(rid, ERROR_NOT_ALLOWED, "Only the party leader can kick");
                        break;
                    }
                    SocialUser? kicked = _users.Values.FirstOrDefault(other => other.Id == kickId);
                    if (room.Members.Remove(kickId))
                    {
                        if (kicked != null)
                        {
                            // The kicked player's party is now empty: the christmas client takes the party it's sent as the
                            // one it's in, so sending the leader's party (without them) left it "still in it".
                            kicked.RoomId = null;
                            outgoing.Add((kicked.Peer, new JObject { ["t"] = "note", ["kind"] = "roomupdate", ["room"] = EmptyRoomJson(roomId) }));
                        }
                        NotifyRoomUpdate(room, user.Id, outgoing);
                    }
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                case "owner":
                {
                    ulong roomId = data.Value<ulong>("room");
                    ulong newOwner = data.Value<ulong>("user");
                    if (!_rooms.TryGetValue(roomId, out Room? room) || room.OwnerId != user.Id || !room.Members.Contains(newOwner))
                    {
                        result = Error(rid, ERROR_NOT_ALLOWED, "Only the party leader can pass leadership");
                        break;
                    }
                    room.OwnerId = newOwner;
                    NotifyRoomUpdate(room, user.Id, outgoing);
                    // The requester gets an update too: the SDK's UpdateOwner response has no room.
                    outgoing.Add((sender, new JObject { ["t"] = "note", ["kind"] = "roomupdate", ["room"] = RoomJson(room) }));
                    result = Ok(rid);
                    break;
                }

                case "lock":
                {
                    ulong roomId = data.Value<ulong>("room");
                    if (!_rooms.TryGetValue(roomId, out Room? room))
                    {
                        result = Error(rid, ERROR_NOT_FOUND, "Party not found");
                        break;
                    }
                    room.Locked = data.Value<bool>("locked");
                    NotifyRoomUpdate(room, user.Id, outgoing);
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                case "data":
                {
                    ulong roomId = data.Value<ulong>("room");
                    if (!_rooms.TryGetValue(roomId, out Room? room))
                    {
                        result = Error(rid, ERROR_NOT_FOUND, "Party not found");
                        break;
                    }
                    if (data["data"] is JObject values)
                        foreach (var property in values.Properties())
                            room.Data[property.Name] = property.Value.ToString();
                    NotifyRoomUpdate(room, user.Id, outgoing);
                    result = Ok(rid);
                    result["room"] = RoomJson(room);
                    break;
                }

                default:
                    result = Error(rid, ERROR_NOT_ALLOWED, $"Unsupported operation '{op}'");
                    break;
            }
            outgoing.Add((sender, result));
        }

        /// <summary>
        /// Relays a room packet to the sender's party members (or one member).
        /// </summary>
        private void RelayPacket(SocialUser user, JObject data, List<(Peer, JObject)> outgoing)
        {
            if (user.RoomId == null || !_rooms.TryGetValue(user.RoomId.Value, out Room? room))
                return;
            ulong to = data.Value<ulong?>("to") ?? 0;
            JObject packet = new JObject { ["t"] = "pkt", ["from"] = user.Id, ["data"] = data["data"] };
            foreach (SocialUser member in _users.Values)
            {
                if (member.Id == user.Id || !room.Members.Contains(member.Id))
                    continue;
                if (to != 0 && member.Id != to)
                    continue;
                outgoing.Add((member.Peer, packet));
            }
        }

        /// <summary>
        /// Removes a player from their party, handing leadership on or closing the party if they were the last member.
        /// </summary>
        private void LeaveRoom(SocialUser user, List<(Peer, JObject)> outgoing)
        {
            if (user.RoomId == null)
                return;
            ulong roomId = user.RoomId.Value;
            user.RoomId = null;
            if (!_rooms.TryGetValue(roomId, out Room? room))
                return;
            room.Members.Remove(user.Id);
            if (room.Members.Count == 0)
            {
                _rooms.Remove(roomId);
                _invites.RemoveAll(invite => invite.RoomId == roomId);
                return;
            }
            if (room.OwnerId == user.Id)
                room.OwnerId = room.Members[0];
            NotifyRoomUpdate(room, user.Id, outgoing);
        }

        /// <summary>
        /// Sends a room update notification to every member of a room except one (the requester, who gets a response instead).
        /// </summary>
        private void NotifyRoomUpdate(Room room, ulong exceptId, List<(Peer, JObject)> outgoing)
        {
            JObject note = new JObject { ["t"] = "note", ["kind"] = "roomupdate", ["room"] = RoomJson(room) };
            foreach (SocialUser member in _users.Values)
                if (member.Id != exceptId && room.Members.Contains(member.Id))
                    outgoing.Add((member.Peer, note));
        }

        private string ResolveName(Peer peer, ulong id)
        {
            XPlatformId userId = Server.LoginService.ResolveSummerAccount(new XPlatformId(PlatformCode.OVR_ORG, id), null, peer.Address);
            AccountResource? account = Storage.Accounts.Get(userId);
            return account?.Profile.Server.DisplayName ?? account?.Profile.Client.DisplayName ?? userId.ToString();
        }

        private JObject UserJson(SocialUser user)
        {
            // The player's social connection can come up before their login has created their account, so refresh a
            // placeholder name (their id) until the account exists.
            if (user.Name == new XPlatformId(PlatformCode.OVR_ORG, user.Id).ToString())
                user.Name = ResolveName(user.Peer, user.Id);
            return new JObject { ["id"] = user.Id, ["name"] = user.Name, ["online"] = true, ["presence"] = user.RoomId != null ? "In a party" : "Online" };
        }

        private JObject UserJson(ulong id)
        {
            SocialUser? user = _users.Values.FirstOrDefault(other => other.Id == id);
            return user != null ? UserJson(user) : new JObject { ["id"] = id, ["name"] = id.ToString(), ["online"] = false, ["presence"] = "" };
        }

        private JObject RoomJson(Room room)
        {
            return new JObject
            {
                ["id"] = room.Id,
                ["owner"] = UserJson(room.OwnerId),
                ["users"] = new JArray(room.Members.Select(UserJson)),
                ["max"] = room.MaxUsers,
                ["locked"] = room.Locked,
                ["data"] = JObject.FromObject(room.Data),
            };
        }

        private static JObject EmptyRoomJson(ulong roomId)
        {
            return new JObject { ["id"] = roomId, ["owner"] = null, ["users"] = new JArray(), ["max"] = 0, ["locked"] = false, ["data"] = new JObject() };
        }

        private static JObject InviteJson(Invite invite)
        {
            return new JObject { ["id"] = invite.Id, ["room"] = invite.RoomId, ["from"] = invite.FromId, ["sent"] = invite.SentTime };
        }

        private static JObject Ok(ulong rid)
        {
            return new JObject { ["t"] = "res", ["rid"] = rid, ["ok"] = true };
        }

        private static JObject Error(ulong rid, int code, string message)
        {
            return new JObject { ["t"] = "res", ["rid"] = rid, ["ok"] = false, ["code"] = code, ["msg"] = message };
        }

        /// <summary>
        /// Generates a room/invite id. Kept below 2^53 so it survives JSON number handling everywhere.
        /// </summary>
        private static ulong NewId()
        {
            return (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x001FFFFFFFFFFFFFUL) | 1;
        }
        #endregion
    }
}
