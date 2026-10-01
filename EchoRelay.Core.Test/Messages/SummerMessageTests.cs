using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Login;
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
        public void LoginLogTextHidesTheOculusAccessToken()
        {
            SummerLoginRequest request = new SummerLoginRequest();
            request.AccountInfo.AccessToken = "FRLAsecretTokenValue123";
            string text = request.ToString();
            Assert.DoesNotContain("FRLAsecretTokenValue123", text);
            Assert.Contains("\"access_token\":\"(hidden)\"", text);

            // Clients signed out of the Oculus app send "?", which is harmless and shows as is.
            request.AccountInfo.AccessToken = "?";
            Assert.Contains("\"access_token\":\"?\"", request.ToString());
        }

        [Fact]
        public void DecodesCapturedTelemetryEvent()
        {
            // A christmas 2018 client's failed "unlock Echo Combat" purchase, as logged by a live server.
            byte[] data = Convert.FromHexString("04000000000000009FF205E200000000BDF8D9A67C18913E7B2266726F6D223A226D61696E5F6D656E75222C226170706964223A2231333639303738343039383733343032222C22736B75223A22756E6C6F636B5F6563686F5F636F6D626174222C2274696D655F7370656E745F6D73223A31327D00");
            SummerTelemetryEvent telemetry = new SummerTelemetryEvent();
            telemetry.Decode(data);
            Assert.Equal(new XPlatformId(PlatformCode.OVR_ORG, 3792040607), telemetry.UserId);
            Assert.Equal("iap_failure", telemetry.EventName);
            Assert.Equal("unlock_echo_combat", JObject.Parse(telemetry.Details).Value<string>("sku"));
            Assert.Equal(unchecked((long)0xF9BC2A364E230214), Symbol.Hash("SNSTelemetryEvent"));
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

        [Fact]
        public void ChristmasCreateSessionRequestDecodesCapturedPayload()
        {
            // A christmas party leader creating a private match for a party of two (captured from a live relay).
            byte[] data = Convert.FromHexString("FFFFFFFFFFFFFFFFC3B159E222DD78F1038CDBF465099909F8F49FA8B1D0E8C8020000000000000001000000030000007B2267616D6574797065223A3639313539343335313238323435373630332C226C6576656C223A2D317D000400000000000000F9BF4DA90000000004000000000000001C4C0B980000000000000100");
            ChristmasLobbyCreateSessionRequestv6 request = new ChristmasLobbyCreateSessionRequestv6();
            request.Decode(data);
            Assert.True(SummerBuild.IsChristmasVersionLock(request.VersionLock));
            Assert.Equal(Symbol.Hash("echo_arena_private"), request.GameTypeSymbol);
            Assert.True(SummerBuild.IsPrivateGameType(request.GameTypeSymbol));
            Assert.Equal(SummerBuild.PlatformSymbolOvr, request.PlatformSymbol);
            Assert.Equal(Server.Messages.ServerDB.ERGameServerStartSession.LobbyType.Private, request.LobbyType);
            Assert.Equal(new[] { "OVR-ORG-2840444921", "OVR-ORG-2550877212" }, request.Entrants.Select(e => e.ToString()));
            Assert.Equal("OVR-ORG-2840444921", request.UserId.ToString());
            Assert.Equal(-1, request.SessionSettings.Level);
            // echo_arena_private has a default level on christmas (the request's level is -1).
            Assert.Equal(SummerBuild.LevelArena, SummerBuild.DefaultLevelForGameType(request.GameTypeSymbol, request.VersionLock));
            Assert.Equal(new ChristmasLobbyCreateSessionRequestv6().MessageTypeSymbol, Symbol.Hash("SNSLobbyCreateSessionRequestv6"));
        }

        [Fact]
        public void RepairCombatChoicesRestoresNamesAChristmasSaveHashed()
        {
            // A real account after playing christmas: its summer combat choices (rocket, burst, heal) saved as hashes.
            JObject client = new JObject
            {
                ["weapon"] = "4743087768721687050",
                ["grenade"] = "-2076784119188203612",
                ["ability"] = "-3980269165860216900",
                ["weaponarm"] = 1,
            };
            Assert.True(EchoRelay.Core.Game.SummerBuild.RepairCombatChoices(client));
            Assert.Equal("rocket", (string?)client["weapon"]);
            Assert.Equal("burst", (string?)client["grenade"]);
            Assert.Equal("heal", (string?)client["ability"]);
            Assert.Equal(1, (int?)client["weaponarm"]);

            // Names are left alone, and an unknown hash is dropped so the game uses its default.
            JObject named = new JObject { ["weapon"] = "scout", ["grenade"] = "12345" };
            Assert.True(EchoRelay.Core.Game.SummerBuild.RepairCombatChoices(named));
            Assert.Equal("scout", (string?)named["weapon"]);
            Assert.Null(named["grenade"]);
            Assert.False(EchoRelay.Core.Game.SummerBuild.RepairCombatChoices(new JObject { ["weapon"] = "assault" }));
        }

        [Fact]
        public void DecodesHeaderlessHalloween2017Login()
        {
            // A halloween 2017 client's login: symbol, length and data, without the packet header later builds send.
            byte[] data = Convert.FromHexString("40CE0C1BBBD1ADA5A9000000000000000000000000000000000000000000000004000000000000000EE0205CFD967C5B656E0000000000007B226170706964223A313336393037383430393837333430322C22646973706C61796E616D65223A2248616C6C6F7765656E536572766572222C22636C69656E7476657273696F6E223A313530383433353932372C226163636F756E746964223A363539323330393936393438303131343139302C226E6F6E6365223A22227D00");
            Packet packet = Packet.Decode(data);
            Assert.True(packet.Headerless);
            SummerLoginRequest login = Assert.IsType<SummerLoginRequest>(Assert.Single(packet));
            Assert.Equal(6592309969480114190UL, login.UserId.AccountId);
            // Encoded back the same way: symbol first (no header), and the length it writes is what follows.
            byte[] encoded = packet.Encode(headerless: true);
            Assert.Equal(login.MessageTypeSymbol, BitConverter.ToInt64(encoded, 0));
            Assert.Equal((ulong)(encoded.Length - 16), BitConverter.ToUInt64(encoded, 8));
            Assert.Equal(Packet.HEADER_ID, BitConverter.ToUInt64(packet.Encode(), 0));
        }

        [Fact]
        public void DecodesCapturedHalloween2017FindSessionRequest()
        {
            byte[] data = Convert.FromHexString("F750E391AD7C015E12062A1932D4D241F8F49FA8B1D0E8C8010000000000000000000000000000007B2267616D6574797065223A343734333038363636393231303139313337382C226C6576656C223A2D333939323034353334343239333233363934392C22636C6F7365646C6F626279223A66616C73657D0004000000000000000EE0205CFD967C5B");
            Halloween2017LobbyFindSessionRequestv4 request = new Halloween2017LobbyFindSessionRequestv4();
            request.Decode(data);
            Assert.Equal(SummerBuild.Halloween2017VersionLock, request.VersionLock);
            Assert.True(SummerBuild.IsChristmasVersionLock(request.VersionLock));
            Assert.Equal(SummerBuild.GameTypeSocialChristmas, request.GameTypeSymbol);
            Assert.Equal(SummerBuild.LevelHalloween2017Lobby, request.SessionSettings.Level);
            Assert.Equal(6592309969480114190UL, request.UserId.AccountId);
            Assert.Equal(SummerBuild.LevelHalloween2017Lobby, SummerBuild.DefaultLevelForGameType(request.GameTypeSymbol, request.VersionLock));
            Assert.Equal(data, request.Encode());
        }

        [Fact]
        public void DecodesCapturedHalloween2017JoinSessionRequest()
        {
            // A halloween 2017 player joining a lobby by id (captured from a live relay).
            byte[] data = Convert.FromHexString("E040F4897A9F5FE8451C7E07B1E78F87F750E391AD7C015EF8F49FA8B1D0E8C801000000000000000100000000000000040000000000000042E137A1856B24530000");
            Halloween2017LobbyJoinSessionRequestv4 request = new Halloween2017LobbyJoinSessionRequestv4();
            request.Decode(data);
            Assert.Equal(new Guid(Convert.FromHexString("E040F4897A9F5FE8451C7E07B1E78F87")), request.LobbyId);
            Assert.Equal(SummerBuild.Halloween2017VersionLock, request.VersionLock);
            Assert.Equal(SummerBuild.PlatformSymbolOvr, request.PlatformSymbol);
            Assert.Equal("OVR-ORG-5991031625989218626", request.UserId.ToString());
            Assert.Equal(0, request.TeamIndex);
            Assert.Equal(data, request.Encode());
            Assert.Equal(request.LobbyId, request.ToChristmasRequest().LobbyId);
            Assert.Equal(request.MessageTypeSymbol, Symbol.Hash("SNSLobbyJoinSessionRequestv4"));
        }

        [Fact]
        public void DecodesCapturedRemoteLogSetv2()
        {
            // A halloween 2018 game server's SESSION_STARTED log (captured from a live relay).
            byte[] data = Convert.FromHexString("00000000000000002439D984F97F0000DEBFC538E260DAB2873D472F7D4E3754020000000100000001000000000000007B226D657373616765223A2253657373696F6E2053746172746564222C226D6573736167655F74797065223A2253455353494F4E5F53544152544544222C225B73657373696F6E5D5B757569645D223A227B33384335424644452D363045322D423244412D383733442D3437324637443445333735347D227D0A00");
            RemoteLogSetv2 logSet = new RemoteLogSetv2();
            logSet.Decode(data);
            Assert.Equal(Guid.Parse("38C5BFDE-60E2-B2DA-873D-472F7D4E3754"), logSet.SessionId);
            Assert.Equal(2U, logSet.LogLevel);
            Assert.Single(logSet.Logs);
            Assert.Equal("SESSION_STARTED", JObject.Parse(logSet.Logs[0])["message_type"]?.ToString());
            Assert.Equal(data, logSet.Encode());
            Assert.Equal(logSet.MessageTypeSymbol, Symbol.Hash("SNSRemoteLogSetv2"));

            // Later logs are found through the offset table.
            logSet.Logs = new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" };
            RemoteLogSetv2 decoded = new RemoteLogSetv2();
            decoded.Decode(logSet.Encode());
            Assert.Equal(logSet.Logs, decoded.Logs);
        }

        [Fact]
        public void DecodesCapturedMatchEnded()
        {
            // A halloween 2018 game server's match result (captured from a live relay).
            byte[] data = Convert.FromHexString("93F933E8E1ED6809DBAE007E054946A973AF1C7EDEA460CB6D706C5F6172656E615F61000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000074696D656C696D69745F6869740000001109000000000000020000000000000000000000000000007B22726F756E6473223A5B7B227465616D73223A5B7B2273636F7265223A307D2C7B2273636F7265223A307D5D7D5D7D006261F158A3AFDEF2174FB5DEA8EC5E3F040000000000000096457479BCDC61500000000000000000AFB90F6B5C0AEFE420403E7819F3892204000000000000004B6633B3AA69E67C010001004A617661");
            MatchEnded matchEnded = new MatchEnded();
            matchEnded.Decode(data);
            Assert.Equal(Symbol.Hash("echo_arena"), matchEnded.GameTypeSymbol);
            Assert.Equal("mpl_arena_a", matchEnded.Level);
            Assert.Equal("timelimit_hit", matchEnded.EndReason);
            Assert.Equal("{\"rounds\":[{\"teams\":[{\"score\":0},{\"score\":0}]}]}", matchEnded.Scores);
            Assert.Equal(new[] { "OVR-ORG-5792153297824794006", "OVR-ORG-8999997087218361931" }, matchEnded.Players.Select(p => p.UserId.ToString()));
            Assert.Equal(matchEnded.MessageTypeSymbol, Symbol.Hash("SNSMatchEnded"));
        }

        [Fact]
        public void DecodesCapturedProcessSkillUpdates()
        {
            // A 2017 game server's skill update for a 2v2 match (captured from a live relay).
            byte[] data = Convert.FromHexString("0100000000000000ABA4B940EC3201CB53E9D325A9809F3104000000000000007C4D3525345C194C000080DAC9C72DE3E82DC7A408933942A9F296A2FC19D756040000000000000042E137A1856B24530100000000000000793F765F5D2AE3A7901087F7445B991B04000000000000004B6633B3AA69E67C000000000000000028C6ECFC591538B1AEF2D4809F5D6514040000000000000096457479BCDC61500100000000000000");
            ProcessSkillUpdates updates = new ProcessSkillUpdates();
            updates.Decode(data);
            Assert.Equal(1UL, updates.Unk0);
            Assert.Equal(new[] { "OVR-ORG-5483515400332594556", "OVR-ORG-5991031625989218626", "OVR-ORG-8999997087218361931", "OVR-ORG-5792153297824794006" }, updates.Players.Select(p => p.UserId.ToString()));
            Assert.Equal(new ushort[] { 0, 1, 0, 1 }, updates.Players.Select(p => p.Team));
            Assert.Equal(updates.MessageTypeSymbol, Symbol.Hash("SNSProcessSkillUpdates"));
        }

        [Fact]
        public void DecodesCapturedClientLogSet()
        {
            // Two log lines from a summer client (180 bytes: two lines, padding, then the offsets 0 and 0x3B).
            byte[] data = Convert.FromHexString("FB2FA6697E2EE6AD722A95BA72B11A900400000000000000C63417E44761D05F08000000000000000200000000000000080000005B4E455447414D455D20556E6B6E6F776E206974656D20307845343345443834423930453341433741206973206265696E67206170706C696564005B4E455447414D455D20556E6B6E6F776E206974656D20307845343345443834423930453341433741206973206265696E67206170706C696564003C3F000000003B000000");
            ClientLogSet logSet = new ClientLogSet();
            logSet.Decode(data);
            Assert.Equal(8UL, logSet.Level);
            Assert.Equal(2, logSet.Lines.Length);
            Assert.All(logSet.Lines, line => Assert.Equal("[NETGAME] Unknown item 0xE43ED84B90E3AC7A is being applied", line));

            // One line from a game server (no session or user), with its trailing padding.
            data = Convert.FromHexString("C09860012FE7CAFA5D04D11567C30E1F04000000000000003500A0E55940C75904000000000000000100000000000000040000004D65737361676520686973746F72792062756666657220696E73756666696369656E742C2067726F77696E672066726F6D20333930204B4220746F20373831204B42005D00000000");
            logSet = new ClientLogSet();
            logSet.Decode(data);
            Assert.Equal(new[] { "Message history buffer insufficient, growing from 390 KB to 781 KB" }, logSet.Lines);

            // And it encodes back to the same layout.
            ClientLogSet decoded = new ClientLogSet();
            decoded.Decode(logSet.Encode());
            Assert.Equal(logSet.Lines, decoded.Lines);
        }

        [Fact]
        public void LiveStatsCombineWithStoredTotals()
        {
            JObject stored = JObject.Parse(@"{""loadout"": {""emote"": ""a""}, ""stats"": {""arena"": {
                ""Goals"": {""op"": ""add"", ""val"": 3, ""cnt"": 2},
                ""TopSpeed"": {""op"": ""max"", ""val"": 20.5, ""cnt"": 2},
                ""AveragePoints"": {""op"": ""avg"", ""val"": 4, ""cnt"": 2},
                ""Level"": {""op"": ""rep"", ""val"": 5, ""cnt"": 1}}}}");
            JObject update = JObject.Parse(@"{""stats"": {""arena"": {
                ""Goals"": {""op"": ""add"", ""val"": 2, ""cnt"": 1},
                ""TopSpeed"": {""op"": ""max"", ""val"": 18.0, ""cnt"": 1},
                ""AveragePoints"": {""op"": ""avg"", ""val"": 10, ""cnt"": 1},
                ""Level"": {""op"": ""rep"", ""val"": 6, ""cnt"": 1},
                ""Saves"": {""op"": ""add"", ""val"": 1, ""cnt"": 1}},
                ""combat"": {""Kills"": {""op"": ""add"", ""val"": 4, ""cnt"": 1}}}}");

            stored.Merge(LiveStats.CombineStats(stored, update));

            JObject arena = (JObject)stored["stats"]!["arena"]!;
            Assert.Equal(5, arena["Goals"]!.Value<long>("val"));
            Assert.Equal(3, arena["Goals"]!.Value<long>("cnt"));
            Assert.Equal(JTokenType.Integer, arena["Goals"]!["val"]!.Type);
            Assert.Equal(20.5, arena["TopSpeed"]!.Value<double>("val"));
            Assert.Equal(6.0, arena["AveragePoints"]!.Value<double>("val"), 5);
            Assert.Equal(6, arena["Level"]!.Value<long>("val"));
            Assert.Equal(1, arena["Saves"]!.Value<long>("val"));
            Assert.Equal(4, stored["stats"]!["combat"]!["Kills"]!.Value<long>("val"));
            Assert.Equal("a", stored["loadout"]!.Value<string>("emote"));
        }

        private enum ERGameServerStartSessionLobbyType { Public = 0, Private = 1, Unassigned = 2 }
    }
}
