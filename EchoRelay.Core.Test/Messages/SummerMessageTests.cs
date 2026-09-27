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
        public void SharedReviveIdMapsToPerNameAccounts()
        {
            // Every summer client running through Revive reports this id (0x4C01DB400B0C9 truncated to 32 bits).
            XPlatformId shared = new XPlatformId(PlatformCode.OVR_ORG, 3019944137);
            Assert.True(Server.Services.Login.LoginService.IsSharedSummerUserId(shared));
            Assert.False(Server.Services.Login.LoginService.IsSharedSummerUserId(new XPlatformId(PlatformCode.OVR_ORG, 12345)));

            // Accounts are keyed by display name: stable, case-insensitive, distinct per name, and never a real (small) Oculus id.
            XPlatformId a = Server.Services.Login.LoginService.GetSummerAccountId("PlayerOne");
            Assert.Equal(a, Server.Services.Login.LoginService.GetSummerAccountId(" playerone "));
            Assert.NotEqual(a, Server.Services.Login.LoginService.GetSummerAccountId("PlayerTwo"));
            Assert.True(a.AccountId >= 0x4000000000000000UL);
        }

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
                .Concat(json).Concat(Convert.FromHexString("0400000000000000c9b000b400000000FFFF")).ToArray();
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

        [Fact]
        public void RefreshProfileFromServerHasClientHeader()
        {
            // The client's decoder requires a 0x18 byte header (user id + 8 bytes), followed by null-terminated JSON.
            XPlatformId userId = new XPlatformId(PlatformCode.OVR_ORG, 0xB400B0C9);
            SummerRefreshProfileFromServer message = new SummerRefreshProfileFromServer(userId, new JObject { ["loadout"] = new JObject() });
            byte[] encoded = message.Encode();
            Assert.Equal(Symbol.Hash("SNSRefreshProfileFromServer"), message.MessageTypeSymbol);
            Assert.Equal(Convert.FromHexString("0400000000000000c9b000b4000000000000000000000000"), encoded.Take(0x18).ToArray());
            Assert.Equal((byte)'{', encoded[0x18]);
            Assert.Equal(0, encoded[^1]);
        }

        [Fact]
        public void DecodesCapturedProfileRequest()
        {
            // Captured from a summer client asking for another player's profile.
            Packet packet = Packet.Decode(new Packet(new UnimplementedMessage(8801406627506010498) { Data = Convert.FromHexString("FA474A682BDD04000400000000000000E8AF00B400000000") }).Encode());
            SummerProfileRequestv2 request = Assert.IsType<SummerProfileRequestv2>(packet[0]);
            Assert.Equal(Symbol.Hash("SNSProfileRequestv2"), request.MessageTypeSymbol);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 3019943912), request.UserId);
        }

        [Fact]
        public void ProfileResponseHasClientHeader()
        {
            // The client's decoder requires a 0x10 byte header (the user id), followed by null-terminated JSON.
            XPlatformId userId = new XPlatformId(PlatformCode.OVR_ORG, 3019943912);
            SummerProfileResponsev2 message = new SummerProfileResponsev2(userId, new JObject { ["loadout"] = new JObject() });
            byte[] encoded = message.Encode();
            Assert.Equal(Symbol.Hash("SNSProfileResponsev2"), message.MessageTypeSymbol);
            Assert.Equal(Convert.FromHexString("0400000000000000E8AF00B400000000"), encoded.Take(0x10).ToArray());
            Assert.Equal((byte)'{', encoded[0x10]);
            Assert.Equal(0, encoded[^1]);
        }

        [Fact]
        public void DecodesCapturedJoinSessionRequest()
        {
            // Captured from a party member following their leader into a match.
            byte[] data = Convert.FromHexString("686F47EE5353ED47D4AC2C4BA5664EB122784F9C9593AD5AF8F49FA8B1D0E8C8010000000000000003000000000000007B226170706964223A2231333639303738343039383733343032227D000400000000000000122FCEC16C697E67FFFF");
            Packet packet = Packet.Decode(new Packet(new UnimplementedMessage(3387628926720258576) { Data = data }).Encode());
            SummerLobbyJoinSessionRequestv6 request = Assert.IsType<SummerLobbyJoinSessionRequestv6>(packet[0]);
            Assert.Equal(Symbol.Hash("SNSLobbyJoinSessionRequestv6"), request.MessageTypeSymbol);
            Assert.Equal(Guid.Parse("EE476F68-5353-47ED-D4AC-2C4BA5664EB1"), request.LobbyId);
            Assert.Equal(SummerBuild.VersionLock, request.VersionLock);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 7457513948801019666), request.UserId);
            Assert.Equal(-1, request.TeamIndex);
        }

        [Fact]
        public void CreateSessionRequestReadsUserIdBeforeTeam()
        {
            // Same tail as the join request: ... json \0 | user id | i16 team.
            byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"gametype\":691594351282457603,\"appid\":\"1369078409873402\"}\0");
            byte[] data = Convert.FromHexString("ffffffffffffffff22784f9c9593ad5a038cdbf465099909fffffffffffffffff8f49fa8b1d0e8c800000100000000000000000300000000000000")
                .Concat(json).Concat(Convert.FromHexString("0400000000000000122FCEC16C697E67FFFF")).ToArray();
            SummerLobbyCreateSessionRequestv7 request = new SummerLobbyCreateSessionRequestv7();
            request.Decode(data);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 7457513948801019666), request.UserId);
            Assert.Equal(-1, request.TeamIndex);
            Assert.Equal(691594351282457603, request.SessionSettings.GameType);
        }

        [Fact]
        public void ChristmasProfileResponseHasRequestIdHeader()
        {
            // The christmas client's ProfileResponseCB reads u64 request id | user id (16) and the JSON from 0x18.
            ChristmasProfileResponse response = new ChristmasProfileResponse(0x11, new XPlatformId(PlatformCode.OVR_ORG, 1621290493), new JObject { ["loadout"] = new JObject() });
            byte[] data = response.Encode();
            Assert.Equal(0x11UL, BitConverter.ToUInt64(data, 0));
            Assert.Equal(1621290493UL, BitConverter.ToUInt64(data, 0x10));
            Assert.Equal("{\"loadout\":{}}\0", System.Text.Encoding.UTF8.GetString(data, 0x18, data.Length - 0x18));
            Assert.Equal(new ChristmasProfileResponse().MessageTypeSymbol, Symbol.Hash("SNSProfileResponse"));
        }

        [Fact]
        public void UpdateProfileFromServerReadsWithOrWithoutUnk()
        {
            XPlatformId user = new XPlatformId(PlatformCode.OVR_ORG, 1621290493);
            Guid session = Guid.NewGuid();
            byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"loadout\":{\"number\":2}}\0");
            // Halloween layout: session | user id | i64 -1 | JSON.
            byte[] withUnk = new HalloweenUpdateProfileFromServer { Session = session, UserId = user, Update = new JObject { ["loadout"] = new JObject { ["number"] = 2 } } }.Encode();
            // Without the i64: session | user id | JSON.
            byte[] withoutUnk = withUnk.Take(32).Concat(json).ToArray();
            foreach (byte[] data in new[] { withUnk, withoutUnk })
            {
                HalloweenUpdateProfileFromServer decoded = new HalloweenUpdateProfileFromServer();
                decoded.Decode(data);
                Assert.Equal(session, decoded.Session);
                Assert.Equal(1621290493UL, decoded.UserId.AccountId);
                Assert.Equal(2, decoded.Update["loadout"]!["number"]!.Value<int>());
            }
            Assert.Equal(new HalloweenUpdateProfileFromServer().MessageTypeSymbol, Symbol.Hash("SNSUpdateProfileFromServer"));
        }

        [Fact]
        public void LobbyBuildSymbolsHaveNames()
        {
            // The App showed the christmas lobby as unknown(-1752788093133717341).
            Assert.Equal("mpl_lobby_a_xmas", SummerBuild.GetKnownName(-1752788093133717341));
            Assert.Equal("mpl_lobby_a_xmas", new Server.Storage.Resources.SymbolCache().GetName(-1752788093133717341));
            Assert.Equal("social", SummerBuild.GetKnownName(4743086669210191378));
            Assert.Null(SummerBuild.GetKnownName(12345));
        }

        [Fact]
        public void ChristmasJoinSessionRequestDecodesCapturedPayload()
        {
            // A christmas party member following their leader (captured from a live relay).
            byte[] data = Convert.FromHexString("CB487EE2592DB9DC4EC6662CEC436798C3B159E222DD78F1F8F49FA8B1D0E8C80100000000000000020000000000000004000000000000001C4C0B9800000000");
            ChristmasLobbyJoinSessionRequestv5 request = new ChristmasLobbyJoinSessionRequestv5();
            request.Decode(data);
            Assert.Equal(Guid.Parse("e27e48cb-2d59-dcb9-4ec6-662cec436798"), request.LobbyId);
            Assert.True(SummerBuild.IsChristmasVersionLock(request.VersionLock));
            Assert.Equal(SummerBuild.PlatformSymbolOvr, request.PlatformSymbol);
            Assert.Equal("OVR-ORG-2550877212", request.UserId.ToString());
            Assert.Equal(new ChristmasLobbyJoinSessionRequestv5().MessageTypeSymbol, Symbol.Hash("SNSLobbyJoinSessionRequestv5"));
        }

        private enum ERGameServerStartSessionLobbyType { Public = 0, Private = 1, Unassigned = 2 }
    }
}
