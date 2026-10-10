using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using KLink.Bot.Engine;
using KLink.Bot.Server;

namespace KLink.Bot.ServerBridgeTest;

internal sealed record FyServerSession(
    ServerMatchSnapshot Snapshot,
    Side ActionSide,
    bool HasMulliganState);

/// <summary>fyserver action polling and explicit action submission.</summary>
internal sealed class FyServerHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public FyServerHttpClient(string baseUrl, string token)
    {
        _baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + '/', UriKind.Absolute);
        _http = new HttpClient();
        _http.BaseAddress = _baseUri;
        string rawToken = token.Trim();
        if (rawToken.StartsWith("JWT ", StringComparison.OrdinalIgnoreCase)) rawToken = rawToken[4..].Trim();
        else if (rawToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) rawToken = rawToken[7..].Trim();
        if (rawToken.Length == 0) throw new ArgumentException("JWT token is empty.", nameof(token));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("JWT", rawToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<FyServerSession> GetCurrentSessionAsync(Side botSide, bool includeMulliganState,
                                                               int? expectedMatchId = null,
                                                               CancellationToken cancellationToken = default)
    {
        JsonObject root = await GetObjectAsync("matches/v2", cancellationToken);
        (JsonObject match, JsonObject startingData) = ExtractMatch(root);
        int matchId = Int(match, "match_id");
        if (expectedMatchId is not null && expectedMatchId.Value != matchId)
        {
            throw new FormatException($"Active match is {matchId}, not requested match {expectedMatchId.Value}.");
        }

        if (includeMulliganState)
        {
            // fyserver's reconnect endpoint includes mulligan discards, but also marks
            // this user online. It is used only by the explicit commit path.
            root = await GetObjectAsync("matches/v2/reconnect", cancellationToken);
            (match, startingData) = ExtractMatch(root);
        }

        matchId = Int(match, "match_id");
        int leftPlayerId = Int(startingData, "player_id_left", Int(match, "player_id_left"));
        int rightPlayerId = Int(startingData, "player_id_right", Int(match, "player_id_right"));
        var cards = ReadCards(startingData, includeMulliganState ? root : null);
        List<string> packets;
        if (includeMulliganState)
        {
            packets = Strings(root["actions"]);
        }
        else
        {
            packets = await PollActionsAsync(matchId, botSide == Side.Left ? rightPlayerId : leftPlayerId,
                                             cancellationToken);
            // PUT /actions calls fyserver's TickBot before returning. Refresh the
            // envelope so counters include any bot actions appended by that poll.
            JsonObject refreshedRoot = await GetObjectAsync("matches/v2", cancellationToken);
            (JsonObject refreshedMatch, JsonObject refreshedStartingData) = ExtractMatch(refreshedRoot);
            if (Int(refreshedMatch, "match_id") != matchId)
            {
                throw new FormatException("fyserver active match changed while polling actions.");
            }
            match = refreshedMatch;
            startingData = refreshedStartingData;
            cards = ReadCards(startingData, null);
            root = refreshedRoot;
        }

        var actions = new List<ServerAction>(packets.Count);
        int sessionId = 0;
        foreach (string packet in packets)
        {
            var decoded = ActionPacketCodec.Decode(packet);
            if (sessionId == 0)
            {
                sessionId = decoded.ActionId;
            }
            else if (sessionId != decoded.ActionId)
            {
                throw new FormatException("fyserver action packets contain multiple action session IDs.");
            }

            JsonObject payload = decoded.Payload;
            actions.Add(new ServerAction(
                Int(payload, "action_id"),
                String(payload, "action_type"),
                Int(payload, "player_id"),
                ReadActionData(payload["action_data"]),
                Int(payload, "turn_number")));
        }

        var snapshot = new ServerMatchSnapshot(
            matchId,
            Int(match, "current_turn"),
            leftPlayerId,
            rightPlayerId,
            cards,
            actions)
        {
            NextActionId = Int(match, "current_action_id") + 1,
            SendActionId = Int(match, "current_action_id"),
            ActionSessionId = sessionId,
        };

        string actionSide = String(match, "action_side", String(root, "action_side"));
        Side currentActionSide = actionSide switch
        {
            "left" => Side.Left,
            "right" => Side.Right,
            _ => Side.NotAvailable,
        };
        bool hasMulliganState = root["mulligan_left"]?["discarded_cards"] is JsonArray
                                && root["mulligan_right"]?["discarded_cards"] is JsonArray;
        return new FyServerSession(snapshot, currentActionSide, hasMulliganState);
    }

    public async Task SubmitActionAsync(int matchId, int sessionId, ServerAction action,
                                        CancellationToken cancellationToken = default)
    {
        if (sessionId <= 0)
        {
            throw new InvalidOperationException("Cannot submit actions without a valid ActionSessionId.");
        }

        var data = new JsonObject();
        foreach (var (key, value) in action.ActionData)
        {
            data[key] = value;
        }

        var payload = new JsonObject
        {
            ["action_id"] = action.ActionId,
            ["action_type"] = action.ActionType,
            ["player_id"] = action.PlayerId,
            ["action_data"] = data,
            ["sub_actions"] = new JsonArray(),
            ["turn_number"] = action.TurnNumber,
        };
        string packet = ActionPacketCodec.Encode(sessionId, payload);
        var body = new JsonObject { ["a"] = packet };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"matches/v2/{matchId}/actions", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<List<string>> PollActionsAsync(int matchId, int opponentPlayerId, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["min_action_id"] = 1, ["opponent_id"] = opponentPlayerId };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync($"matches/v2/{matchId}/actions", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Strings(root?["actions"]);
    }

    private async Task<JsonObject> GetObjectAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
               ?? throw new FormatException("fyserver returned an empty or non-object response.");
    }

    private static (JsonObject Match, JsonObject StartingData) ExtractMatch(JsonObject root)
    {
        if (root["match_and_starting_data"] is JsonObject data
            && data["match"] is JsonObject match
            && data["starting_data"] is JsonObject startingData)
        {
            return (match, startingData);
        }
        if (root["match"] is JsonObject reconnectMatch && root["starting_data"] is JsonObject reconnectData)
        {
            return (reconnectMatch, reconnectData);
        }
        throw new FormatException("fyserver did not return an active match snapshot.");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"fyserver returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static List<ServerCard> ReadCards(JsonObject startingData, JsonObject? reconnect)
    {
        var cards = new List<ServerCard>();
        Add(startingData["location_card_left"], "board_hqleft");
        Add(startingData["location_card_right"], "board_hqright");
        AddMany(startingData["starting_hand_left"], "hand_left");
        AddMany(startingData["starting_hand_right"], "hand_right");
        AddMany(startingData["deck_left"], "deck_left");
        AddMany(startingData["deck_right"], "deck_right");
        if (reconnect is not null)
        {
            AddMany(reconnect["mulligan_left"]?["discarded_cards"], "discard_left", forceLocation: true);
            AddMany(reconnect["mulligan_right"]?["discarded_cards"], "discard_right", forceLocation: true);
        }

        return cards;

        void AddMany(JsonNode? node, string fallbackLocation, bool forceLocation = false)
        {
            if (node is not JsonArray array) return;
            foreach (JsonNode? card in array)
            {
                Add(card, fallbackLocation, forceLocation);
            }
        }

        void Add(JsonNode? node, string fallbackLocation, bool forceLocation = false)
        {
            if (node is not JsonObject card) return;
            cards.Add(new ServerCard(
                Int(card, "card_id"),
                Bool(card, "is_gold"),
                forceLocation ? fallbackLocation : String(card, "location", fallbackLocation),
                Int(card, "location_number"),
                String(card, "name")));
        }
    }

    private static Dictionary<string, string> ReadActionData(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                result[key] = value is JsonValue scalar && scalar.TryGetValue<string>(out string? text)
                    ? text ?? ""
                    : value?.ToJsonString() ?? "";
            }
        }

        return result;
    }

    private static List<string> Strings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null)
                   .Where(text => !string.IsNullOrEmpty(text)).Cast<string>().ToList()
            : new List<string>();

    private static int Int(JsonObject obj, string key, int fallback = 0)
        => obj[key] is JsonValue value && value.TryGetValue<int>(out int result) ? result : fallback;

    private static bool Bool(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.TryGetValue<bool>(out bool result) && result;

    private static string String(JsonObject obj, string key, string fallback = "")
        => obj[key] is JsonValue value && value.TryGetValue<string>(out string? result) ? result ?? fallback : fallback;

    public void Dispose() => _http.Dispose();
}
