from pathlib import Path


def replace_once(text, old, new, label):
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

# Android: move graceful-close countdown ownership to Windows.
p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')
s = replace_once(s,
'''        AndroidDebugLog.log("Graceful local end: " + reason + " · mic OFF now · PC Voice close in " + delaySeconds + "s");
        stopLocalMicKeepDownlink();
        updateNotification(delaySeconds == 0 ? "Mic apagado · cerrando Voice…" : "Mic apagado · Voice sigue activo " + delaySeconds + " s");
        if (delaySeconds == 0) { requestEndSession(reason + "_delayed"); return; }
        pendingPcEndRunnable = () -> requestEndSession(reason + "_delayed");
        handler.postDelayed(pendingPcEndRunnable, delaySeconds * 1000L);''',
'''        AndroidDebugLog.log("Graceful local end: " + reason + " · mic OFF now · smart PC Voice close delay=" + delaySeconds + "s");
        stopLocalMicKeepDownlink();
        sendText("{\\\"type\\\":\\\"graceful_end\\\",\\\"reason\\\":\\\"" + reason + "\\\",\\\"delaySeconds\\\":" + delaySeconds + "}");
        updateNotification(delaySeconds == 0 ? "Mic apagado · cierre inteligente…" : "Mic apagado · esperando a Codex");''',
'android smart graceful end')
p.write_text(s, encoding='utf-8')

# Windows UI Automation detector.
detector = Path('windows/CodexAudioRemote.Server/CodexUiStateDetector.cs')
detector.write_text(r'''using System.Diagnostics;
using System.Windows.Automation;

internal enum CodexUiState
{
    Unknown,
    Listening,
    Thinking,
    Speaking
}

internal sealed record CodexUiSnapshot(CodexUiState State, string? MatchedText, int ElementsScanned, bool WindowFound)
{
    public bool Busy => State is CodexUiState.Thinking or CodexUiState.Speaking;
}

internal static class CodexUiStateDetector
{
    static readonly string[] ThinkingTerms =
    {
        "pensando", "thinking", "procesando", "processing", "trabajando", "working",
        "ejecutando", "running", "generando", "generating"
    };

    static readonly string[] SpeakingTerms =
    {
        "hablando", "speaking", "respondiendo", "responding"
    };

    static readonly string[] ListeningTerms =
    {
        "escuchando", "listening"
    };

    public static CodexUiSnapshot Detect()
    {
        try
        {
            var processes = Process.GetProcesses()
                .Where(p => IsCodexProcess(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero)
                .OrderByDescending(p => p.MainWindowHandle != IntPtr.Zero)
                .ToArray();

            if (processes.Length == 0)
                return new(CodexUiState.Unknown, null, 0, false);

            int scanned = 0;
            foreach (var process in processes)
            {
                AutomationElement? root = null;
                try { root = AutomationElement.FromHandle(process.MainWindowHandle); }
                catch { }
                if (root is null) continue;

                var direct = Classify(ReadName(root));
                if (direct.State != CodexUiState.Unknown)
                    return new(direct.State, direct.Text, 1, true);

                AutomationElementCollection? all = null;
                try { all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
                catch { }
                if (all is null) continue;

                var limit = Math.Min(all.Count, 1200);
                CodexUiSnapshot? listening = null;
                for (int i = 0; i < limit; i++)
                {
                    scanned++;
                    string? name = null;
                    try { name = ReadName(all[i]); } catch { }
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var match = Classify(name);
                    if (match.State is CodexUiState.Thinking or CodexUiState.Speaking)
                        return new(match.State, match.Text, scanned, true);
                    if (match.State == CodexUiState.Listening && listening is null)
                        listening = new(CodexUiState.Listening, match.Text, scanned, true);
                }

                if (listening is not null) return listening;
            }

            return new(CodexUiState.Unknown, null, scanned, true);
        }
        catch
        {
            return new(CodexUiState.Unknown, null, 0, false);
        }
    }

    static bool IsCodexProcess(string name) =>
        name.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("openai", StringComparison.OrdinalIgnoreCase);

    static string? ReadName(AutomationElement element)
    {
        try
        {
            var name = element.Current.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; }
    }

    static (CodexUiState State, string? Text) Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (CodexUiState.Unknown, null);
        var normalized = text.Trim().ToLowerInvariant();
        if (ThinkingTerms.Any(normalized.Contains)) return (CodexUiState.Thinking, text);
        if (SpeakingTerms.Any(normalized.Contains)) return (CodexUiState.Speaking, text);
        if (ListeningTerms.Any(normalized.Contains)) return (CodexUiState.Listening, text);
        return (CodexUiState.Unknown, null);
    }
}
''', encoding='utf-8')

# Reference Windows UI Automation assemblies.
p = Path('windows/CodexAudioRemote.Server/CodexAudioRemote.Server.csproj')
s = p.read_text(encoding='utf-8')
if 'UIAutomationClient' not in s:
    s = replace_once(s,
'''  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.3.0" />
  </ItemGroup>''',
'''  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.3.0" />
    <Reference Include="UIAutomationClient" />
    <Reference Include="UIAutomationTypes" />
  </ItemGroup>''', 'uia references')
p.write_text(s, encoding='utf-8')

# Windows companion: smart delayed close owned by Windows.
p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')
s = replace_once(s,
'''            bool gracefulHold = false;
            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, cts.Token);''',
'''            bool gracefulHold = false;
            CancellationTokenSource? smartCloseCts = null;
            string lastUiStateLog = "";
            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, cts.Token);''', 'smart close fields')

s = replace_once(s,
'''            async Task StopUplinkOnly()
            {
                codexInputRecorder?.Dispose(); codexInputRecorder = null;
                audioSink?.Dispose(); audioSink = null;
                await SendJson(socket, sendGate, new { type = "mic_stopped" });
                Console.WriteLine("Remote microphone stopped; Codex Voice/downlink kept alive");
            }''',
'''            async Task StopUplinkOnly()
            {
                codexInputRecorder?.Dispose(); codexInputRecorder = null;
                audioSink?.Dispose(); audioSink = null;
                await SendJson(socket, sendGate, new { type = "mic_stopped" });
                Console.WriteLine("Remote microphone stopped; Codex Voice/downlink kept alive");
            }

            void CancelSmartClose()
            {
                try { smartCloseCts?.Cancel(); } catch { }
                try { smartCloseCts?.Dispose(); } catch { }
                smartCloseCts = null;
            }

            async Task ExecuteVoiceClose(string reason, string source)
            {
                gracefulHold = false;
                Console.WriteLine($"Smart Voice close executing ({source}) · reason={reason}");
                await SendJson(socket, sendGate, new { type = "session_ending", reason, source });
                await StopAudioSession();

                var ui = CodexUiStateDetector.Detect();
                var shouldToggle = CodexMicDetector.IsActive() || ui.State != CodexUiState.Unknown;
                if (shouldToggle)
                {
                    ShortcutSender.Send(options.Shortcut);
                    await ForceRestoreAfterEnd(switcher, options.EndSessionRestoreTimeoutMs);
                }
                else
                {
                    Console.WriteLine("Voice close fallback: no active mic/UI state detected; restoring microphone without toggling Voice.");
                    switcher.RestoreNow();
                }
            }

            async Task RunSmartClose(string reason, int delaySeconds, CancellationToken token)
            {
                var remainingMs = Math.Clamp(delaySeconds, 0, 120) * 1000;
                const int TickMs = 400;
                Console.WriteLine($"Smart Voice close armed · delay={delaySeconds}s · UI Automation will pause countdown while Codex is busy");
                await SendJson(socket, sendGate, new { type = "smart_close_armed", delaySeconds });

                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var snapshot = CodexUiStateDetector.Detect();
                    var stateLog = $"{snapshot.State}|{snapshot.MatchedText}|{snapshot.WindowFound}";
                    if (!string.Equals(lastUiStateLog, stateLog, StringComparison.Ordinal))
                    {
                        lastUiStateLog = stateLog;
                        Console.WriteLine($"Codex UI state: {snapshot.State} · text='{snapshot.MatchedText ?? "-"}' · scanned={snapshot.ElementsScanned} · window={snapshot.WindowFound}");
                        await SendJson(socket, sendGate, new { type = "codex_ui_state", state = snapshot.State.ToString().ToLowerInvariant(), text = snapshot.MatchedText });
                    }

                    if (snapshot.Busy)
                    {
                        await Task.Delay(TickMs, token);
                        continue;
                    }

                    if (remainingMs <= 0) break;
                    await Task.Delay(TickMs, token);
                    remainingMs -= TickMs;
                }

                if (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                    await ExecuteVoiceClose(reason, "smart_delay");
            }''', 'smart close methods')

s = replace_once(s,
'''                    case "audio_start":
                        gracefulHold = false;
                        audioBytes = 0;''',
'''                    case "audio_start":
                        gracefulHold = false;
                        CancelSmartClose();
                        audioBytes = 0;''', 'cancel smart on audio start')

s = replace_once(s,
'''                    case "mic_stop":
                        gracefulHold = true;
                        await StopUplinkOnly();
                        Console.WriteLine("Graceful hold ACTIVE: suppressing mic-idle session end until Android closes Voice");
                        break;

                    case "audio_stop":''',
'''                    case "mic_stop":
                        gracefulHold = true;
                        await StopUplinkOnly();
                        Console.WriteLine("Graceful hold ACTIVE: suppressing mic-idle session end until smart close completes");
                        break;

                    case "graceful_end":
                        gracefulHold = true;
                        var gracefulReason = doc.RootElement.TryGetProperty("reason", out var gracefulReasonProp) ? gracefulReasonProp.GetString() ?? "client" : "client";
                        var gracefulDelay = GetInt(doc.RootElement, "delaySeconds", 15, 0, 120);
                        CancelSmartClose();
                        smartCloseCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        _ = RunSmartClose(gracefulReason, gracefulDelay, smartCloseCts.Token);
                        break;

                    case "audio_stop":''', 'graceful end protocol')

s = replace_once(s,
'''                    case "end_session":
                        gracefulHold = false;
                        var reason =''',
'''                    case "end_session":
                        gracefulHold = false;
                        CancelSmartClose();
                        var reason =''', 'cancel smart immediate end')

s = replace_once(s,
'''            await StopAudioSession(false);
            cts.Cancel();''',
'''            CancelSmartClose();
            await StopAudioSession(false);
            cts.Cancel();''', 'cancel smart disconnect')

s = replace_once(s,
'''            codexInputRecorder?.Dispose(); audioSink?.Dispose(); downlink?.Dispose();
            Console.WriteLine($"Client error: {ex.Message}");''',
'''            CancelSmartClose();
            codexInputRecorder?.Dispose(); audioSink?.Dispose(); downlink?.Dispose();
            Console.WriteLine($"Client error: {ex.Message}");''', 'cancel smart error')

p.write_text(s, encoding='utf-8')
print('Smart Codex UI-state patch ready for build')
