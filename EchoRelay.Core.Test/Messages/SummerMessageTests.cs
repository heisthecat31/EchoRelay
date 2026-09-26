using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Summer;
using Newtonsoft.Json.Linq;

namespace EchoRelay.Core.Test.Messages
{
    /// <summary>
    /// Tests for the summer lobby build (rad15_summer) messages, using payloads captured from a summer client.
    /// </summary>
    public class SummerMessageTests
    {
        [Fact]
        public void SymbolHashMatchesKnownSymbols()
        {
            Assert.Equal(-3415139097788326908, Symbol.Hash("mpl_lobby_b2"));
            Assert.Equal(301069346851901302, Symbol.Hash("social_2.0"));
            Assert.Equal(unchecked((long)0xA9B30EF16760761C), Symbol.Hash("mpl_lobby_b2_summer"));
            Assert.Equal(new SummerLoginRequest().MessageTypeSymbol, Symbol.Hash("SNSLoginRequest"));
            Assert.Equal(new SummerLoginProfileResult().MessageTypeSymbol, Symbol.Hash("SNSLoginProfileResult"));
            Assert.Equal(new SummerLobbyFindSessionRequestv8().MessageTypeSymbol, Symbol.Hash("SNSLobbyFindSessionRequestv8"));
            Assert.Equal(new SummerLobbyCreateSessionRequestv7().MessageTypeSymbol, Symbol.Hash("SNSLobbyCreateSessionRequestv7"));
            Assert.Equal(new SummerLobbyPlayerSessionsRequestv3().MessageTypeSymbol, Symbol.Hash("SNSLobbyPlayerSessionsRequestv3"));
            Assert.Equal(new SummerLobbyPendingSessionCancel().MessageTypeSymbol, Symbol.Hash("SNSLobbyPendingSessionCancel"));
        }

        [Fact]
        public void XPlatformIdInequalityComparesFields()
        {
            // Summer player session requests compare a freshly decoded user id against the matching session's.
            XPlatformId a = new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9);
            XPlatformId b = new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9);
            XPlatformId? none = null;
            Assert.False(a != b);
            Assert.True(a != new XPlatformId(PlatformCode.OVR_ORG, 1));
            Assert.True(a != none);
            Assert.False(none != null);
        }

        [Fact]
        public void DecodesCapturedFindSessionRequest()
        {
            byte[] data = Convert.FromHexString("22784f9c9593ad5a76cfddcff99c2d04fffffffffffffffff8f49fa8b1d0e8c80102000000000000000000000000000000000000000000007b2267616d6574797065223a3330313036393334363835313930313330322c226170706964223a2231333639303738343039383733343032227d000400000000000000c9b000b400000000");
            SummerLobbyFindSessionRequestv8 request = new SummerLobbyFindSessionRequestv8();
            request.Decode(data);
            Assert.Equal(SummerBuild.VersionLock, request.VersionLock);
            Assert.Equal(SummerBuild.GameTypeSocial, request.GameTypeSymbol);
            Assert.Equal(-1, request.LevelSymbol);
            Assert.Equal(SummerBuild.PlatformSymbolOvr, request.PlatformSymbol);
            Assert.Equal(SummerBuild.GameTypeSocial, request.SessionSettings.GameType);
            Assert.Equal(SummerBuild.AppId, request.SessionSettings.AppId);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
        }

        [Fact]
        public void DecodesCapturedPlayerSessionsRequest()
        {
            byte[] data = Convert.FromHexString("81cc93ba01098c4eace8d7c65381a0ccf8f49fa8b1d0e8c801000000000000000400000000000000c9b000b400000000");
            SummerLobbyPlayerSessionsRequestv3 request = new SummerLobbyPlayerSessionsRequestv3();
            request.Decode(data);
            Assert.Single(request.PlayerIds);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
        }

        [Fact]
        public void DecodesCreateSessionRequest()
        {
            // region, version lock, echo_arena_private, level -1, platform, lobby type 1, 7 unmapped bytes, channel, json, user id
            byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"gametype\":691594351282457603,\"appid\":\"1369078409873402\"}\0");
            byte[] data = Convert.FromHexString("ffffffffffffffff22784f9c9593ad5a038cdbf465099909fffffffffffffffff8f49fa8b1d0e8c801000000000000000102030405060708090a0b0c0d0e0f10")
                .Concat(json).Concat(Convert.FromHexString("0400000000000000c9b000b400000000")).ToArray();
            SummerLobbyCreateSessionRequestv7 request = new SummerLobbyCreateSessionRequestv7();
            request.Decode(data);
            Assert.Equal(SummerBuild.VersionLock, request.VersionLock);
            Assert.Equal(691594351282457603, request.GameTypeSymbol);
            Assert.Equal(-1, request.LevelSymbol);
            Assert.Equal(ERGameServerStartSessionLobbyType.Private, (ERGameServerStartSessionLobbyType)(int)request.LobbyType);
            Assert.Equal(new Guid(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")), request.Channel);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
        }

        [Fact]
        public void DecodesLoginRequestAndRoundTripsProfileResult()
        {
            byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"appid\":1369078409873402,\"lobbyversion\":123,\"publisher_lock\":\"rad15_summer\"}\0");
            byte[] data = Convert.FromHexString("000000000000000000000000000000000400000000000000c9b000b400000000656e000000000000").Concat(json).ToArray();
            SummerLoginRequest request = new SummerLoginRequest();
            request.Decode(data);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
            Assert.Equal("en", request.Locale.TrimEnd('\0'));
            Assert.Equal(123UL, request.AccountInfo.LobbyVersion);
            Assert.Equal("rad15_summer", request.AccountInfo.PublisherLock);

            Guid session = Guid.NewGuid();
            SummerLoginProfileResult result = new SummerLoginProfileResult(session, request.UserId, SummerLoginProfileResult.RESULT_SUCCESS,
                new JObject { ["displayname"] = "test" }, new JObject { ["publisher_lock"] = "rad15_summer" });
            byte[] encoded = result.Encode();
            Assert.Equal(0x0B, encoded[0x24]);
            SummerLoginProfileResult decoded = new SummerLoginProfileResult();
            decoded.Decode(encoded);
            Assert.Equal(session, decoded.Session);
            Assert.Equal("test", (string?)decoded.ClientProfile["displayname"]);
            Assert.Equal("rad15_summer", (string?)decoded.ServerProfile["publisher_lock"]);
        }

        [Fact]
        public void SummerUnlockablesAreEmbedded()
        {
            Assert.True(SummerBuild.Unlockables.Count > 300);
        }

        [Fact]
        public void PacketDecodesSummerMessageTypes()
        {
            Packet packet = new Packet(new SummerLobbyPendingSessionCancel());
            Packet decoded = Packet.Decode(packet.Encode());
            Assert.IsType<SummerLobbyPendingSessionCancel>(decoded[0]);
        }

        [Fact]
        public void DecodesCapturedGlobalLeaderboardRequest()
        {
            byte[] data = Convert.FromHexString("FE796147FE9C334B0000000000000000000000000000000009000000000000004172656E6157696E50657263656E74616765004C6576656C004172656E6147616D6573506C6179656400486967686573744172656E6157696E53747265616B0000");
            SummerLeaderboardRequest request = new SummerLeaderboardRequest();
            request.Decode(data);
            Assert.Equal(0x4B339CFE476179FEUL, request.Tag);
            Assert.Equal(SummerLeaderboardRequest.SCOPE_GLOBAL, request.Scope);
            Assert.Equal(9, request.Count);
            Assert.Equal(new[] { "ArenaWinPercentage", "Level", "ArenaGamesPlayed", "HighestArenaWinStreak" }, request.StatNames);
            Assert.Empty(request.UserIds);
        }

        [Fact]
        public void DecodesCapturedUserLeaderboardRequest()
        {
            byte[] data = Convert.FromHexString("589DF2A02EE8E7A401000000000000000000000000000000FFFFFFFFFFFFFFFF4172656E6157696E50657263656E74616765004C6576656C004172656E6147616D6573506C6179656400486967686573744172656E6157696E53747265616B000001000000000000000400000000000000C9B000B400000000");
            SummerLeaderboardRequest request = new SummerLeaderboardRequest();
            request.Decode(data);
            Assert.Equal(SummerLeaderboardRequest.SCOPE_USER, request.Scope);
            Assert.Equal(-1, request.Count);
            Assert.Equal(4, request.StatNames.Length);
            Assert.Equal(new[] { new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9) }, request.UserIds);
        }

        [Fact]
        public void LeaderboardResponseRoundTrips()
        {
            JArray entries = new JArray(new JObject { ["rank"] = 1, ["displayname"] = "a", ["score"] = new JArray("Level", "50"), ["related"] = new JArray() });
            SummerLeaderboardResponse response = new SummerLeaderboardResponse(0x1234, entries);
            SummerLeaderboardResponse decoded = new SummerLeaderboardResponse();
            decoded.Decode(response.Encode());
            Assert.Equal(0x1234UL, decoded.Tag);
            Assert.True(JToken.DeepEquals(entries, decoded.Entries));
        }

        [Fact]
        public void DecodesCapturedRefreshProfile()
        {
            byte[] data = Convert.FromHexString("1AE3DDEB1AE3D02DAD195577BECC79CE0400000000000000C9B000B4000000000100000000000000");
            SummerRefreshProfile request = new SummerRefreshProfile();
            request.Decode(data);
            Assert.Equal(Guid.Parse("ebdde31a-e31a-2dd0-ad19-5577becc79ce"), request.Session);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), request.UserId);
            Assert.Equal(1UL, request.Flags);
            Assert.Equal(new SummerRefreshProfileResult().MessageTypeSymbol, Symbol.Hash("SNSRefreshProfileResult"));
            Assert.Equal(new SummerRefreshProfile().MessageTypeSymbol, Symbol.Hash("SNSRefreshProfile"));
            Assert.Equal(new SummerLeaderboardRequest().MessageTypeSymbol, Symbol.Hash("SNSLeaderboardRequest"));
            Assert.Equal(new SummerLeaderboardResponse().MessageTypeSymbol, Symbol.Hash("SNSLeaderboardResponse"));
            Assert.Equal(new SummerUpdateProfileFromServerv2().MessageTypeSymbol, Symbol.Hash("SNSUpdateProfileFromServerv2"));
        }

        [Fact]
        public void RefreshProfileResultHasSummerHeader()
        {
            SummerRefreshProfileResult result = new SummerRefreshProfileResult(new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9), SummerRefreshProfileResult.RESULT_SUCCESS, new JObject { ["displayname"] = "a" });
            byte[] data = result.Encode();
            // user id (16) | u32 | u8 result | pad3, then the null terminated profile JSON.
            Assert.Equal(0x0B, data[0x14]);
            Assert.Equal("{\"displayname\":\"a\"}\0", System.Text.Encoding.UTF8.GetString(data, 0x18, data.Length - 0x18));
        }

        [Fact]
        public void LoginFailureIsHeaderOnly()
        {
            // Summer clients show "Login authentication failed" for result 7; failures carry no profiles, but keep the
            // length field, since the client drops messages shorter than its 0x30 byte fixed header.
            SummerLoginProfileResult failure = new SummerLoginProfileResult(Guid.Empty, new XPlatformId(PlatformCode.OVR_ORG, 1), SummerLoginProfileResult.RESULT_AUTHENTICATION_FAILED, new JObject(), new JObject());
            byte[] data = failure.Encode();
            Assert.Equal(0x30, data.Length);
            Assert.Equal(7, data[0x24]);
            SummerLoginProfileResult decoded = new SummerLoginProfileResult();
            decoded.Decode(data);
            Assert.Equal(SummerLoginProfileResult.RESULT_AUTHENTICATION_FAILED, decoded.Result);
        }

        private enum ERGameServerStartSessionLobbyType { Public = 0, Private = 1, Unassigned = 2 }
    }
}
