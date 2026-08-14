from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)


p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

# Audio routing is process-global, but a blocking semaphore is wrong here: an old WebSocket can
# remain alive for a long time and prevent a freshly reconnected Android client from ever being
# serviced. Instead each accepted connection receives a monotonically increasing owner id. The
# newest connection becomes authoritative immediately; stale connections keep unwinding normally
# but are not allowed to inject audio, process control messages, emit Codex state, or restore audio.
s = replace_once(
    s,
    '''listener.Start();\n\nwhile (true)''',
    '''listener.Start();\n\nlong nextClientOwner = 0;\nlong activeClientOwner = 0;\n\nwhile (true)''',
    'client ownership generation declaration')

s = replace_once(
    s,
    '''    _ = Task.Run(async () =>\n    {\n        AudioCableSink? audioSink = null;''',
    '''    _ = Task.Run(async () =>\n    {\n        var ownerId = Interlocked.Increment(ref nextClientOwner);\n        Interlocked.Exchange(ref activeClientOwner, ownerId);\n        bool IsCurrentOwner() => Volatile.Read(ref activeClientOwner) == ownerId;\n        AudioCableSink? audioSink = null;''',
    'client ownership generation acquire')

# Ignore all payload from stale sockets. This prevents an old Android connection from continuing
# to feed the virtual microphone or ending/restoring the active session owned by the replacement.
s = replace_once(
    s,
    '''                if (result.MessageType == WebSocketMessageType.Binary)\n                {\n                    audioBytes += result.Count;''',
    '''                if (result.MessageType == WebSocketMessageType.Binary)\n                {\n                    if (!IsCurrentOwner()) continue;\n                    audioBytes += result.Count;''',
    'stale binary ignored')

s = replace_once(
    s,
    '''                var type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;\n                Console.WriteLine($"<- {text}");\n\n                switch (type)''',
    '''                var type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;\n                Console.WriteLine($"<- {text}");\n                if (!IsCurrentOwner())\n                {\n                    Console.WriteLine($"Ignoring stale client message: {type ?? "unknown"}");\n                    continue;\n                }\n\n                switch (type)''',
    'stale control ignored')

# Only the current owner may restore process-global audio state when its socket closes or faults.
s = replace_once(
    s,
    '''            Console.WriteLine("Client disconnected");''',
    '''            Console.WriteLine("Client disconnected");''',
    'disconnect log anchor')

s = replace_once(
    s,
    '''            switcher.RestoreNow();\n            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)''',
    '''            if (IsCurrentOwner()) switcher.RestoreNow();\n            else Console.WriteLine("Stale client disconnected; audio restore skipped");\n            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)''',
    'stale normal disconnect restore guard')

s = replace_once(
    s,
    '''            Console.WriteLine($"Client error: {ex.Message}");\n            switcher.RestoreNow();''',
    '''            Console.WriteLine($"Client error: {ex}");\n            if (IsCurrentOwner()) switcher.RestoreNow();\n            else Console.WriteLine("Stale client faulted; audio restore skipped");''',
    'stale error restore guard')

# A Codex microphone idle transition happens while Codex thinks/speaks. It must not end the
# session, send codex_idle, restore Windows defaults, or disconnect a companion-connected A2DP
# endpoint. The explicit end_session / smart-close paths own teardown instead.
s = s.replace('    const int IdleConfirmMs = 1200;\n', '', 1)
s = replace_once(
    s,
    '''            if (announcedActive == true)\n            {\n                if (idleSince == 0)\n                {\n                    idleSince = now;\n                    Console.WriteLine($"Codex microphone looks idle; confirming for {IdleConfirmMs} ms...");\n                }\n                else if (now - idleSince >= IdleConfirmMs)\n                {\n                    announcedActive = false;\n                    idleSince = 0;\n                    await SendJson(socket, gate, new { type = "codex_idle" });\n                    Console.WriteLine("Codex microphone idle CONFIRMED");\n                    if (audioSwitcher.State == AudioSessionState.Listening) audioSwitcher.ScheduleRestore();\n                }\n            }''',
    '''            if (announcedActive == true)\n            {\n                if (idleSince == 0)\n                {\n                    idleSince = now;\n                    var ui = CodexUiStateDetector.Detect();\n                    Console.WriteLine($"Codex microphone inactive · UI={ui.State}; keeping session/downlink and selected output active until explicit end");\n                }\n            }''',
    'mic idle must not end session')

# Make the mic watcher ownership-aware so a stale connection cannot emit duplicate
# codex_listening/codex_idle state into an obsolete Android socket.
s = replace_once(
    s,
    '''            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, cts.Token);''',
    '''            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, IsCurrentOwner, cts.Token);''',
    'watcher ownership argument')

s = replace_once(
    s,
    '''async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, Func<bool> suppressIdle, CancellationToken token)''',
    '''async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, Func<bool> suppressIdle, Func<bool> isCurrentOwner, CancellationToken token)''',
    'watcher ownership signature')

s = replace_once(
    s,
    '''    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)\n    {\n        if (ExternalConversationHub.SuppressCodexEvents)''',
    '''    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)\n    {\n        if (!isCurrentOwner())\n        {\n            await Task.Delay(100, token);\n            continue;\n        }\n        if (ExternalConversationHub.SuppressCodexEvents)''',
    'watcher stale owner suppression')

# Explicit smart-close is authoritative. Once audio capture is stopped and defaults are restored,
# tell Android to finish its local mic/speaker session.
s = replace_once(
    s,
    '''                else\n                {\n                    Console.WriteLine("Voice close fallback: no active mic/UI state detected; restoring microphone without toggling Voice.");\n                    switcher.RestoreNow();\n                }\n            }\n\n            async Task RunSmartClose''',
    '''                else\n                {\n                    Console.WriteLine("Voice close fallback: no active mic/UI state detected; restoring microphone without toggling Voice.");\n                    switcher.RestoreNow();\n                }\n                await SendJson(socket, sendGate, new { type = "codex_idle", reason, source = "explicit_smart_close" });\n            }\n\n            async Task RunSmartClose''',
    'smart close sends deterministic local finish')

s = replace_once(
    s,
    '''                    case "end_session":\n                        gracefulHold = false;\n                        CancelSmartClose();\n                        var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : "client";\n                        Console.WriteLine($"Ending conversation ({reason})");\n                        await SendJson(socket, sendGate, new { type = "session_ending", reason });\n                        await StopAudioSession();\n                        if (CodexMicDetector.IsActive())\n                        {\n                            ShortcutSender.Send(options.Shortcut);\n                            await ForceRestoreAfterEnd(switcher, options.EndSessionRestoreTimeoutMs);\n                        }\n                        else\n                        {\n                            switcher.RestoreNow();\n                        }\n                        break;''',
    '''                    case "end_session":\n                        gracefulHold = false;\n                        CancelSmartClose();\n                        var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : "client";\n                        Console.WriteLine($"Ending conversation ({reason})");\n                        await SendJson(socket, sendGate, new { type = "session_ending", reason });\n                        await StopAudioSession();\n                        if (CodexMicDetector.IsActive())\n                        {\n                            ShortcutSender.Send(options.Shortcut);\n                            await ForceRestoreAfterEnd(switcher, options.EndSessionRestoreTimeoutMs);\n                        }\n                        else\n                        {\n                            switcher.RestoreNow();\n                        }\n                        await SendJson(socket, sendGate, new { type = "codex_idle", reason, source = "explicit_end" });\n                        break;''',
    'explicit end sends deterministic local finish')

p.write_text(s, encoding='utf-8')
print('Nonblocking client ownership + explicit-end routing patch applied')
