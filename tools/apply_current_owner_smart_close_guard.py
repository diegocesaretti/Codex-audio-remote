from pathlib import Path

p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

old = '''            async Task ExecuteVoiceClose(string reason, string source)\n            {\n                gracefulHold = false;'''
new = '''            async Task ExecuteVoiceClose(string reason, string source)\n            {\n                if (!IsCurrentOwner())\n                {\n                    Console.WriteLine($"Ignoring stale smart Voice close · reason={reason} · source={source}");\n                    return;\n                }\n                gracefulHold = false;'''

if new not in s:
    if old not in s:
        raise RuntimeError('Patch anchor not found: ExecuteVoiceClose owner guard')
    s = s.replace(old, new, 1)

p.write_text(s, encoding='utf-8')
print('Current-owner smart-close guard applied')
