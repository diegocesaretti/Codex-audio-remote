from pathlib import Path

p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')
old = 'downlink = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm));'
new = 'downlink = new LoopbackDownlink(async pcm => await SendBinary(socket, sendGate, pcm), DownlinkDeviceSettings.SelectedDeviceId);'
if new not in s:
    if old not in s:
        raise RuntimeError('LoopbackDownlink constructor anchor not found')
    s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
print('Downlink selector wired into Program.cs')
