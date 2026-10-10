using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KLink.Bot.Engine;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

internal static class FyServerHttpContractTest
{
    public static async Task<int> RunAsync()
    {
        var handler = new FyServerStubHandler();
        using var client = new FyServerHttpClient("http://fyserver.test", "test-token", handler);
        FyServerSession polled = await client.GetCurrentSessionAsync(Side.Right, includeMulliganState: false,
                                                                     expectedMatchId: 501);
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        Check(handler.PollCount == 1, "poll endpoint should be called once");
        Check(handler.MatchReadCount == 2, "match metadata should be refreshed after poll");
        Check(handler.LastOpponentId == 10, "poll should identify the opponent player");
        Check(polled.Snapshot.MatchId == 501, "match ID should be mapped");
        Check(polled.Snapshot.Turns == 4, "post-poll current turn should be used");
        Check(polled.Snapshot.NextActionId == 4 && polled.Snapshot.SendActionId == 3,
              "post-poll action counters should be used");
        Check(polled.Snapshot.ActionSessionId == 77, "packet header should provide ActionSessionId");
        Check(polled.Snapshot.Actions.Count == 3 && polled.Snapshot.Actions[2].ActionId == 3,
              "encrypted actions should be decoded in order");
        Check(polled.ActionSide == Side.Right, "current action side should be mapped");
        Check(polled.Snapshot.Cards.Count == 6, "starting cards should be mapped without duplicates");

        var action = new ServerAction(4, "XActionEndOfTurn", 20,
            new Dictionary<string, string> { ["side"] = "right", ["40"] = "20" }, 2);
        await client.SubmitActionAsync(501, polled.Snapshot.ActionSessionId, action);
        Check(handler.SubmittedAction is not null, "POST should submit an encoded action");
        Check(handler.SubmittedSessionId == 77, "POST packet header should use the session ID");
        Check(handler.SubmittedAction?.ActionId == 4 && handler.SubmittedAction.ActionType == "XActionEndOfTurn",
              "POST payload should retain action identity");
        Check(handler.SubmittedAction?.ActionData.GetValueOrDefault("40") == "20",
              "POST payload should retain action data");

        var commitHandler = new FyServerStubHandler();
        using var commitClient = new FyServerHttpClient("http://fyserver.test", "test-token", commitHandler);
        FyServerSession reconnected = await commitClient.GetCurrentSessionAsync(Side.Right, includeMulliganState: true,
                                                                                 expectedMatchId: 501);
        Check(commitHandler.ReconnectCount == 1 && commitHandler.PollCount == 0,
              "commit snapshot should use reconnect without polling");
        Check(reconnected.HasMulliganState, "reconnect should expose both mulligan states");
        Check(reconnected.Snapshot.Cards.Count(card => card.CardId == 7) == 1
              && reconnected.Snapshot.Cards.Single(card => card.CardId == 7).Location == "discard_left",
              "mulligan discard should overwrite its deck copy by card ID");

        if (failures.Count > 0)
        {
            foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
            return 1;
        }

        Console.WriteLine("fyserver HTTP contract: polling refresh, encrypted actions, ActionSessionId, POST payload, reconnect mulligan dedupe passed");
        return 0;
    }

    private sealed class FyServerStubHandler : HttpMessageHandler
    {
        private int _readCount;
        public int MatchReadCount => _readCount;
        public int PollCount { get; private set; }
        public int ReconnectCount { get; private set; }
        public int LastOpponentId { get; private set; }
        public int SubmittedSessionId { get; private set; }
        public ServerAction? SubmittedAction { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Scheme != "JWT"
                || request.Headers.Authorization.Parameter != "test-token")
            {
                return Response(HttpStatusCode.Unauthorized, "unauthorized");
            }

            string path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/matches/v2")
            {
                _readCount++;
                return JsonResponse(BuildMatchEnvelope(_readCount > 1));
            }
            if (request.Method == HttpMethod.Put && path == "/matches/v2/501/actions")
            {
                PollCount++;
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                LastOpponentId = body["opponent_id"]?.GetValue<int>() ?? 0;
                return JsonResponse(new JsonObject { ["actions"] = PacketArray() });
            }
            if (request.Method == HttpMethod.Get && path == "/matches/v2/reconnect")
            {
                ReconnectCount++;
                JsonObject response = BuildReconnectEnvelope();
                return JsonResponse(response);
            }
            if (request.Method == HttpMethod.Post && path == "/matches/v2/501/actions")
            {
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                var decoded = ActionPacketCodec.Decode(body["a"]!.GetValue<string>());
                SubmittedSessionId = decoded.ActionId;
                JsonObject data = decoded.Payload["action_data"]!.AsObject();
                SubmittedAction = new ServerAction(
                    decoded.Payload["action_id"]!.GetValue<int>(),
                    decoded.Payload["action_type"]!.GetValue<string>(),
                    decoded.Payload["player_id"]!.GetValue<int>(),
                    data.ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>() ?? ""),
                    decoded.Payload["turn_number"]!.GetValue<int>());
                return Response(HttpStatusCode.Created, "OK");
            }

            return Response(HttpStatusCode.NotFound, "missing");
        }

        private static JsonObject BuildMatchEnvelope(bool refreshed)
        {
            var match = new JsonObject
            {
                ["match_id"] = 501,
                ["current_turn"] = refreshed ? 4 : 3,
                ["current_action_id"] = refreshed ? 3 : 1,
                ["player_id_left"] = 10,
                ["player_id_right"] = 20,
                ["action_side"] = "right",
            };
            return new JsonObject
            {
                ["action_side"] = "right",
                ["match_and_starting_data"] = new JsonObject
                {
                    ["match"] = match,
                    ["starting_data"] = StartingData(),
                },
            };
        }

        private static JsonObject BuildReconnectEnvelope()
        {
            var root = new JsonObject
            {
                ["action_side"] = "right",
                ["match"] = new JsonObject
                {
                    ["match_id"] = 501,
                    ["current_turn"] = 3,
                    ["current_action_id"] = 1,
                    ["player_id_left"] = 10,
                    ["player_id_right"] = 20,
                    ["action_side"] = "right",
                },
                ["starting_data"] = StartingData(),
                ["actions"] = PacketArray(),
                ["mulligan_left"] = new JsonObject { ["discarded_cards"] = new JsonArray(Card(7, "deck_left", 6)) },
                ["mulligan_right"] = new JsonObject { ["discarded_cards"] = new JsonArray() },
            };
            return root;
        }

        private static JsonObject StartingData()
            => new()
            {
                ["player_id_left"] = 10,
                ["player_id_right"] = 20,
                ["location_card_left"] = Card(1, "board_hqleft", 0),
                ["location_card_right"] = Card(2, "board_hqright", 0),
                ["starting_hand_left"] = new JsonArray(Card(3, "hand_left", 0)),
                ["starting_hand_right"] = new JsonArray(Card(4, "hand_right", 0)),
                ["deck_left"] = new JsonArray(Card(7, "deck_left", 6)),
                ["deck_right"] = new JsonArray(Card(8, "deck_right", 6)),
            };

        private static JsonObject Card(int id, string location, int locationNumber)
            => new()
            {
                ["card_id"] = id,
                ["is_gold"] = false,
                ["location"] = location,
                ["location_number"] = locationNumber,
                ["name"] = "card_" + id,
            };

        private static JsonArray PacketArray()
        {
            var packets = new JsonArray();
            packets.Add(Packet(1, "XActionStartOfTurn", 10, new JsonObject { ["side"] = "left" }, 1));
            packets.Add(Packet(2, "XActionStartOfTurn", 20, new JsonObject { ["side"] = "right" }, 2));
            packets.Add(Packet(3, "XActionEndOfTurn", 20, new JsonObject { ["side"] = "right" }, 2));
            return packets;
        }

        private static string Packet(int id, string type, int player, JsonObject data, int turn)
        {
            var payload = new JsonObject
            {
                ["action_id"] = id,
                ["action_type"] = type,
                ["player_id"] = player,
                ["action_data"] = data,
                ["sub_actions"] = new JsonArray(),
                ["turn_number"] = turn,
            };
            return ActionPacketCodec.Encode(77, payload);
        }

        private static HttpResponseMessage JsonResponse(JsonNode value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage Response(HttpStatusCode status, string value)
            => new(status) { Content = new StringContent(value, Encoding.UTF8, "text/plain") };
    }
}
