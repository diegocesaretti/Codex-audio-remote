from pathlib import Path

p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

changes = 0

# Vosk remains loaded for wake-word recognition and optional response transcript,
# but it must not inspect the live uplink to decide when a conversation ends.
for old in (
    '                startPhraseDetector(finalRate);\n',
    '                        offerPhraseAudio(buffer, read);\n',
):
    if old in s:
        s = s.replace(old, '', 1)
        changes += 1

# Make the disabled state obvious in runtime logs when starting a mic session.
anchor = '                updateNotification("Codex escuchando · " + audioSourceName(finalSource)'
if anchor in s and 'STT session-end detector DISABLED' not in s:
    s = s.replace(
        anchor,
        '                AndroidDebugLog.log("STT session-end detector DISABLED · Vosk is wake/transcript only");\n' + anchor,
        1,
    )
    changes += 1

if changes < 2:
    raise RuntimeError('Could not disable all STT session-end hooks')

p.write_text(s, encoding='utf-8')
print('STT-driven session ending disabled; Vosk kept for wake/transcript only')
