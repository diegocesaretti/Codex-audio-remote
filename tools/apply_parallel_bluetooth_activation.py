from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

# Windows companion: remove Bluetooth reconnect from the wake critical path while preserving
# selected-output routing. Codex/mic activation starts immediately; Bluetooth connects in parallel.
# When the selected render endpoint becomes Active, defaults are switched and an already-running
# downlink capture is rebound to that endpoint without interrupting uplink audio.
p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

s = replace_once(
    s,
'''            using var cts = new CancellationTokenSource();
            bool gracefulHold = false;
            CancellationTokenSource? smartCloseCts = null;
            string lastUiStateLog = "";
            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, IsCurrentOwner, cts.Token);''',
'''            using var cts = new CancellationTokenSource();
            bool gracefulHold = false;
            CancellationTokenSource? smartCloseCts = null;
            CancellationTokenSource? wakeActivationCts = null;
            string lastUiStateLog = "";
            long wakeStartedAt = 0;
            var downlinkSync = new object();
            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, IsCurrentOwner, () => Volatile.Read(ref wakeStartedAt), cts.Token);''',
    'activation state fields')

s = replace_once(
    s,
'''            async Task StopAudioSession(bool notify = true)
            {
                codexInputRecorder?.Dispose(); codexInputRecorder = null;
                audioSink?.Dispose(); audioSink = null;
                downlink?.Dispose(); downlink = null;
                if (notify) await SendJson(socket, sendGate, new { type = "downlink_stop" });
            }''',
'''            async Task StopAudioSession(bool notify = true)
            {
                codexInputRecorder?.Dispose(); codexInputRecorder = null;
                audioSink?.Dispose(); audioSink = null;
                LoopbackDownlink? oldDownlink;
                lock (downlinkSync) { oldDownlink = downlink; downlink = null; }
                oldDownlink?.Dispose();
                if (notify) await SendJson(socket, sendGate, new { type = "downlink_stop" });
            }

            void CancelWakeActivation()
            {
                try { wakeActivationCts?.Cancel(); } catch { }
                try { wakeActivationCts?.Dispose(); } catch { }
                wakeActivationCts = null;
            }

            async Task RebindDownlinkToSelectedAsync(CancellationToken token)
            {
                var selectedId = DownlinkDeviceSettings.SelectedDeviceId;
                if (string.IsNullOrWhiteSpace(selectedId) || token.IsCancellationRequested || !IsCurrentOwner()) return;

                LoopbackDownlink? current;
                lock (downlinkSync) current = downlink;
                if (current is null || string.Equals(current.DeviceId, selectedId, StringComparison.Ordinal)) return;

                LoopbackDownlink? replacement = null;
                try
                {
                    replacement = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm), selectedId);
                    replacement.Start();
                    if (!string.Equals(replacement.DeviceId, selectedId, StringComparison.Ordinal))
                    {
                        replacement.Dispose();
                        return;
                    }

                    bool installed = false;
                    lock (downlinkSync)
                    {
                        if (!token.IsCancellationRequested && IsCurrentOwner() && ReferenceEquals(downlink, current))
                        {
                            downlink = replacement;
                            installed = true;
                        }
                    }
                    if (!installed)
                    {
                        replacement.Dispose();
                        return;
                    }
                    replacement = null;
                    current.Dispose();
                    await SendJson(socket, sendGate, new { type = "downlink_start", sampleRate = 16000, channels = 1, source = "bluetooth_handoff" });
                    var started = Volatile.Read(ref wakeStartedAt);
                    var elapsed = started > 0 ? Environment.TickCount64 - started : 0;
                    Console.WriteLine($"Activation timeline: downlink rebound to selected output T+{elapsed} ms");
                }
                catch (Exception ex)
                {
                    replacement?.Dispose();
                    Console.WriteLine($"Bluetooth downlink handoff warning: {ex.Message}");
                }
            }

            async Task MonitorSelectedOutputHandoffAsync(CancellationToken token)
            {
                var selectedId = DownlinkDeviceSettings.SelectedDeviceId;
                if (string.IsNullOrWhiteSpace(selectedId)) return;
                var monitorStart = Environment.TickCount64;
                const int MaxMonitorMs = 10000;
                while (!token.IsCancellationRequested && IsCurrentOwner() && socket.State == WebSocketState.Open && Environment.TickCount64 - monitorStart < MaxMonitorMs)
                {
                    if (switcher.ActivateSelectedRenderIfAvailable())
                    {
                        var started = Volatile.Read(ref wakeStartedAt);
                        var elapsed = started > 0 ? Environment.TickCount64 - started : 0;
                        Console.WriteLine($"Activation timeline: selected output ACTIVE/routed T+{elapsed} ms");
                        await RebindDownlinkToSelectedAsync(token);
                        return;
                    }
                    await Task.Delay(150, token);
                }
                if (!token.IsCancellationRequested && IsCurrentOwner())
                    Console.WriteLine("Bluetooth handoff monitor: selected output did not become Active within 10 s; keeping safe fallback output.");
            }''',
    'parallel activation helpers')

s = replace_once(
    s,
'''                    case "wake":
                        await SendJson(socket, sendGate, new { type = "activating" });
                        await BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync();
                        if (!switcher.ActivateRemoteMic())
                        {
                            await SendJson(socket, sendGate, new { type = "activation_failed", reason = "virtual_mic_not_found" });
                            break;
                        }
                        switcher.BeginActivation();
                        await Task.Delay(75);
                        ShortcutSender.Send(options.Shortcut);
                        _ = ConfirmActivation(socket, sendGate, switcher, options.ActivationTimeoutMs);
                        break;''',
'''                    case "wake":
                        CancelWakeActivation();
                        wakeActivationCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        var wakeToken = wakeActivationCts.Token;
                        Interlocked.Exchange(ref wakeStartedAt, Environment.TickCount64);
                        Console.WriteLine("Activation timeline: wake received T+0 ms");
                        await SendJson(socket, sendGate, new { type = "activating" });

                        // Start Bluetooth immediately, but never await it on the wake path.
                        _ = Task.Run(async () =>
                        {
                            try { await BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync(wakeToken); }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) { Console.WriteLine($"Parallel Bluetooth reconnect warning: {ex.Message}"); }
                        }, wakeToken);
                        _ = Task.Run(async () =>
                        {
                            try { await MonitorSelectedOutputHandoffAsync(wakeToken); }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) { Console.WriteLine($"Bluetooth handoff monitor warning: {ex.Message}"); }
                        }, wakeToken);

                        if (!switcher.ActivateRemoteMic())
                        {
                            CancelWakeActivation();
                            await SendJson(socket, sendGate, new { type = "activation_failed", reason = "virtual_mic_not_found" });
                            break;
                        }
                        switcher.BeginActivation();
                        var afterMic = Environment.TickCount64 - Volatile.Read(ref wakeStartedAt);
                        Console.WriteLine($"Activation timeline: virtual mic routed T+{afterMic} ms");
                        await Task.Delay(50);
                        ShortcutSender.Send(options.Shortcut);
                        var afterShortcut = Environment.TickCount64 - Volatile.Read(ref wakeStartedAt);
                        Console.WriteLine($"Activation timeline: Codex shortcut sent T+{afterShortcut} ms");
                        _ = ConfirmActivation(socket, sendGate, switcher, options.ActivationTimeoutMs);
                        break;''',
    'nonblocking wake activation')

s = replace_once(
    s,
'''                        codexInputRecorder = CableOutputRecorder.TryCreate(options.VirtualMicName);
                        downlink = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm), DownlinkDeviceSettings.SelectedDeviceId);
                        await SendJson(socket, sendGate, new { type = "downlink_start", sampleRate = 16000, channels = 1 });
                        downlink.Start();
                        Console.WriteLine($"Bidirectional session: {sampleRate} Hz, chunk {chunkMs} ms, quality {quality}%, latency {latency}%");''',
'''                        codexInputRecorder = CableOutputRecorder.TryCreate(options.VirtualMicName);
                        var initialDownlink = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm), DownlinkDeviceSettings.SelectedDeviceId);
                        initialDownlink.Start();
                        lock (downlinkSync) downlink = initialDownlink;
                        await SendJson(socket, sendGate, new { type = "downlink_start", sampleRate = 16000, channels = 1 });
                        var audioStarted = Volatile.Read(ref wakeStartedAt);
                        if (audioStarted > 0) Console.WriteLine($"Activation timeline: bidirectional audio started T+{Environment.TickCount64 - audioStarted} ms");
                        Console.WriteLine($"Bidirectional session: {sampleRate} Hz, chunk {chunkMs} ms, quality {quality}%, latency {latency}%");''',
    'downlink synchronized start')

# Explicit ending paths cancel any in-flight Bluetooth connect/handoff work.
s = replace_once(
    s,
'''                    case "audio_stop":
                        gracefulHold = false;
                        await StopAudioSession();''',
'''                    case "audio_stop":
                        gracefulHold = false;
                        CancelWakeActivation();
                        await StopAudioSession();''',
    'audio stop cancels activation')

s = replace_once(
    s,
'''                    case "end_session":
                        gracefulHold = false;
                        CancelSmartClose();''',
'''                    case "end_session":
                        gracefulHold = false;
                        CancelSmartClose();
                        CancelWakeActivation();''',
    'end session cancels activation')

s = replace_once(
    s,
'''            CancelSmartClose();
            await StopAudioSession(false);''',
'''            CancelSmartClose();
            CancelWakeActivation();
            await StopAudioSession(false);''',
    'disconnect cancels activation')

# Owner-aware watcher: report measured wake->listening latency.
s = replace_once(
    s,
'''async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, Func<bool> suppressIdle, Func<bool> isCurrentOwner, CancellationToken token)''',
'''async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, Func<bool> suppressIdle, Func<bool> isCurrentOwner, Func<long> wakeStartedAt, CancellationToken token)''',
    'watcher activation clock signature')

s = replace_once(
    s,
'''                await SendJson(socket, gate, new { type = "codex_listening" });
                Console.WriteLine("Codex microphone ACTIVE");''',
'''                await SendJson(socket, gate, new { type = "codex_listening" });
                var activationStart = wakeStartedAt();
                if (activationStart > 0) Console.WriteLine($"Activation timeline: Codex microphone ACTIVE T+{now - activationStart} ms");
                Console.WriteLine("Codex microphone ACTIVE");''',
    'watcher activation timing log')

# AudioDeviceSwitcher can now switch the selected render endpoint after it appears asynchronously.
insert_anchor = '''    public void BeginActivation() { lock (sync) { CancelPendingRestore(); State = AudioSessionState.Activating; } }'''
insert = '''    public bool ActivateSelectedRenderIfAvailable()
    {
        lock (sync)
        {
            var renderId = DownlinkDeviceSettings.SelectedDeviceId;
            if (string.IsNullOrWhiteSpace(renderId)) return false;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => string.Equals(d.ID, renderId, StringComparison.Ordinal));
                if (render is null) return false;
                string name;
                try { name = render.FriendlyName; }
                catch { return false; }
                if (DownlinkDeviceSettings.IsUnsafe(name)) return false;

                if (savedRender is null)
                {
                    savedRender = new SavedDefaults(AudioDeviceManager.GetDefaultRenderId(Role.Console), AudioDeviceManager.GetDefaultRenderId(Role.Multimedia), AudioDeviceManager.GetDefaultRenderId(Role.Communications));
                    File.WriteAllText(renderRecoveryPath, JsonSerializer.Serialize(savedRender));
                }
                PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Console);
                PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Multimedia);
                PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Communications);
                Console.WriteLine($"Codex output temporarily switched to: {name}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Selected render handoff warning: {ex.Message}");
                return false;
            }
        }
    }

''' + insert_anchor
if 'public bool ActivateSelectedRenderIfAvailable()' not in s:
    s = replace_once(s, insert_anchor, insert, 'late selected render switch')

p.write_text(s, encoding='utf-8')

# Expose the actual capture endpoint so the connection can decide whether a Bluetooth handoff
# requires recreating loopback capture.
p = Path('windows/CodexAudioRemote.Server/LoopbackDownlink.cs')
s = p.read_text(encoding='utf-8')
s = replace_once(
    s,
'''    const double SpeechRmsThreshold = 420.0;

    public LoopbackDownlink''',
'''    const double SpeechRmsThreshold = 420.0;

    public string DeviceId => captureDevice.ID;
    public string DeviceName
    {
        get { try { return captureDevice.FriendlyName; } catch { return captureDevice.ID; } }
    }

    public LoopbackDownlink''',
    'downlink endpoint identity')
p.write_text(s, encoding='utf-8')

print('Parallel Bluetooth activation + dynamic downlink handoff patch applied')
