using System.Net.WebSockets;

internal sealed partial class SessionServerV2
{
    public sealed record ExternalConversationStartResult(bool Accepted, string Status, string SessionId);

    public async Task<ExternalConversationStartResult> StartExternalConversationAsync(string contextSource, string source)
    {
        if (string.IsNullOrWhiteSpace(contextSource))
            return new ExternalConversationStartResult(false, "empty_context", "");

        source = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim().ToLowerInvariant();
        string newSession;
        CancellationToken token;

        lock (sync)
        {
            if (currentPeer is null || currentPeer.Socket.State != WebSocketState.Open)
                return new ExternalConversationStartResult(false, "android_not_connected", "");
            if (state != ServerSessionState.Idle)
                return new ExternalConversationStartResult(false, "busy", sessionId);

            sessionCts?.Cancel();
            sessionCts?.Dispose();
            sessionCts = new CancellationTokenSource();
            token = sessionCts.Token;
            newSession = Guid.NewGuid().ToString("N");
            sessionId = newSession;

            // Reserve the state atomically so a wake and a REST request cannot both start a session.
            state = ServerSessionState.Activating;
            stateReason = source + "_request";
            revision++;
        }

        await BroadcastStateAsync();
        Console.WriteLine($"Session {newSession}: external activation started · source={source}");
        _ = Task.Run(() => RunExternalConversationAsync(newSession, contextSource, source, token));
        return new ExternalConversationStartResult(true, "accepted", newSession);
    }

    async Task RunExternalConversationAsync(string expectedSession, string contextSource, string source, CancellationToken token)
    {
        Task<bool> bluetoothTask = Task.FromResult(true);
        try
        {
            bluetoothTask = BtcomBluetoothReconnect.EnsureSelectedOutputActiveAsync(token);

            if (!switcher.ActivateRemoteMic())
            {
                await FailActivationAsync(expectedSession, "virtual_mic_not_found");
                return;
            }

            switcher.BeginActivation();
            await Task.Delay(60, token);
            if (!IsSession(expectedSession, ServerSessionState.Activating)) return;
            ShortcutSender.Send(options.Shortcut);

            var started = Environment.TickCount64;
            while (!token.IsCancellationRequested && Environment.TickCount64 - started < options.ActivationTimeoutMs)
            {
                if (!IsSession(expectedSession, ServerSessionState.Activating)) return;
                if (CodexMicDetector.IsActive()) break;
                await Task.Delay(50, token);
            }

            if (!CodexMicDetector.IsActive())
            {
                await FailActivationAsync(expectedSession, "codex_mic_timeout");
                return;
            }

            // For REST-originated conversations correctness matters more than shaving the last
            // seconds from BT activation: route the selected output before feeding the context,
            // so the first word of Codex's answer is not lost on another Windows endpoint.
            try
            {
                var bluetoothReady = await bluetoothTask;
                if (bluetoothReady && IsSession(expectedSession, ServerSessionState.Activating))
                    switcher.TryActivateSelectedRender();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Console.WriteLine("External Bluetooth preparation warning: " + ex.Message); }

            if (!IsSession(expectedSession, ServerSessionState.Activating)) return;
            Console.WriteLine($"Session {expectedSession}: injecting external context · source={source}");
            await ContextAudioInjector.PlayIntoVirtualCableAsync(contextSource, options.VirtualCableInputName, token);
            Console.WriteLine($"Session {expectedSession}: external context delivered");

            if (!IsSession(expectedSession, ServerSessionState.Activating)) return;
            switcher.MarkListening();
            await SetStateAsync(ServerSessionState.Listening, source + "_context_delivered", expectedSession);
            StartDownlink(expectedSession);
            Console.WriteLine($"Session {expectedSession}: LISTENING after external context");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Session {expectedSession}: external conversation error: {ex.Message}");
            if (IsSession(expectedSession, ServerSessionState.Activating))
                await FailActivationAsync(expectedSession, source + "_error");
            else if (IsSession(expectedSession, ServerSessionState.Listening))
                await EndSessionAsync(source + "_error");
        }
    }
}
