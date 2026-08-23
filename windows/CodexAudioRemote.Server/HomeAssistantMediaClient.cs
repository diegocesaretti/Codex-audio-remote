using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed record HomeAssistantMediaPlayerChoice(string EntityId, string Name, string State)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) || Name == EntityId
        ? $"{EntityId} [{State}]"
        : $"{Name} · {EntityId} [{State}]";
}

internal static class HomeAssistantMediaClient
{
    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static async Task<IReadOnlyList<HomeAssistantMediaPlayerChoice>> GetMediaPlayersAsync(CancellationToken cancellationToken = default)
    {
        var token = RealtimeMirrorSettings.HomeAssistantAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Falta el token de Home Assistant.");

        using var request = new HttpRequestMessage(HttpMethod.Get, AppSettings.HomeAssistantBaseUrl + "/api/states");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Home Assistant respondió {(int)response.StatusCode}: {Trim(body, 220)}");

        using var doc = JsonDocument.Parse(body);
        var list = new List<HomeAssistantMediaPlayerChoice>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var entityId = item.TryGetProperty("entity_id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (!entityId.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase)) continue;
            var state = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "unknown" : "unknown";
            var name = entityId;
            if (item.TryGetProperty("attributes", out var attrs) &&
                attrs.TryGetProperty("friendly_name", out var nameProp) &&
                !string.IsNullOrWhiteSpace(nameProp.GetString()))
                name = nameProp.GetString()!;
            list.Add(new HomeAssistantMediaPlayerChoice(entityId, name, state));
        }

        return list
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task StartLiveStreamAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        var entity = RealtimeMirrorSettings.HomeAssistantMediaPlayerEntity;
        var token = RealtimeMirrorSettings.HomeAssistantAccessToken;
        if (string.IsNullOrWhiteSpace(entity)) throw new InvalidOperationException("No hay media_player de Home Assistant seleccionado.");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Falta el token de Home Assistant.");

        var payload = JsonSerializer.Serialize(new
        {
            entity_id = entity,
            media_content_id = streamUrl,
            media_content_type = "audio/mpeg",
            announce = RealtimeMirrorSettings.HomeAssistantMirrorAnnounce,
            extra = new
            {
                stream_type = "LIVE",
                title = "Sol · Codex Realtime"
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            AppSettings.HomeAssistantBaseUrl + "/api/services/media_player/play_media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"media_player.play_media respondió {(int)response.StatusCode}: {Trim(body, 220)}");
    }

    static string Trim(string text, int max)
    {
        text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
