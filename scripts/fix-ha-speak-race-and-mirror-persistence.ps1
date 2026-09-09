$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server'
$peerPath = Join-Path $root 'CodexOAuthWebRtcPeer.cs'
$haUiPath = Join-Path $root 'HomeAssistantSettingsPanel.cs'

# -----------------------------------------------------------------------------
# /api/speak startup race
# A newly-created WebRTC session can report LISTENING before the oai-events data
# channel is open and before Frameless has emitted session.started. Direct
# session.context.append must wait for both conditions.
# -----------------------------------------------------------------------------
$peer = Get-Content -LiteralPath $peerPath -Raw

if (-not $peer.Contains('let speakableSessionStarted = false;')) {
    $marker = '  let statsTimer = null;'
    if (-not $peer.Contains($marker)) { throw 'WebRTC stats variable marker missing.' }
    $peer = $peer.Replace($marker, $marker + "`r`n  let speakableSessionStarted = false;")
}

if (-not $peer.Contains('speakableSessionStarted = false; // reset')) {
    $marker = '    statsTimer = null;'
    if (-not $peer.Contains($marker)) { throw 'WebRTC reset marker missing.' }
    $peer = $peer.Replace($marker, $marker + "`r`n    speakableSessionStarted = false; // reset")
}

$oldMessage = @'
        dc.onmessage = e => {
          try {
            const payload = JSON.parse(String(e.data || '{}'));
            event('datachannel-message', payload.type || 'unknown');
          } catch (_) {
            event('datachannel-message', 'non-json');
          }
        };
'@
$newMessage = @'
        dc.onmessage = e => {
          try {
            const payload = JSON.parse(String(e.data || '{}'));
            const type = payload.type || 'unknown';
            if (type === 'session.started') speakableSessionStarted = true;
            event('datachannel-message', type);
          } catch (_) {
            event('datachannel-message', 'non-json');
          }
        };
'@
if ($peer.Contains($oldMessage)) {
    $peer = $peer.Replace($oldMessage, $newMessage)
} elseif (-not $peer.Contains("if (type === 'session.started') speakableSessionStarted = true;")) {
    throw 'Could not patch datachannel session.started tracking.'
}

$oldSpeakGuard = "        if (!dc || dc.readyState !== 'open') throw new Error('Realtime datachannel is not open.');"
$newSpeakGuard = @'
        const readyDeadline = Date.now() + 8000;
        while (Date.now() < readyDeadline) {
          if (dc && dc.readyState === 'open' && speakableSessionStarted) break;
          await new Promise(resolve => setTimeout(resolve, 50));
        }
        if (!dc || dc.readyState !== 'open') throw new Error('Realtime datachannel did not open before speak timeout.');
        if (!speakableSessionStarted) throw new Error('Realtime session.started was not received before speak timeout.');
'@
if ($peer.Contains($oldSpeakGuard)) {
    $peer = $peer.Replace($oldSpeakGuard, $newSpeakGuard.TrimEnd())
} elseif (-not $peer.Contains('Realtime session.started was not received before speak timeout.')) {
    throw 'Could not patch speakText readiness guard.'
}

Set-Content -LiteralPath $peerPath -Value $peer -Encoding utf8 -NoNewline

# -----------------------------------------------------------------------------
# Home Assistant mirror toggle persistence
# Mirror enable/disable is a live preference: persist it immediately on change,
# and also when running a Settings diagnostic, so closing the dialog cannot
# silently revert the checkbox.
# -----------------------------------------------------------------------------
$ui = Get-Content -LiteralPath $haUiPath -Raw

if (-not $ui.Contains('HA mirror preference saved')) {
    $marker = '        mirrorEnabled.SetBounds(18, y, 670, 26); Controls.Add(mirrorEnabled); y += 31;'
    if (-not $ui.Contains($marker)) { throw 'HA mirror checkbox marker missing.' }
    $replacement = @'
        mirrorEnabled.SetBounds(18, y, 670, 26); Controls.Add(mirrorEnabled); y += 31;
        mirrorEnabled.CheckedChanged += (_, _) =>
        {
            RealtimeMirrorSettings.HomeAssistantMirrorEnabled = mirrorEnabled.Checked;
            Console.WriteLine($"HA mirror preference saved · enabled={mirrorEnabled.Checked}");
        };
'@
    $ui = $ui.Replace($marker, $replacement.TrimEnd())
}

if (-not $ui.Contains('RealtimeMirrorSettings.HomeAssistantMirrorEnabled = mirrorEnabled.Checked; // diagnostic persistence')) {
    $marker = '        RealtimeMirrorSettings.HomeAssistantMirrorAnnounce = announce.Checked;'
    $replacement = @'
        RealtimeMirrorSettings.HomeAssistantMirrorEnabled = mirrorEnabled.Checked; // diagnostic persistence
        RealtimeMirrorSettings.HomeAssistantMirrorAnnounce = announce.Checked;
'@
    # SaveConnectionForTests contains one occurrence and SaveToSettings contains another.
    # Insert only where not already immediately preceded by the regular SaveToSettings assignment.
    $saveTestStart = $ui.IndexOf('    void SaveConnectionForTests()', [StringComparison]::Ordinal)
    if ($saveTestStart -lt 0) { throw 'SaveConnectionForTests marker missing.' }
    $announceIndex = $ui.IndexOf($marker, $saveTestStart, [StringComparison]::Ordinal)
    if ($announceIndex -lt 0) { throw 'HA diagnostic announce marker missing.' }
    $ui = $ui.Substring(0, $announceIndex) + $replacement.TrimEnd() + $ui.Substring($announceIndex + $marker.Length)
}

Set-Content -LiteralPath $haUiPath -Value $ui -Encoding utf8 -NoNewline

# Validation
$peerCheck = Get-Content -LiteralPath $peerPath -Raw
$uiCheck = Get-Content -LiteralPath $haUiPath -Raw
if ($peerCheck -notmatch 'speakableSessionStarted') { throw 'Speak readiness state missing.' }
if ($peerCheck -notmatch 'session\.started') { throw 'session.started readiness tracking missing.' }
if ($peerCheck -notmatch 'readyDeadline') { throw 'Speak readiness wait missing.' }
if ($uiCheck -notmatch 'HA mirror preference saved') { throw 'Immediate HA mirror persistence missing.' }
if ($uiCheck -notmatch 'diagnostic persistence') { throw 'HA diagnostic persistence missing.' }
Write-Host 'Fixed /api/speak startup race and Home Assistant mirror toggle persistence.'
