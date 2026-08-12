from pathlib import Path
p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')
s = s.replace('''        catch (Exception ex)\n        {\n            CancelSmartClose();\n            codexInputRecorder?.Dispose(); audioSink?.Dispose(); downlink?.Dispose();''', '''        catch (Exception ex)\n        {\n            codexInputRecorder?.Dispose(); audioSink?.Dispose(); downlink?.Dispose();''')
p.write_text(s, encoding='utf-8')
print('Smart close scope fixed')
