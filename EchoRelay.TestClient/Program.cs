// A stand-in for a lobby build client (Halloween 2018 by default), for testing matchmaking without the game: it logs
// in, then plays the way the game's menu or a lobby terminal does, claims its seat in the session it gets, and stays
// connected so it keeps that seat.
//
//   EchoRelay.TestClient --relay 127.0.0.1:7777 --name Alice --password pw [--play social|arena|private-arena|none]
//                        [--join-api <match id>] [--request-api REGION] [--hold 120]
//   EchoRelay.TestClient --relay 127.0.0.1:7777 --name BoxHost --host EU --start "command"
//
// --play social         Play in the main menu: the social lobby (a find).
// --play arena          A lobby terminal's public arena match (a find).
// --play private-arena  A lobby terminal's private arena match (a create, private).
// --join-api ID         Before playing, asks the relay's API to put this player into that match ({api}/matches/join).
// --request-api REGION  Before playing, requests a Halloween 2018 game server there ({api}/servers/request) and waits
//                       30 s for it to start.
using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Login;
using EchoRelay.Core.Server.Messages.Matching;
using EchoRelay.Core.Server.Messages.Summer;
using EchoRelay.Core.Server.Services.Login;
using System.Net.WebSockets;
using System.Text;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

static string Arg(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string relay = Arg(args, "--relay", "127.0.0.1:7777");
string name = Arg(args, "--name", "TestA");
string password = Arg(args, "--password", "test-pw");
string play = Arg(args, "--play", "social");
string? joinId = Arg(args, "--join-api", "") is { Length: > 0 } j ? j : null;
int hold = int.Parse(Arg(args, "--hold", "120"));

void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{name}] {text}");

// The id EchoRelay.Patch gives a player: derived from their display name.
XPlatformId userId = LoginService.GetSummerAccountId(name);

// Each connection has a reader that queues what arrives; waiting with a timeout never touches the socket (cancelling a
// receive would abort it, and the relay would drop the player).
var inboxes = new Dictionary<ClientWebSocket, System.Threading.Channels.Channel<Message>>();

async Task<ClientWebSocket> Connect(string service)
{
    var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri($"ws://{relay}/{service}"), CancellationToken.None);
    var inbox = System.Threading.Channels.Channel.CreateUnbounded<Message>();
    inboxes[ws] = inbox;
    _ = Task.Run(async () =>
    {
        var buffer = new byte[Packet.MAX_SIZE * 4];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var data = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    data.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                foreach (var m in Packet.Decode(data.ToArray()))
                    await inbox.Writer.WriteAsync(m);
            }
        }
        catch (Exception e)
        {
            Say($"connection {service.Split('?')[0]} ended: {e.Message}");
        }
        Say($"connection {service.Split('?')[0]} closed ({ws.State})");
    });
    return ws;
}

async Task Send(ClientWebSocket ws, Message message)
{
    Say($"-> {message}");
    await ws.SendAsync(new Packet(message).Encode(), WebSocketMessageType.Binary, true, CancellationToken.None);
}

// What arrived within the timeout (waits for the first message, then takes whatever else is queued).
async Task<List<Message>> Receive(ClientWebSocket ws, TimeSpan timeout)
{
    var list = new List<Message>();
    var inbox = inboxes[ws].Reader;
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        list.Add(await inbox.ReadAsync(cts.Token));
        while (inbox.TryRead(out var more))
            list.Add(more);
    }
    catch (OperationCanceledException)
    {
    }
    foreach (var m in list)
        Say($"<- {m}");
    return list;
}

// Waits for a message of type T, printing everything on the way.
async Task<T?> Expect<T>(ClientWebSocket ws, TimeSpan timeout) where T : Message
{
    DateTime until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        foreach (var m in await Receive(ws, until - DateTime.UtcNow))
            if (m is T t)
                return t;
    }
    return null;
}

// --host REGION --start "command": a game server host as EchoRelay.Host is, on any OS: runs the command for each start
// request it accepts (the build id and the requester's name are appended), and answers stop requests by logging them.
if (Array.IndexOf(args, "--host") >= 0)
{
    string region = Arg(args, "--host", "EU");
    string command = Arg(args, "--start", "true");
    var hostSocket = new ClientWebSocket();
    await hostSocket.ConnectAsync(new Uri($"ws://{relay}/hosts"), CancellationToken.None);
    async Task SendJson(Newtonsoft.Json.Linq.JObject json)
    {
        Say($"-> {json.ToString(Newtonsoft.Json.Formatting.None)}");
        await hostSocket.SendAsync(Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);
    }
    await SendJson(new Newtonsoft.Json.Linq.JObject { ["t"] = "hello", ["region"] = region, ["name"] = name, ["builds"] = new Newtonsoft.Json.Linq.JArray("halloween") });
    var hostBuffer = new byte[65536];
    while (hostSocket.State == WebSocketState.Open)
    {
        var result = await hostSocket.ReceiveAsync(hostBuffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
            break;
        var json = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(hostBuffer, 0, result.Count));
        Say($"<- {json.ToString(Newtonsoft.Json.Formatting.None)}");
        if (json.Value<string>("t") == "start")
        {
            var start = System.Diagnostics.Process.Start("bash", new[] { "-c", $"{command} {json.Value<string>("build")} {json.Value<string>("name")}" });
            start.WaitForExit();
            await SendJson(new Newtonsoft.Json.Linq.JObject { ["t"] = "result", ["rid"] = json["rid"], ["ok"] = start.ExitCode == 0,
                ["message"] = start.ExitCode == 0 ? "Starting a game server. It takes about half a minute; then press Play in the game." : "The host couldn't start a game server." });
        }
    }
    Say("host connection closed");
    return 0;
}

// Log in, as the game does at start (the account is created on first login and locked to the password).
var login = await Connect($"login?auth={Uri.EscapeDataString(password)}&displayname={Uri.EscapeDataString(name)}");
var request = new SummerLoginRequest { UserId = userId };
request.AccountInfo.DisplayName = name;
request.AccountInfo.PublisherLock = SummerBuild.HalloweenPublisherLock;
request.AccountInfo.LobbyVersion = SummerBuild.HalloweenLobbyVersion;
await Send(login, request);
if (await Expect<LoginSuccess>(login, TimeSpan.FromSeconds(10)) == null)
{
    Say("RESULT login failed");
    return 1;
}
// Let the rest of the login (settings, profile) arrive.
await Receive(login, TimeSpan.FromSeconds(2));
await Receive(login, TimeSpan.FromSeconds(1));
Say("RESULT logged in");

if (joinId != null)
{
    using var http = new HttpClient();
    string body = $"{{\"id\":\"{joinId}\",\"displayname\":\"{name}\",\"password\":\"{password}\"}}";
    var reply = await http.PostAsync($"http://{relay}/api/matches/join", new StringContent(body, Encoding.UTF8, "application/json"));
    Say($"RESULT join-api {await reply.Content.ReadAsStringAsync()}");
}

string? requestRegion = Arg(args, "--request-api", "") is { Length: > 0 } r ? r : null;
if (requestRegion != null)
{
    using var http = new HttpClient();
    string body = $"{{\"build\":\"halloween\",\"region\":\"{requestRegion}\",\"displayname\":\"{name}\",\"password\":\"{password}\"}}";
    var reply = await http.PostAsync($"http://{relay}/api/servers/request", new StringContent(body, Encoding.UTF8, "application/json"));
    string text = await reply.Content.ReadAsStringAsync();
    Say($"RESULT request-api {text}");
    if (text.Contains("\"ok\":true"))
        await Task.Delay(TimeSpan.FromSeconds(30));
}

if (play == "none")
{
    await Task.Delay(TimeSpan.FromSeconds(hold));
    return 0;
}

var matching = await Connect("matching");
long gameType = play == "social" ? SummerBuild.GameTypeSocial : play == "arena" ? SummerBuild.GameTypeArena : Symbol.Hash("echo_arena_private");
if (play == "private-arena")
{
    await Send(matching, new SummerLobbyCreateSessionRequestv7
    {
        VersionLock = SummerBuild.HalloweenVersionLock,
        GameTypeSymbol = gameType,
        LevelSymbol = -1,
        LobbyTypeValue = 1,
        SessionSettings = new SessionSettings("1369078409873402", gameType, null),
        UserId = userId,
    });
}
else
{
    await Send(matching, new SummerLobbyFindSessionRequestv8
    {
        VersionLock = SummerBuild.HalloweenVersionLock,
        GameTypeSymbol = gameType,
        LevelSymbol = -1,
        SessionSettings = new SessionSettings("1369078409873402", gameType, null),
        UserId = userId,
    });
}

// The relay holds the reply until the game server has loaded the session.
DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
LobbySessionSuccessv4? success = null;
while (success == null && DateTime.UtcNow < deadline)
{
    foreach (var m in await Receive(matching, deadline - DateTime.UtcNow))
    {
        if (m is LobbySessionSuccessv4 s)
            success = s;
        else if (m.GetType().Name.StartsWith("LobbySessionFailure"))
        {
            Say($"RESULT matchmaking failed: {m}");
            return 2;
        }
    }
}
if (success == null)
{
    Say("RESULT no session within 60 s");
    return 3;
}
Say($"RESULT matched session={success.MatchingSession} lobby_type_byte={success.Unk1 & 0xFF} endpoint={success.Endpoint}");

// Claim the seat, as the game does once it has the session.
await Send(matching, new SummerLobbyPlayerSessionsRequestv3 { MatchingSession = success.MatchingSession, PlayerIds = new[] { userId } });
await Receive(matching, TimeSpan.FromSeconds(5));

{
    using var http = new HttpClient();
    string body = $"{{\"displayname\":\"{name}\",\"password\":\"{password}\"}}";
    var reply = await http.PostAsync($"http://{relay}/api/matches/current", new StringContent(body, Encoding.UTF8, "application/json"));
    Say($"RESULT current {await reply.Content.ReadAsStringAsync()}");
}
Say($"holding {hold} s");
DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(hold);
while (DateTime.UtcNow < end)
{
    await Receive(login, TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, (end - DateTime.UtcNow).TotalSeconds))));
    await Receive(matching, TimeSpan.FromMilliseconds(100));
}
Say("done");
return 0;
