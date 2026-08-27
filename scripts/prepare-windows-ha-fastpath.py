from pathlib import Path

path = Path("windows/CodexAudioRemote.Server/CodexRealtimeBridge.cs")
text = path.read_text(encoding="utf-8")

old = '''        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        var thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
'''

new = '''        var threadParams = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            threadParams["cwd"] = Path.GetFullPath(cwd);

        // Voice sessions are disposable control surfaces. Avoid durable thread persistence work
        // and preload the current HA state so simple device commands do not need a discovery pass.
        threadParams["ephemeral"] = true;
        var haContext = HomeAssistantWebSocketCache.Current?.GetCompactContext(80) ?? "";
        if (!string.IsNullOrWhiteSpace(haContext))
        {
            threadParams["developerInstructions"] =
                "HOME ASSISTANT FAST PATH: The following snapshot is already current. " +
                "For simple home-control requests, use these exact entity ids/states and call the existing Home Assistant tool directly. " +
                "Do not spend a turn listing or rediscovering HA states unless the requested entity is absent or the snapshot is clearly stale.\\n\\n" +
                haContext;
        }

        var threadStartAt = Stopwatch.GetTimestamp();
        JsonElement thread;
        try
        {
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Compatibility escape hatch for an older stock Codex install. Never make HA cache
            // support a hard dependency for starting voice.
            Console.WriteLine("HA fast-path thread/start fallback to stock params: " + ex.Message);
            threadParams.Remove("ephemeral");
            threadParams.Remove("developerInstructions");
            thread = await RequestAsync("thread/start", threadParams, cancellationToken);
        }
        var threadStartMs = Stopwatch.GetElapsedTime(threadStartAt).TotalMilliseconds;
        Console.WriteLine($"Realtime thread/start · {threadStartMs:0} ms · ephemeral={threadParams.ContainsKey(\"ephemeral\")} · HA-context={!string.IsNullOrWhiteSpace(haContext)} · chars={haContext.Length}");
        threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
'''

if old not in text:
    raise SystemExit("CodexRealtimeBridge thread/start anchor not found; refusing to patch an unexpected source version")

text = text.replace(old, new, 1)
path.write_text(text, encoding="utf-8")
print("Prepared official Codex HA fast-path in", path)
