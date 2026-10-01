using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const string appServerUrl = "ws://127.0.0.1:4282";
const string solFastBase = "http://127.0.0.1:8770";

var prompt = args.Length == 0
    ? "Decime si la TV del dormitorio está prendida. Usá las herramientas disponibles y no inventes el estado."
    : string.Join(" ", args);

Console.WriteLine("Codex Text Probe · READ ONLY");
Console.WriteLine("Prompt: " + prompt);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
using var rpc = new AppServerRpc(appServerUrl, http, solFastBase);

var total = Stopwatch.StartNew();

await rpc.ConnectAsync();

var init = await rpc.RequestAsync("initialize", new
{
    clientInfo = new { name = "codex-text-probe", title = "Codex Text Probe", version = "0.1.0" },
    capabilities = new { experimentalApi = true }
});
await rpc.NotifyAsync("initialized", new { });

var account = await rpc.RequestAsync("account/read", new { refreshToken = false });
var authMode = ReadString(account, "authMode")
    ?? (account.TryGetProperty("account", out var accountObj) ? ReadString(accountObj, "type") : null)
    ?? "unknown";
var planType = ReadString(account, "planType")
    ?? (account.TryGetProperty("account", out var nestedAccount) ? ReadString(nestedAccount, "planType") : null)
    ?? "unknown";

Console.WriteLine($"Auth: {authMode} · plan={planType}");
if (!string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Codex app-server is not authenticated with ChatGPT OAuth.");

var dynamicTools = await LoadReadOnlySolToolsAsync(http, solFastBase);
Console.WriteLine("Dynamic tools: " + string.Join(", ", dynamicTools.Select(t => t.Name)));

var threadStartWatch = Stopwatch.StartNew();
var thread = await rpc.RequestAsync("thread/start", new
{
    ephemeral = true,
    dynamicTools = dynamicTools.Select(tool => new
    {
        name = tool.Name,
        description = tool.Description,
        inputSchema = tool.InputSchema
    }).ToArray(),
    developerInstructions =
        "This is a read-only diagnostic probe. " +
        "For current Home Assistant state, use the provided SOL tools. " +
        "Do not claim current state from memory. " +
        "Do not attempt actions, shell, browser, computer use, or writes. " +
        "If the requested state cannot be established from the tools, say so."
});
threadStartWatch.Stop();

if (!thread.TryGetProperty("thread", out var threadObj))
    throw new InvalidOperationException("thread/start returned no thread object.");
var threadId = ReadString(threadObj, "id") ?? throw new InvalidOperationException("thread/start returned no thread id.");
Console.WriteLine($"Thread: {threadId} · start={threadStartWatch.ElapsedMilliseconds}ms");

rpc.BeginTurnCapture(threadId);

var turnWatch = Stopwatch.StartNew();
var turn = await rpc.RequestAsync("turn/start", new
{
    threadId,
    input = new object[]
    {
        new
        {
            type = "text",
            text = prompt,
            textElements = Array.Empty<object>()
        }
    },
    turnTrigger = "codex_text_probe"
});
var turnId = turn.TryGetProperty("turn", out var turnObj) ? ReadString(turnObj, "id") : null;
Console.WriteLine("Turn: " + (turnId ?? "(unknown)"));

var completed = await rpc.WaitForTurnAsync(TimeSpan.FromSeconds(35));
turnWatch.Stop();
total.Stop();

Console.WriteLine();
Console.WriteLine("----- RESULT -----");
Console.WriteLine(completed.Text.Trim());
Console.WriteLine("------------------");
Console.WriteLine($"Status: {completed.Status}");
Console.WriteLine($"Tool calls: {completed.ToolCalls}");
Console.WriteLine($"Turn latency: {turnWatch.ElapsedMilliseconds}ms");
Console.WriteLine($"Total latency: {total.ElapsedMilliseconds}ms");

if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
    Environment.ExitCode = 2;

static async Task<List<SolTool>> LoadReadOnlySolToolsAsync(HttpClient http, string baseUrl)
{
    using var response = await http.GetAsync(baseUrl + "/tools");
    var body = await response.Content.ReadAsStringAsync();
    response.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("SOL Fast /tools returned no tool catalog.");

    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "home_assistant_get_state",
        "home_assistant_search_states"
    };

    var result = new List<SolTool>();
    foreach (var item in tools.EnumerateArray())
    {
        var name = ReadString(item, "name");
        if (name is null || !allowed.Contains(name)) continue;
        var description = ReadString(item, "description") ?? name;
        var schema = item.TryGetProperty("inputSchema", out var inputSchema) && inputSchema.ValueKind == JsonValueKind.Object
            ? inputSchema.Clone()
            : JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        result.Add(new SolTool(name, description, schema));
    }

    if (result.Count != allowed.Count)
        throw new InvalidOperationException("Required read-only SOL tools are missing from SOL Fast.");

    return result;
}

static string? ReadString(JsonElement root, string name)
    => root.ValueKind == JsonValueKind.Object
       && root.TryGetProperty(name, out var value)
       && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

internal sealed record SolTool(string Name, string Description, JsonElement InputSchema);
internal sealed record TurnResult(string Status, string Text, int ToolCalls);

internal sealed class AppServerRpc : IDisposable
{
    readonly Uri uri;
    readonly HttpClient http;
    readonly string solFastBase;
    readonly ClientWebSocket socket = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly StringBuilder assistantText = new();
    readonly object captureSync = new();

    Task? receiveLoop;
    TaskCompletionSource<TurnResult>? turnCompletion;
    string activeThreadId = "";
    int toolCalls;
    long nextId;
    bool disposed;

    public AppServerRpc(string url, HttpClient http, string solFastBase)
    {
        uri = new Uri(url);
        this.http = http;
        this.solFastBase = solFastBase.TrimEnd('/');
    }

    public async Task ConnectAsync()
    {
        await socket.ConnectAsync(uri, lifetime.Token);
        receiveLoop = Task.Run(() => ReceiveLoopAsync(lifetime.Token));
    }

    public void BeginTurnCapture(string threadId)
    {
        lock (captureSync)
        {
            activeThreadId = threadId;
            assistantText.Clear();
            toolCalls = 0;
            turnCompletion = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task<TurnResult> WaitForTurnAsync(TimeSpan timeout)
    {
        Task<TurnResult> task;
        lock (captureSync)
            task = (turnCompletion ?? throw new InvalidOperationException("No active turn capture.")).Task;
        return await task.WaitAsync(timeout);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters)
    {
        var id = Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, lifetime.Token);
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters)
        => SendAsync(new { method, @params = parameters }, lifetime.Token);

    async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                using var doc = JsonDocument.Parse(stream.ToArray());
                await HandleMessageAsync(doc.RootElement.Clone(), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (captureSync)
                turnCompletion?.TrySetException(ex);
        }
    }

    async Task HandleMessageAsync(JsonElement root, CancellationToken token)
    {
        if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var id))
        {
            if (root.TryGetProperty("method", out _))
            {
                await HandleServerRequestAsync(root, token);
                return;
            }

            if (!pending.TryGetValue(id, out var waiter)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : error.ToString();
                waiter.TrySetException(new InvalidOperationException(message ?? "App-server request failed."));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                waiter.TrySetResult(result.Clone());
            }
            else
            {
                waiter.TrySetResult(default);
            }
            return;
        }

        if (!root.TryGetProperty("method", out var methodProp)) return;
        var method = methodProp.GetString() ?? "";
        var parameters = root.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "item/agentMessage/delta":
                if (parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.String)
                {
                    lock (captureSync) assistantText.Append(delta.GetString());
                }
                break;

            case "item/completed":
                CaptureCompletedAgentMessage(parameters);
                break;

            case "turn/completed":
                CompleteTurn(parameters);
                break;

            case "error":
                var errorText = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("message", out var em)
                    ? em.GetString() ?? "App-server error"
                    : parameters.ToString();
                lock (captureSync) turnCompletion?.TrySetException(new InvalidOperationException(errorText));
                break;
        }
    }

    async Task HandleServerRequestAsync(JsonElement root, CancellationToken token)
    {
        var id = root.GetProperty("id").Clone();
        var method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? "" : "";

        if (method != "item/tool/call")
        {
            await SendAsync(new
            {
                id,
                error = new { code = -32601, message = "Unsupported server request in read-only probe: " + method }
            }, token);
            return;
        }

        var parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var toolName = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tool", out var tool)
            ? tool.GetString() ?? ""
            : "";
        var arguments = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("arguments", out var args)
                        && args.ValueKind == JsonValueKind.Object
            ? args.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        if (toolName is not ("home_assistant_get_state" or "home_assistant_search_states"))
        {
            await SendToolResultAsync(id, false, "Tool blocked by read-only probe: " + toolName, token);
            return;
        }

        Interlocked.Increment(ref toolCalls);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, solFastBase + "/call");
            request.Content = JsonContent.Create(new { tool = toolName, arguments });
            using var response = await http.SendAsync(request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"SOL Fast HTTP {(int)response.StatusCode}: {text}");

            await SendToolResultAsync(id, true, text, token);
        }
        catch (Exception ex)
        {
            await SendToolResultAsync(id, false, "SOL read tool failed: " + ex.Message, token);
        }
    }

    Task SendToolResultAsync(JsonElement id, bool success, string text, CancellationToken token)
        => SendAsync(new
        {
            id,
            result = new
            {
                contentItems = new object[]
                {
                    new { type = "inputText", text }
                },
                success
            }
        }, token);

    void CaptureCompletedAgentMessage(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("item", out var item)
            || item.ValueKind != JsonValueKind.Object)
            return;

        var type = ReadString(item, "type");
        if (!string.Equals(type, "agentMessage", StringComparison.Ordinal)) return;

        var text = ReadString(item, "text");
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (captureSync)
        {
            if (assistantText.Length == 0)
                assistantText.Append(text);
        }
    }

    void CompleteTurn(JsonElement parameters)
    {
        var status = "unknown";
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("turn", out var turn)
            && turn.ValueKind == JsonValueKind.Object)
        {
            status = ReadString(turn, "status") ?? status;
        }

        TurnResult result;
        lock (captureSync)
        {
            result = new TurnResult(status, assistantText.ToString(), toolCalls);
            turnCompletion?.TrySetResult(result);
        }
    }

    async Task SendAsync(object payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await sendGate.WaitAsync(token);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new InvalidOperationException("Codex app-server socket is not open.");
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { socket.Abort(); } catch { }
        try { socket.Dispose(); } catch { }
        sendGate.Dispose();
        lifetime.Dispose();
    }
}
