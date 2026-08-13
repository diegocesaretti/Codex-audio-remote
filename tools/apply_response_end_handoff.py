from pathlib import Path


def replace_once(text, old, new, label):
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f'Patch anchor not found: {label}')
    return text.replace(old, new, 1)

# Add a lightweight voice-activity tracker to the actual downlink audio.
p = Path('windows/CodexAudioRemote.Server/LoopbackDownlink.cs')
s = p.read_text(encoding='utf-8')

s = replace_once(s,
'''    long droppedPackets;\n    long lastCaptureTicks;''',
'''    long droppedPackets;\n    long lastCaptureTicks;\n    long lastSpeechTicks;\n    int speechSeen;\n    const double SpeechRmsThreshold = 420.0;''',
'downlink VAD fields')

s = replace_once(s,
'''        sendQueue.Enqueue(packet);\n        capturedPackets++;\n        queueSignal.Release();''',
'''        TrackSpeechActivity(packet);\n        sendQueue.Enqueue(packet);\n        capturedPackets++;\n        queueSignal.Release();''',
'downlink VAD enqueue')

anchor = '''    async Task SenderLoop(CancellationToken token)\n    {'''
insert = '''    void TrackSpeechActivity(byte[] packet)\n    {\n        if (packet.Length < 2) return;\n        double sumSquares = 0;\n        int samples = packet.Length / 2;\n        for (int i = 0; i + 1 < packet.Length; i += 2)\n        {\n            short sample = (short)(packet[i] | (packet[i + 1] << 8));\n            sumSquares += (double)sample * sample;\n        }\n        var rms = Math.Sqrt(sumSquares / Math.Max(1, samples));\n        if (rms >= SpeechRmsThreshold)\n        {\n            Interlocked.Exchange(ref speechSeen, 1);\n            Interlocked.Exchange(ref lastSpeechTicks, Stopwatch.GetTimestamp());\n        }\n    }\n\n    public async Task<bool> WaitForSpeechThenSilenceAsync(int speechStartTimeoutMs, int silenceMs, CancellationToken token)\n    {\n        var started = Stopwatch.GetTimestamp();\n        var timeoutTicks = speechStartTimeoutMs * (double)Stopwatch.Frequency / 1000.0;\n        while (!token.IsCancellationRequested && Volatile.Read(ref speechSeen) == 0)\n        {\n            if (Stopwatch.GetTimestamp() - started >= timeoutTicks)\n            {\n                Console.WriteLine($\"Response VAD: no speech detected within {speechStartTimeoutMs} ms\");\n                return false;\n            }\n            await Task.Delay(50, token);\n        }\n\n        Console.WriteLine(\"Response VAD: speech detected; waiting for end-of-response silence\");\n        while (!token.IsCancellationRequested)\n        {\n            var last = Volatile.Read(ref lastSpeechTicks);\n            if (last != 0)\n            {\n                var quietMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;\n                if (quietMs >= silenceMs)\n                {\n                    Console.WriteLine($\"Response VAD: end detected after {quietMs:F0} ms silence\");\n                    return true;\n                }\n            }\n            await Task.Delay(50, token);\n        }\n        return false;\n    }\n\n    async Task SenderLoop(CancellationToken token)\n    {'''
s = replace_once(s, anchor, insert, 'downlink VAD methods')
p.write_text(s, encoding='utf-8')

# After the HA context is injected, use the response audio itself as the primary handoff signal.
p = Path('windows/CodexAudioRemote.Server/ExternalConversationHub.cs')
s = p.read_text(encoding='utf-8')
old = '''        var becameBusy = await WaitForBusyAsync(15000);\n        Console.WriteLine(becameBusy ? \"Codex processing external context\" : \"No explicit busy transition detected; watching for stable ready state\");\n        var ready = await WaitForStableReadyAsync(60000);\n        Console.WriteLine(ready ? \"Codex stable-ready after external context\" : \"Stable-ready timeout; enabling Android mic as fallback\");\n\n        downlink?.Dispose();\n        downlink = null;\n        switcher.MarkListening();\n        ExternalConversationHub.SetSuppressCodexEvents(false);\n        await Task.Delay(180, cts.Token);\n        await SendJson(new { type = \"codex_listening\", source = \"external_context\", readyConfirmed = ready });\n        Console.WriteLine(\"External conversation READY · Android microphone enabled\");'''
new = '''        var becameBusy = await WaitForBusyAsync(12000);\n        Console.WriteLine(becameBusy ? \"Codex processing external context\" : \"No explicit busy transition detected; response VAD remains primary\");\n\n        // Primary signal: listen to the exact audio being sent to Android. As soon as Codex has\n        // spoken and stays quiet for ~0.9 s, its response is over. This is substantially faster\n        // and more reliable than waiting for accessibility/microphone state transitions.\n        var responseEnded = downlink != null && await downlink.WaitForSpeechThenSilenceAsync(45000, 900, cts.Token);\n        bool ready;\n        if (responseEnded)\n        {\n            ready = true;\n            Console.WriteLine(\"HA handoff: response audio ended; opening Android mic\");\n        }\n        else\n        {\n            Console.WriteLine(\"HA handoff: response VAD unavailable; using short 4 s readiness fallback\");\n            ready = await WaitForStableReadyAsync(4000);\n        }\n\n        // Allow the Android jitter buffer to drain the final packets before its microphone opens.\n        await Task.Delay(450, cts.Token);\n        downlink?.Dispose();\n        downlink = null;\n        switcher.MarkListening();\n        ExternalConversationHub.SetSuppressCodexEvents(false);\n        await SendJson(new { type = \"codex_listening\", source = \"external_context\", readyConfirmed = ready, handoff = responseEnded ? \"audio_end\" : \"4s_fallback\" });\n        Console.WriteLine(\"External conversation READY · Android microphone enabled\");'''
s = replace_once(s, old, new, 'external response-audio handoff')
p.write_text(s, encoding='utf-8')

print('Response-end audio handoff patch ready for build')
