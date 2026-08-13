from pathlib import Path
import runpy
import sys

p = Path('windows/CodexAudioRemote.Server/CodexAudioRemote.Server.csproj')
s = p.read_text(encoding='utf-8')
line = '    <PackageReference Include="System.Speech" Version="8.0.0" />\n'
marker = '    <PackageReference Include="NAudio" Version="2.3.0" />\n'
mode = sys.argv[1] if len(sys.argv) > 1 else ''

if mode == 'hide':
    s = s.replace(line, '')
elif mode == 'restore':
    if line not in s:
        s = s.replace(marker, marker + line, 1)
else:
    raise SystemExit('usage: toggle_speech_package.py hide|restore')

p.write_text(s, encoding='utf-8')
print('System.Speech package ' + mode + ' complete')

if mode == 'hide':
    first = Path('tools/apply_connection_handoff_fix.py')
    second = Path('tools/apply_response_end_handoff.py')
    if first.exists():
        runpy.run_path(str(first), run_name='__main__')
    if second.exists():
        runpy.run_path(str(second), run_name='__main__')
