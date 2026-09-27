using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Matching;
using EchoRelay.Core.Server.Messages.Summer;
using Newtonsoft.Json.Linq;

namespace EchoRelay.Core.Test.Messages
{
    /// <summary>
    /// Tests for the halloween lobby build (rad15_halloween) messages, which are the original (v1) versions of some summer ones.
    /// </summary>
    public class HalloweenMessageTests
    {
        [Fact]
        public void SymbolsMatchTheHalloweenBuildsMessageNames()
        {
            Assert.Equal(Symbol.Hash("SNSProfileRequest"), new HalloweenProfileRequest().MessageTypeSymbol);
            Assert.Equal(Symbol.Hash("SNSProfileResponse"), new HalloweenProfileResponse().MessageTypeSymbol);
            Assert.Equal(Symbol.Hash("SNSUpdateProfileFromServer"), new HalloweenUpdateProfileFromServer().MessageTypeSymbol);
            Assert.Equal(Symbol.Hash("SNSLoginClientSettings"), new HalloweenLoginClientSettings().MessageTypeSymbol);
            // Halloween has no SNSLobbySessionFailurev3, so its clients get v2.
            Assert.Equal(Symbol.Hash("SNSLobbySessionFailurev2"), new LobbySessionFailurev2().MessageTypeSymbol);
        }

        [Fact]
        public void V1MessagesAreRegisteredSeparatelyFromV2()
        {
            Assert.IsType<HalloweenProfileRequest>(MessageTypes.CreateMessage(Symbol.Hash("SNSProfileRequest")));
            Assert.IsType<SummerProfileRequestv2>(MessageTypes.CreateMessage(Symbol.Hash("SNSProfileRequestv2")));
        }

        [Fact]
        public void DecodesCapturedProfileRequest()
        {
            // Sent by a halloween client for its own profile while building its menu (unanswered, it stays on a black screen).
            byte[] data = Convert.FromHexString("00000000000000000400000000000000C9B000B400000000");
            HalloweenProfileRequest request = new HalloweenProfileRequest();
            request.Decode(data);
            Assert.Equal(0UL, request.Unk0);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
        }

        [Fact]
        public void ProfileResponseRoundTrips()
        {
            XPlatformId user = new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9);
            HalloweenProfileResponse response = new HalloweenProfileResponse(user, new JObject { ["displayname"] = "Cat", ["publisher_lock"] = SummerBuild.HalloweenPublisherLock });
            HalloweenProfileResponse decoded = new HalloweenProfileResponse();
            decoded.Decode(response.Encode());
            Assert.Equal(user, decoded.UserId);
            Assert.Equal("rad15_halloween", (string?)decoded.Profile["publisher_lock"]);
        }

        [Fact]
        public void UnlockAllProfileFitsTheHalloweenMessageLimit()
        {
            // The halloween client disconnects from the relay on a login connection message over 16 KiB. Its profiles carry
            // one unlock list, on its own (smaller) default profile, where summer's carry two lists (21 KB with unlock all).
            JObject profile = SummerBuild.HalloweenDefaultServerProfile;
            Assert.True(profile.ContainsKey("loadout"));
            Assert.False(profile.ContainsKey("unlocks_combat"));
            profile["unlocks"] = new JArray(SummerBuild.Unlockables);
            profile["displayname"] = "a display name of some length";
            profile["xplatformid"] = "OVR-ORG-3019944137";
            profile["publisher_lock"] = SummerBuild.HalloweenPublisherLock;
            profile["profile_stats"] = new JObject { ["Level"] = new JObject { ["op"] = "add", ["val"] = 50, ["cnt"] = 1 } };
            byte[] encoded = new HalloweenProfileResponse(new XPlatformId(PlatformCode.OVR_ORG, 3019944137), profile).Encode();
            Assert.True(encoded.Length < SummerBuild.HalloweenMaxMessageSize - 1024, $"{encoded.Length} bytes");
        }
    }
}
