from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)


p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

# Only one Android WebSocket may own the global AudioDeviceSwitcher at a time.
s = replace_once(
    s,
    '''listener.Start();\n\nwhile (true)''',
    '''listener.Start();\n\n// Audio routing/default endpoints are process-global. Serialize Android sessions so a\n// reconnecting/stale WebSocket cannot restore or disconnect audio owned by the current one.\nusing var clientOwnershipGate = new SemaphoreSlim(1, 1);\n\nwhile (true)''',
    'client ownership gate declaration')

s = replace_once(
    s,
    '''    _ = Task.Run(async () =>\n    {\n        AudioCableSink? audioSink = null;''',
    '''    _ = Task.Run(async () =>\n    {\n        await clientOwnershipGate.WaitAsync();\n        AudioCableSink? audioSink = null;''',
    'client ownership gate acquire')

s = replace_once(
    s,
    '''            externalController?.Dispose();\n            sendGate.Dispose();\n        }\n    });''',
    '''            externalController?.Dispose();\n            sendGate.Dispose();\n            clientOwnershipGate.Release();\n        }\n    });''',
    'client ownership gate release')

# A Codex microphone idle transition happens while Codex thinks/speaks. It must not end the
# session, send codex_idle, restore Windows defaults, or disconnect a companion-connected A2DP
# endpoint. The explicit end_session / smart-close paths own teardown instead.
s = s.replace('    const int IdleConfirmMs = 1200;\n', '', 1)
s = replace_once(
    s,
    '''            if (announcedActive == true)\n            {\n                if (idleSince == 0)\n                {\n                    idleSince = now;\n                    Console.WriteLine($"Codex microphone looks idle; confirming for {IdleConfirmMs} ms...");\n                }\n                else if (now - idleSince >= IdleConfirmMs)\n                {\n                    announcedActive = false;\n                    idleSince = 0;\n                    await SendJson(socket, gate, new { type = "codex_idle" });\n                    Console.WriteLine("Codex microphone idle CONFIRMED");\n                    if (audioSwitcher.State == AudioSessionState.Listening) audioSwitcher.ScheduleRestore();\n                }\n            }''',
    '''            if (announcedActive == true)\n            {\n                if (idleSince == 0)\n                {\n                    idleSince = now;\n                    var ui = CodexUiStateDetector.Detect();\n                    Console.WriteLine($"Codex microphone inactive · UI={ui.State}; keeping session/downlink and selected output active until explicit end");\n                }\n            }''',
    'mic idle must not end session')

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
print('Single-client audio ownership + explicit-end routing patch applied')
