from pathlib import Path


def replace_once(text, old, new, label):
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f'Patch anchor not found: {label}')
    return text.replace(old, new, 1)

p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')

# Add render endpoint lookup helper.
s = replace_once(s,
'''    public static string? GetDefaultCaptureId(Role role)\n    {\n        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID; } catch { return null; }\n    }''',
'''    public static string? GetDefaultCaptureId(Role role)\n    {\n        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID; } catch { return null; }\n    }\n    public static string? GetDefaultRenderId(Role role)\n    {\n        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Render, role).ID; } catch { return null; }\n    }''',
'render default helper')

# Extend AudioDeviceSwitcher with independent render recovery.
s = replace_once(s,
'''    readonly string virtualMicName; readonly int restoreDelayMs;\n    readonly string recoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-restore.json");''',
'''    readonly string virtualMicName; readonly int restoreDelayMs;\n    readonly string recoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-restore.json");\n    readonly string renderRecoveryPath = Path.Combine(AppContext.BaseDirectory, "audio-render-restore.json");\n    SavedDefaults? savedRender;''',
'render recovery fields')

# On activation, switch output to the selected physical render device if configured.
s = replace_once(s,
'''                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Console); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Multimedia); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Communications);\n                RemoteMicIsActive = true; Console.WriteLine($"Default capture temporarily switched to: {target.FriendlyName}"); return true;''',
'''                PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Console); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Multimedia); PolicyConfig.SetDefaultEndpoint(target.ID, PolicyRole.Communications);\n\n                var renderId = DownlinkDeviceSettings.SelectedDeviceId;\n                if (!string.IsNullOrWhiteSpace(renderId))\n                {\n                    using var render = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)\n                        .FirstOrDefault(d => string.Equals(d.ID, renderId, StringComparison.Ordinal));\n                    if (render is not null && !DownlinkDeviceSettings.IsUnsafe(render.FriendlyName))\n                    {\n                        savedRender = new SavedDefaults(AudioDeviceManager.GetDefaultRenderId(Role.Console), AudioDeviceManager.GetDefaultRenderId(Role.Multimedia), AudioDeviceManager.GetDefaultRenderId(Role.Communications));\n                        File.WriteAllText(renderRecoveryPath, JsonSerializer.Serialize(savedRender));\n                        PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Console);\n                        PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Multimedia);\n                        PolicyConfig.SetDefaultEndpoint(render.ID, PolicyRole.Communications);\n                        Console.WriteLine($"Codex output temporarily switched to: {render.FriendlyName}");\n                    }\n                }\n\n                RemoteMicIsActive = true; Console.WriteLine($"Default capture temporarily switched to: {target.FriendlyName}"); return true;''',
'activate selected render')

# Restore render defaults along with capture defaults.
s = replace_once(s,
'''            try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }\n            saved = null; RemoteMicIsActive = false; State = AudioSessionState.Idle;\n            if (restored) Console.WriteLine("Default capture restored to the devices selected before the conversation.");''',
'''            try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }\n\n            bool renderRestored = false;\n            try\n            {\n                if (savedRender is null && File.Exists(renderRecoveryPath)) savedRender = JsonSerializer.Deserialize<SavedDefaults>(File.ReadAllText(renderRecoveryPath));\n                if (savedRender is not null)\n                {\n                    if (!string.IsNullOrWhiteSpace(savedRender.Console)) { PolicyConfig.SetDefaultEndpoint(savedRender.Console, PolicyRole.Console); renderRestored = true; }\n                    if (!string.IsNullOrWhiteSpace(savedRender.Multimedia)) { PolicyConfig.SetDefaultEndpoint(savedRender.Multimedia, PolicyRole.Multimedia); renderRestored = true; }\n                    if (!string.IsNullOrWhiteSpace(savedRender.Communications)) { PolicyConfig.SetDefaultEndpoint(savedRender.Communications, PolicyRole.Communications); renderRestored = true; }\n                }\n            }\n            catch (Exception ex) { Console.WriteLine($"Render restore warning: {ex.Message}"); }\n            try { if (File.Exists(renderRecoveryPath)) File.Delete(renderRecoveryPath); } catch { }\n            savedRender = null;\n\n            saved = null; RemoteMicIsActive = false; State = AudioSessionState.Idle;\n            if (restored) Console.WriteLine("Default capture restored to the devices selected before the conversation.");\n            if (renderRestored) Console.WriteLine("Default playback restored to the devices selected before the conversation.");''',
'restore selected render')

# Recovery should also work after a crash where only render state was left behind.
s = replace_once(s,
'''    public async Task TryRecoverAsync() { if (!File.Exists(recoveryPath)) return; Console.WriteLine("Recovering audio defaults from previous run..."); RestoreNow(); await Task.Delay(100); }''',
'''    public async Task TryRecoverAsync()\n    {\n        if (!File.Exists(recoveryPath) && !File.Exists(renderRecoveryPath)) return;\n        Console.WriteLine("Recovering audio defaults from previous run...");\n        RestoreNow();\n        await Task.Delay(100);\n    }''',
'recover render defaults')

p.write_text(s, encoding='utf-8')

# Clarify the existing selector: it now controls both local playback and Android downlink capture.
p = Path('windows/CodexAudioRemote.Server/DownlinkDeviceSettings.cs')
s = p.read_text(encoding='utf-8')
s = s.replace('Text = "Audio de respuesta / Downlink",', 'Text = "Salida de Codex / Downlink",')
s = s.replace('Text = "Elegí dónde se escucha Codex. CABLE/VB-Audio no aparecen para evitar loops de eco."', 'Text = "Elegí dónde habla Codex en la PC y desde dónde se captura la respuesta para Android. Bluetooth, HDMI, parlantes y auriculares son válidos. CABLE/VB-Audio se bloquean para evitar loops."')
s = s.replace('ClientSize = new Size(560, 170)', 'ClientSize = new Size(560, 190)')
s = s.replace('var combo = new ComboBox { Left = 12, Top = 54, Width = 530, DropDownStyle = ComboBoxStyle.DropDownList };', 'var combo = new ComboBox { Left = 12, Top = 72, Width = 530, DropDownStyle = ComboBoxStyle.DropDownList };')
s = s.replace('var save = new Button { Left = 372, Top = 116, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };', 'var save = new Button { Left = 372, Top = 136, Width = 80, Text = "Guardar", DialogResult = DialogResult.OK };')
s = s.replace('var cancel = new Button { Left = 462, Top = 116, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };', 'var cancel = new Button { Left = 462, Top = 136, Width = 80, Text = "Cancelar", DialogResult = DialogResult.Cancel };')
p.write_text(s, encoding='utf-8')

print('Selected response device now routes both Codex playback and Android downlink capture')
