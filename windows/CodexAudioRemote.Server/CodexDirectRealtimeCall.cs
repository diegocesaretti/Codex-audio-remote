using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed class CodexDirectRealtimeCall
{
    const string RealtimeCallUrl = "https://chatgpt.com/backend-api/realtime/calls?intent=quicksilver&architecture=avas";
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal sealed record Result(string Sdp, string CallId);

    public async Task<Result> CreateAsync(string offerSdp, CancellationToken cancellationToken)
    {
        var auth = ReadCodexOAuth();
        using var request = new HttpRequestMessage(HttpMethod.Post, RealtimeCallUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("OpenAI-Alpha", "quicksilver=v2");
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-audio-remote/0.2.0");
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", auth.AccountId);

        // Match Codex realtime V1's AVAS session shape, but intentionally omit `model`.
        // The current ChatGPT backend selects the compatible realtime model server-side and
        // rejects clients that include session.model.
        var payload = new
        {
            sdp = offerSdp,
            session = new
            {
                type = "quicksilver",
                instructions = "You are Codex Voice. Keep responses concise and conversational. Use the attached Codex sideband for context, tools, and handoffs.",
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 }
                    },
                    output = new
                    {
                        voice = "cove"
                    }
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        Console.WriteLine("Codex direct OAuth WebRTC: POST realtime/calls · session.model omitted · quicksilver=v2");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Direct realtime call failed HTTP {(int)response.StatusCode}: {body}");

        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location) && response.Headers.TryGetValues("Location", out var locations))
            location = locations.FirstOrDefault();
        var callId = ParseCallId(location);
        if (string.IsNullOrWhiteSpace(callId))
            throw new InvalidOperationException("Direct realtime call response did not contain a usable Location/call id.");
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Direct realtime call response did not contain an SDP answer.");

        Console.WriteLine($"Codex direct OAuth WebRTC call created · call={callId}");
        return new Result(body, callId);
    }

    static string ParseCallId(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "";
        var path = location.Split('?', 2)[0].TrimEnd('/');
        return path.Split('/').LastOrDefault() ?? "";
    }

    static OAuthSnapshot ReadCodexOAuth()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var authPath = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(authPath))
            throw new InvalidOperationException($"Codex OAuth file was not found at {authPath}. This experiment currently requires Codex auth stored in auth.json; run 'codex login' using file credential storage.");

        using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Codex auth.json does not contain ChatGPT OAuth tokens.");
        var accessToken = tokens.TryGetProperty("access_token", out var access) ? access.GetString() ?? "" : "";
        var accountId = tokens.TryGetProperty("account_id", out var account) ? account.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Codex auth.json does not contain a usable access_token.");
        return new OAuthSnapshot(accessToken, accountId);
    }

    sealed record OAuthSnapshot(string AccessToken, string AccountId);
}
