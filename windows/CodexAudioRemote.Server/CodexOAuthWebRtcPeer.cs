using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// Owns the Chromium WebRTC media leg for official Codex realtime. V3/Frameless
/// carries microphone and speaker audio on the WebRTC audio track; app-server is
/// used for call creation, control, transcript events and lifecycle only.
/// </summary>
internal sealed class CodexOAuthWebRtcPeer : IDisposable
{
    readonly object sync = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<string>> pending = new();
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Thread? uiThread;
    Form? hostForm;
    WebView2? webView;
    long requestId;
    bool disposed;

    public Action<byte[], int>? AudioReceived { get; set; }

    const string BridgePage = """
<!doctype html>
<html>
<body>
<script>
(() => {
  let pc = null;
  let dc = null;
  let uplinkTrack = null;
  let uplinkWriter = null;
  let uplinkTimestampUs = 0;
  let uplinkChain = Promise.resolve();
  let downlinkGeneration = 0;
  let downlinkSink = null;

  function post(obj) {
    window.chrome.webview.postMessage(JSON.stringify(obj));
  }

  function event(name, value) {
    post({ event: name, value: String(value ?? '') });
  }

  function bytesFromBase64(text) {
    const binary = atob(text);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  }

  function base64FromBytes(bytes) {
    let binary = '';
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode(...bytes.subarray(i, Math.min(bytes.length, i + chunk)));
    }
    return btoa(binary);
  }

  async function waitForIceGathering(peer, timeoutMs = 2500) {
    if (peer.iceGatheringState === 'complete') return;
    await Promise.race([
      new Promise(resolve => {
        const changed = () => {
          if (peer.iceGatheringState === 'complete') {
            peer.removeEventListener('icegatheringstatechange', changed);
            resolve();
          }
        };
        peer.addEventListener('icegatheringstatechange', changed);
      }),
      new Promise(resolve => setTimeout(resolve, timeoutMs))
    ]);
  }

  async function resetMedia() {
    downlinkGeneration++;
    try {
      if (downlinkSink) {
        downlinkSink.pause();
        downlinkSink.srcObject = null;
        downlinkSink.remove();
      }
    } catch (_) {}
    downlinkSink = null;
    try { if (uplinkWriter) await uplinkWriter.close(); } catch (_) {}
    try { if (uplinkTrack) uplinkTrack.stop(); } catch (_) {}
    uplinkWriter = null;
    uplinkTrack = null;
    uplinkTimestampUs = 0;
    uplinkChain = Promise.resolve();
  }

  async function closeCurrent(reason) {
    try { if (dc) dc.close(); } catch (_) {}
    try { if (pc) pc.close(); } catch (_) {}
    await resetMedia();
    dc = null;
    pc = null;
    event('closed', reason || 'normal');
  }

  function enqueueUplinkPcm(base64, sampleRate) {
    uplinkChain = uplinkChain.then(async () => {
      if (!uplinkWriter) return;
      const bytes = bytesFromBase64(base64);
      const evenLength = bytes.byteLength & ~1;
      if (evenLength <= 0) return;
      const frames = evenLength / 2;
      const data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + evenLength);
      const audio = new AudioData({
        format: 's16',
        sampleRate: sampleRate,
        numberOfFrames: frames,
        numberOfChannels: 1,
        timestamp: uplinkTimestampUs,
        data
      });
      uplinkTimestampUs += Math.round(frames * 1000000 / sampleRate);
      try {
        await uplinkWriter.ready;
        await uplinkWriter.write(audio);
      } finally {
        audio.close();
      }
    }).catch(err => event('audio-uplink-error', err && err.stack ? err.stack : err));
  }

  async function captureDownlink(track) {
    const generation = ++downlinkGeneration;
    try {
      if (typeof MediaStreamTrackProcessor !== 'function')
        throw new Error('MediaStreamTrackProcessor is unavailable in this WebView2 runtime.');

      // Prime the remote track with a muted autoplay sink. Chromium/WebView2 may keep a
      // remote audio track dormant when it has no consumer; the hidden muted element makes
      // the receiver actively render while MediaStreamTrackProcessor extracts PCM for Android.
      const sink = document.createElement('audio');
      sink.autoplay = true;
      sink.muted = true;
      sink.playsInline = true;
      sink.style.display = 'none';
      sink.srcObject = new MediaStream([track]);
      document.body.appendChild(sink);
      downlinkSink = sink;
      try {
        await sink.play();
        event('remote-audio-sink', 'playing-muted');
      } catch (e) {
        event('remote-audio-sink-error', e && e.stack ? e.stack : e);
      }

      const processor = new MediaStreamTrackProcessor({ track });
      const reader = processor.readable.getReader();
      event('remote-audio-track', track.id || 'audio');

      while (generation === downlinkGeneration) {
        const result = await reader.read();
        if (result.done || generation !== downlinkGeneration) break;
        const frame = result.value;
        try {
          const frames = frame.numberOfFrames;
          const channels = Math.max(1, frame.numberOfChannels);
          const mix = new Float32Array(frames);
          for (let channel = 0; channel < channels; channel++) {
            const plane = new Float32Array(frames);
            frame.copyTo(plane, { planeIndex: channel, format: 'f32-planar' });
            for (let i = 0; i < frames; i++) mix[i] += plane[i] / channels;
          }

          const pcm = new Int16Array(frames);
          for (let i = 0; i < frames; i++) {
            const value = Math.max(-1, Math.min(1, mix[i]));
            pcm[i] = value < 0 ? Math.round(value * 32768) : Math.round(value * 32767);
          }

          post({
            event: 'remote-audio',
            sampleRate: frame.sampleRate,
            data: base64FromBytes(new Uint8Array(pcm.buffer))
          });
        } finally {
          frame.close();
        }
      }
    } catch (e) {
      if (generation === downlinkGeneration)
        event('audio-downlink-error', e && e.stack ? e.stack : e);
    }
  }

  window.chrome.webview.addEventListener('message', e => {
    const message = e.data;
    if (!message || message.kind !== 'uplink-pcm') return;
    const sampleRate = Number(message.sampleRate || 48000);
    enqueueUplinkPcm(String(message.data || ''), sampleRate);
  });

  window.codexPeer = {
    async createOffer(id) {
      try {
        await closeCurrent('replaced');
        if (typeof MediaStreamTrackGenerator !== 'function' || typeof AudioData !== 'function')
          throw new Error('WebCodecs audio track generation is unavailable in this WebView2 runtime.');

        pc = new RTCPeerConnection();
        uplinkTrack = new MediaStreamTrackGenerator({ kind: 'audio' });
        uplinkWriter = uplinkTrack.writable.getWriter();
        uplinkTimestampUs = 0;
        pc.addTransceiver(uplinkTrack, { direction: 'sendrecv' });
        dc = pc.createDataChannel('oai-events');

        dc.onopen = () => event('datachannel', 'open');
        dc.onclose = () => event('datachannel', 'closed');
        pc.onconnectionstatechange = () => event('connection', pc.connectionState);
        pc.oniceconnectionstatechange = () => event('ice', pc.iceConnectionState);
        pc.onicegatheringstatechange = () => event('ice-gathering', pc.iceGatheringState);
        pc.ontrack = e => {
          if (e.track && e.track.kind === 'audio') void captureDownlink(e.track);
        };

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        await waitForIceGathering(pc);

        if (!pc.localDescription || !pc.localDescription.sdp)
          throw new Error('Chromium did not produce a local SDP offer.');

        post({ id, ok: true, result: pc.localDescription.sdp });
      } catch (e) {
        post({ id, ok: false, error: String(e && e.stack ? e.stack : e) });
      }
    },

    async applyAnswer(id, sdp) {
      try {
        if (!pc) throw new Error('WebRTC offer has not been created.');
        await pc.setRemoteDescription({ type: 'answer', sdp });
        post({ id, ok: true, result: 'ok' });
      } catch (e) {
        post({ id, ok: false, error: String(e && e.stack ? e.stack : e) });
      }
    },

    async closePeer(id, reason) {
      try {
        await closeCurrent(reason || 'normal');
        if (id) post({ id, ok: true, result: 'ok' });
      } catch (e) {
        if (id) post({ id, ok: false, error: String(e) });
      }
    }
  };

  post({ event: 'bridge-ready', value: navigator.userAgent });
})();
</script>
</body>
</html>
""";

    public async Task<string> CreateOfferAsync()
    {
        ThrowIfDisposed();
        await EnsureHostAsync();
        Console.WriteLine("Codex OAuth WebRTC: creating Chromium offer with PCM media track");
        return await InvokeAsync("createOffer");
    }

    public async Task ApplyAnswerAsync(string sdp)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sdp))
            throw new ArgumentException("SDP answer is empty.", nameof(sdp));
        await EnsureHostAsync();
        await InvokeAsync("applyAnswer", sdp);
    }

    public void ApplyAnswer(string sdp)
        => ApplyAnswerAsync(sdp).GetAwaiter().GetResult();

    public async Task PushPcmAsync(byte[] pcm, int count, int sampleRate, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (count <= 0) return;
        count = Math.Min(count, pcm.Length) & ~1;
        if (count <= 0) return;
        await EnsureHostAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var view = webView ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var payload = JsonSerializer.Serialize(new
        {
            kind = "uplink-pcm",
            sampleRate = Math.Clamp(sampleRate, 8000, 48000),
            data = Convert.ToBase64String(pcm, 0, count)
        });

        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.BeginInvoke(new Action(() =>
        {
            try
            {
                view.CoreWebView2.PostWebMessageAsJson(payload);
                dispatched.TrySetResult(true);
            }
            catch (Exception ex) { dispatched.TrySetException(ex); }
        }));
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
    }

    public void Close(string reason = "normal")
    {
        if (disposed || !ready.Task.IsCompletedSuccessfully) return;
        _ = InvokeAsync("closePeer", reason).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    async Task EnsureHostAsync()
    {
        lock (sync)
        {
            if (uiThread is null)
            {
                uiThread = new Thread(UiThreadMain)
                {
                    IsBackground = true,
                    Name = "Codex Chromium WebRTC"
                };
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();
            }
        }

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    void UiThreadMain()
    {
        try
        {
            var form = new Form
            {
                Text = "Codex Chromium WebRTC",
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(2, 2),
                Opacity = 0
            };
            var view = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            hostForm = form;
            webView = view;

            form.Load += async (_, _) =>
            {
                try
                {
                    await view.EnsureCoreWebView2Async();
                    view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                    var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                    {
                        view.NavigationCompleted -= Completed;
                        if (args.IsSuccess) navigation.TrySetResult(true);
                        else navigation.TrySetException(new InvalidOperationException("WebView2 bridge page failed to load: " + args.WebErrorStatus));
                    }
                    view.NavigationCompleted += Completed;
                    view.NavigateToString(BridgePage);
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    ready.TrySetResult(true);
                    Console.WriteLine("Chromium WebRTC bridge ready · WebView2 " + CoreWebView2Environment.GetAvailableBrowserVersionString());
                }
                catch (Exception ex)
                {
                    ready.TrySetException(new InvalidOperationException(
                        "Could not initialize WebView2. Install/update Microsoft Edge WebView2 Runtime.", ex));
                    try { form.Close(); } catch { }
                }
            };

            Application.Run(form);
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("event", out var eventProp))
            {
                var name = eventProp.GetString() ?? "event";
                if (name == "remote-audio")
                {
                    var data = root.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null;
                    var sampleRate = root.TryGetProperty("sampleRate", out var rateProp) && rateProp.TryGetInt32(out var rate)
                        ? rate
                        : 48000;
                    if (!string.IsNullOrEmpty(data)) AudioReceived?.Invoke(Convert.FromBase64String(data), sampleRate);
                    return;
                }

                var value = root.TryGetProperty("value", out var valueProp) ? valueProp.GetString() ?? "" : "";
                Console.WriteLine($"Codex Chromium WebRTC {name}: {value}");
                return;
            }

            if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var id)) return;
            if (!pending.TryRemove(id, out var waiter)) return;

            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (ok)
            {
                var result = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";
                waiter.TrySetResult(result);
            }
            else
            {
                var error = root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() ?? "Chromium WebRTC command failed." : "Chromium WebRTC command failed.";
                waiter.TrySetException(new InvalidOperationException(error));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Chromium WebRTC bridge message error: " + ex.Message);
        }
    }

    async Task<string> InvokeAsync(string command, string? argument = null)
    {
        ThrowIfDisposed();
        await EnsureHostAsync();
        var view = webView ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var id = Interlocked.Increment(ref requestId);
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;

        var script = argument is null
            ? $"window.codexPeer.{command}({id});"
            : $"window.codexPeer.{command}({id}, {JsonSerializer.Serialize(argument)});";

        try
        {
            var dispatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await view.CoreWebView2.ExecuteScriptAsync(script);
                    dispatch.TrySetResult(true);
                }
                catch (Exception ex) { dispatch.TrySetException(ex); }
            }));
            await dispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CodexOAuthWebRtcPeer));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (var waiter in pending.Values)
            waiter.TrySetCanceled();
        pending.Clear();

        var form = hostForm;
        if (form is not null && !form.IsDisposed)
        {
            try { form.BeginInvoke(new Action(() => form.Close())); } catch { }
        }
    }
}
