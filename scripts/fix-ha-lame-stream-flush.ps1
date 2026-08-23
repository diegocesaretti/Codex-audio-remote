$ErrorActionPreference = 'Stop'

$path = Join-Path $PSScriptRoot '..\windows\CodexAudioRemote.Server\RealtimeSecondaryAudioMirror.cs'
$source = Get-Content -LiteralPath $path -Raw

# NAudio.Lame's LameMP3FileWriter.Flush() is terminal: it finalizes LAME and sets
# its output stream to null. Calling it after every PCM chunk makes the first
# chunk succeed and every subsequent Write fail with "Output stream closed."
# For a live stream, keep the encoder open for the whole session/test and let
# Dispose() perform the single final Flush when the producer ends.
$before = ([regex]::Matches($source, 'encoder\.Flush\(\);')).Count
if ($before -lt 1) { throw 'No per-chunk LAME Flush calls found to remove.' }
$source = $source.Replace('                encoder.Flush();' + "`r`n", '')
$source = $source.Replace('            encoder.Flush();' + "`r`n", '')
$source = $source.Replace('                encoder.Flush();' + "`n", '')
$source = $source.Replace('            encoder.Flush();' + "`n", '')

# Add explicit diagnostics to make the next runtime log unambiguous.
$runMarker = '            using var encoder = new LameMP3FileWriter(sink, new WaveFormat(16000, 16, 1), 64);'
if ($source.Contains($runMarker) -and -not $source.Contains('Realtime HA MP3 encoder streaming · continuous mode')) {
    $source = $source.Replace($runMarker, $runMarker + "`r`n            Console.WriteLine(\"Realtime HA MP3 encoder streaming · continuous mode\");")
}

$remaining = ([regex]::Matches($source, 'encoder\.Flush\(\);')).Count
if ($remaining -ne 0) { throw "Per-chunk LAME Flush calls remain: $remaining" }

Set-Content -LiteralPath $path -Value $source -Encoding utf8 -NoNewline
Write-Host "Removed $before terminal LAME Flush call(s); HA MP3 encoder now remains open until producer disposal."
