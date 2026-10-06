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
        public async Task PlayersCanRequestFourGameServersEveryTenMinutes()
        {
            GameServerHosts hosts = new GameServerHosts();
            hosts.SetLocalHost("EU", "test", new[] { "summer", "halloween" }, request => new GameServerRequestResult(true, "started"));
            XPlatformId player = XPlatformId.Parse("OVR-ORG-1")!;
            XPlatformId other = XPlatformId.Parse("OVR-ORG-2")!;

            for (int i = 0; i < GameServerHosts.RequestsPerWindow; i++)
                Assert.True((await hosts.Request(new GameServerRequest(i % 2 == 0 ? "summer" : "halloween", player, "a", null), "EU")).Accepted);
            GameServerRequestResult next = await hosts.Request(new GameServerRequest("summer", player, "a", null), "EU");
            Assert.False(next.Accepted);
            Assert.Contains("every 10 minutes", next.Message);

            // Other players have their own allowance.
            Assert.True((await hosts.Request(new GameServerRequest("summer", other, "b", null), "EU")).Accepted);
        }

        [Fact]
        public async Task NewNamesFromOneAddressShareItsAllowance()
        {
            GameServerHosts hosts = new GameServerHosts();
            hosts.SetLocalHost("EU", "test", new[] { "summer" }, request => new GameServerRequestResult(true, "started"));
            IPAddress home = IPAddress.Parse("203.0.113.7");
            for (int i = 0; i < GameServerHosts.RequestsPerAddress; i++)
                Assert.True((await hosts.Request(new GameServerRequest("summer", XPlatformId.Parse($"OVR-ORG-{100 + i}")!, $"name{i}", home), "EU")).Accepted);
            GameServerRequestResult next = await hosts.Request(new GameServerRequest("summer", XPlatformId.Parse("OVR-ORG-999")!, "fresh", home), "EU");
            Assert.False(next.Accepted);
            Assert.Contains("from your network", next.Message);

            // Another address isn't affected.
            Assert.True((await hosts.Request(new GameServerRequest("summer", XPlatformId.Parse("OVR-ORG-999")!, "fresh", IPAddress.Parse("203.0.113.8")), "EU")).Accepted);
        }

        [Fact]
        public void AJoinIsTakenOnceAndAnOwnServerUntilUsed()
        {
            MatchJoins joins = new MatchJoins();
            XPlatformId player = XPlatformId.Parse("OVR-ORG-1")!;
            Guid match = Guid.NewGuid();
            Assert.Null(joins.Take(player));
            joins.Set(player, match);
            Assert.Equal(match, joins.Take(player));
            Assert.Null(joins.Take(player));

            joins.SetOwnServer(player, "halloween");
            Assert.Equal("halloween", joins.OwnServer(player));
            Assert.Equal("halloween", joins.OwnServer(player));
            joins.UsedOwnServer(player);
            Assert.Null(joins.OwnServer(player));
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
        public void NamesEachVersionAndItsLoginBuild()
        {
            Assert.Equal("Christmas 2017", GameServerBuilds.VersionName(SummerBuild.ChristmasLiveVersionLock));
            Assert.Equal("Halloween 2017", GameServerBuilds.VersionName(SummerBuild.Halloween2017VersionLock));
            Assert.Equal("Christmas 2018", GameServerBuilds.VersionName(SummerBuild.WinterVersionLock));
            Assert.Equal("Halloween 2018", GameServerBuilds.VersionName(SummerBuild.HalloweenVersionLock));
            Assert.Equal("Summer 2019", GameServerBuilds.VersionName(SummerBuild.VersionLock));
            Assert.Equal("Lone Echo", GameServerBuilds.VersionName(SummerBuild.LoneEchoVersionLock));
            // Logging in with a publisher lock leads to the build whose servers serve it.
            foreach (string publisherLock in new[] { SummerBuild.PublisherLock, SummerBuild.HalloweenPublisherLock, SummerBuild.WinterPublisherLock,
                SummerBuild.ChristmasPublisherLock, SummerBuild.Halloween2017PublisherLock })
                Assert.NotNull(GameServerBuilds.FromPublisherLock(publisherLock));
            Assert.Null(GameServerBuilds.FromPublisherLock(SummerBuild.LoneEchoPublisherLock));
            Assert.True(GameServerBuilds.ServesBuild(GameServerBuilds.FromPublisherLock(SummerBuild.ChristmasPublisherLock)!, SummerBuild.ChristmasLiveVersionLock));
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

        [Fact]
        public void ClosesGameServersStartedBeforeARestart()
        {
            if (!OperatingSystem.IsWindows())
                return;
            // A stand-in game server: a process listening on a UDP port, started by a launcher that has since restarted.
            ProcessStartInfo info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"$u = New-Object System.Net.Sockets.UdpClient 0; [Console]::WriteLine($u.Client.LocalEndPoint.Port); Start-Sleep 60\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process server = Process.Start(info)!;
            string stateFile = Path.Combine(Path.GetTempPath(), $"echorelay-launcher-test-{Guid.NewGuid():N}.json");
            try
            {
                ushort port = ushort.Parse(server.StandardOutput.ReadLine()!.Trim());
                long started = server.StartTime.ToUniversalTime().Ticks;

                // A start time that doesn't match is someone else's process reusing the id: it's left alone.
                File.WriteAllText(stateFile, $"[{{\"pid\":{server.Id},\"started\":{started - TimeSpan.TicksPerMinute},\"requester\":\"OVR-ORG-1\",\"build\":\"summer\"}}]");
                Assert.Equal(0, new GameServerLauncher().EnablePersistence(stateFile));

                File.WriteAllText(stateFile, $"[{{\"pid\":{server.Id},\"started\":{started},\"requester\":\"OVR-ORG-1\",\"build\":\"summer\"}}]");
                GameServerLauncher restarted = new GameServerLauncher();
                Assert.Equal(1, restarted.EnablePersistence(stateFile));
                Assert.Equal(1, restarted.StopIdle(new[] { port }));
                Assert.True(server.WaitForExit(5000));
                Assert.DoesNotContain(server.Id.ToString(), File.ReadAllText(stateFile));
            }
            finally
            {
                if (!server.HasExited)
                    server.Kill();
                File.Delete(stateFile);
            }
        }
    }
}
