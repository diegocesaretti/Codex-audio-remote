from pathlib import Path


def replace_once(text, old, new, label):
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)


# Android: accept codex_listening only once for each activating event.
# This is deliberately independent from the streaming flag: if an audio error stops
# streaming while duplicate codex_listening events are already queued, they still must
# not start a second AudioRecord/uplink session.
p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

s = replace_once(s,
'''    private boolean wakeReceiverRegistered;''',
'''    private boolean codexListeningAccepted;\n    private boolean wakeReceiverRegistered;''',
'android listening dedupe field')

s = replace_once(s,
'''                connected = true; socket = webSocket; reconnectAttempt = 0;''',
'''                connected = true; socket = webSocket; reconnectAttempt = 0; codexListeningAccepted = false;''',
'android reset dedupe on websocket open')

s = replace_once(s,
'''                case "activating": stopWakeRecognition(); overlay.clearTranscript(); overlay.show("Activando…"); updateNotification("Activando Codex…"); break;''',
'''                case "activating": codexListeningAccepted = false; stopWakeRecognition(); overlay.clearTranscript(); overlay.show("Activando…"); updateNotification("Activando Codex…"); break;''',
'android reset dedupe on activation')

s = replace_once(s,
'''                case "codex_listening":\n                    if (gracefulEndPending) {''',
'''                case "codex_listening":\n                    if (codexListeningAccepted) {\n                        AndroidDebugLog.log("Duplicate codex_listening ignored for current activation");\n                        break;\n                    }\n                    codexListeningAccepted = true;\n                    if (gracefulEndPending) {''',
'android dedupe codex listening')

p.write_text(s, encoding='utf-8')


# Windows: one audio_start is allowed per wake activation. A duplicate used to tear
# down/recreate WASAPI + loopback while the first session was already running, which is
# especially risky while a Bluetooth render endpoint is renegotiating.
p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

s = replace_once(s,
'''            int uplinkBytesPerSecond = 32000;''',
'''            int uplinkBytesPerSecond = 32000;\n            bool audioStartAccepted = false;''',
'windows audio start guard field')

s = replace_once(s,
'''                    case "wake":\n                        // Codex has its own intrinsic activation delay.''',
'''                    case "wake":\n                        audioStartAccepted = false;\n                        // Codex has its own intrinsic activation delay.''',
'windows reset audio guard')

s = replace_once(s,
'''                    case "audio_start":\n                        gracefulHold = false;\n                        CancelSmartClose();\n                        audioBytes = 0;''',
'''                    case "audio_start":\n                        if (audioStartAccepted)\n                        {\n                            Console.WriteLine("Duplicate audio_start ignored for current activation");\n                            break;\n                        }\n                        audioStartAccepted = true;\n                        gracefulHold = false;\n                        CancelSmartClose();\n                        audioBytes = 0;''',
'windows reject duplicate audio start')

s = replace_once(s,
'''                Console.WriteLine("Codex microphone ACTIVE");''',
'''                Console.WriteLine("Codex microphone ACTIVE · -> codex_listening");''',
'windows listening announcement log')

s = replace_once(s,
'''            Console.WriteLine($"Client error: {ex.Message}");''',
'''            Console.WriteLine($"Client error: {ex}");''',
'windows full websocket exception')

p.write_text(s, encoding='utf-8')

print('Voice session duplicate guards ready for build')
