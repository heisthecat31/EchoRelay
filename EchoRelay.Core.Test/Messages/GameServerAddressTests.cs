using EchoRelay.Core.Server.Services.ServerDB;
using System.Net;

namespace EchoRelay.Core.Test.Messages
{
    /// <summary>
    /// The game server addresses players are sent (see RegisteredGameServer.InternalAddressFor).
    /// </summary>
    public class GameServerAddressTests
    {
        private static readonly IPAddress Loopback = IPAddress.Parse("127.0.0.1");
        private static readonly IPAddress RelayPublic = IPAddress.Parse("168.119.2.92");
        private static readonly IPAddress Lan = IPAddress.Parse("192.168.0.195");
        private static readonly IPAddress RemotePlayer = IPAddress.Parse("145.224.90.142");

        [Fact]
        public void RemotePlayersDontGetALoopbackInternalAddress()
        {
            // A game server on EchoRelay's PC registers as 127.0.0.1: a remote player tried that on their own PC first
            // ("failed connect to internal host peer [127.0.0.1:6792]", "reset by host"), so they get the external address.
            Assert.Equal(RelayPublic, RegisteredGameServer.ChooseInternalAddress(Loopback, RelayPublic, RemotePlayer));
        }

        [Fact]
        public void PlayersOnTheRelayPcKeepTheLoopbackAddress()
        {
            Assert.Equal(Loopback, RegisteredGameServer.ChooseInternalAddress(Loopback, RelayPublic, Loopback));
        }

        [Fact]
        public void LanAddressesAreKept()
        {
            // A game server on another PC registers with its LAN address; players on that LAN connect to it directly.
            Assert.Equal(Lan, RegisteredGameServer.ChooseInternalAddress(Lan, IPAddress.Parse("71.15.39.215"), RemotePlayer));
        }
    }
}
