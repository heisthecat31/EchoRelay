using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EchoRelay.Core.Test.Messages
{
    /// <summary>
    /// Requested game servers are closed by the UDP port they registered with (see GameServerLauncher.StopIdle).
    /// </summary>
    public class GameServerLauncherTests
    {
        [Fact]
        public async Task PlayersCanRequestTwoGameServersEveryTenMinutes()
        {
            GameServerHosts hosts = new GameServerHosts();
            hosts.SetLocalHost("EU", "test", new[] { "summer", "halloween" }, request => new GameServerRequestResult(true, "started"));
            XPlatformId player = XPlatformId.Parse("OVR-ORG-1")!;
            XPlatformId other = XPlatformId.Parse("OVR-ORG-2")!;

            Assert.True((await hosts.Request(new GameServerRequest("summer", player, "a", null), "EU")).Accepted);
            Assert.True((await hosts.Request(new GameServerRequest("halloween", player, "a", null), "EU")).Accepted);
            GameServerRequestResult third = await hosts.Request(new GameServerRequest("summer", player, "a", null), "EU");
            Assert.False(third.Accepted);
            Assert.Contains("every 10 minutes", third.Message);

            // Other players have their own allowance.
            Assert.True((await hosts.Request(new GameServerRequest("summer", other, "b", null), "EU")).Accepted);
        }

        [Fact]
        public async Task DeclinedRequestsDontCount()
        {
            GameServerHosts hosts = new GameServerHosts();
            bool full = true;
            hosts.SetLocalHost("EU", "test", new[] { "summer" }, request => new GameServerRequestResult(!full, full ? "full" : "started"));
            XPlatformId player = XPlatformId.Parse("OVR-ORG-1")!;
            for (int i = 0; i < 3; i++)
                Assert.False((await hosts.Request(new GameServerRequest("summer", player, "a", null), "EU")).Accepted);
            full = false;
            Assert.True((await hosts.Request(new GameServerRequest("summer", player, "a", null), "EU")).Accepted);
        }

        [Fact]
        public void FindsTheProcessListeningOnAUdpPort()
        {
            if (!OperatingSystem.IsWindows())
                return;
            using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            ushort port = (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            Assert.Equal(Environment.ProcessId, GameServerLauncher.UdpPortOwner(port));
        }

        [Fact]
        public void LeavesProcessesItDidntStartAlone()
        {
            if (!OperatingSystem.IsWindows())
                return;
            // This test process owns the port, but the launcher never started it, so nothing is closed.
            using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            ushort port = (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            Assert.Equal(0, new GameServerLauncher().StopIdle(new[] { port }));
            Assert.False(Process.GetCurrentProcess().HasExited);
        }
    }
}
