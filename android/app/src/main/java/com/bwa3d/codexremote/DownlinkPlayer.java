package com.bwa3d.codexremote;

import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;

import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

public class DownlinkPlayer implements AutoCloseable {
    /**
     * Keep the listener API backward-compatible with the protocol-v2 RemoteService used on
     * Android 6 while preserving the newer onPlayed callback. Default methods are deliberately
     * no-op so older/newer call sites can coexist without requiring platform-specific APIs.
     */
    public interface Listener {
        default void onStarted() { }
        default void onStopped() { }
        default void onAudio(byte[] pcm) { }
        default void onPlayed(byte[] pcm) { }
    }

    private static final int SAMPLE_RATE = 16000;
    private static final int BYTES_PER_MS = SAMPLE_RATE * 2 / 1000;
    private final ArrayBlockingQueue<byte[]> queue = new ArrayBlockingQueue<>(96);
    private final AtomicInteger queuedBytes = new AtomicInteger();
    private final AtomicBoolean running = new AtomicBoolean(true);
    private final int prebufferBytes;
    private final Listener listener;
    private final Thread thread;
    private AudioTrack track;
    private long packetsIn;
    private long packetsPlayed;
    private long underruns;
    private long dropped;

    public DownlinkPlayer(int prebufferMs, Listener listener) {
        this.prebufferBytes = Math.max(80, Math.min(500, prebufferMs)) * BYTES_PER_MS;
        this.listener = listener;
        thread = new Thread(this::loop, "DownlinkPlayer");
        thread.setPriority(Thread.MAX_PRIORITY);
        thread.start();
    }

    public void enqueue(byte[] pcm) {
        if (!running.get() || pcm == null || pcm.length == 0) return;
        byte[] copy = java.util.Arrays.copyOf(pcm, pcm.length);
        while (!queue.offer(copy)) {
            byte[] old = queue.poll();
            if (old == null) break;
            queuedBytes.addAndGet(-old.length);
            dropped++;
        }
        queuedBytes.addAndGet(copy.length);
        packetsIn++;
    }

    private void ensureTrack() {
        if (track != null) return;
        int min = AudioTrack.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
        int buffer = Math.max(Math.max(min * 4, 16384), prebufferBytes * 2);
        track = new AudioTrack(AudioManager.STREAM_MUSIC, SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT, buffer, AudioTrack.MODE_STREAM);
        track.play();
        if (listener != null) {
            try { listener.onStarted(); } catch (Exception ignored) { }
        }
        AndroidDebugLog.log("DownlinkPlayer START · prebuffer=" + (prebufferBytes / BYTES_PER_MS) + "ms · AudioTrackBuffer=" + buffer);
    }

    private void loop() {
        boolean primed = false;
        long lastStats = System.currentTimeMillis();
        try {
            ensureTrack();
            while (running.get()) {
                if (!primed) {
                    while (running.get() && queuedBytes.get() < prebufferBytes) {
                        Thread.sleep(5);
                    }
                    if (!running.get()) break;
                    primed = true;
                    AndroidDebugLog.log("DownlinkPlayer primed · queued=" + queuedBytes.get() + " bytes");
                }

                byte[] pcm = queue.poll(120, TimeUnit.MILLISECONDS);
                if (pcm == null) {
                    underruns++;
                    primed = false;
                    AndroidDebugLog.log("DownlinkPlayer UNDERRUN #" + underruns + " · queued=" + queuedBytes.get());
                    continue;
                }
                queuedBytes.addAndGet(-pcm.length);
                int pos = 0;
                while (running.get() && pos < pcm.length) {
                    int written = track.write(pcm, pos, pcm.length - pos);
                    if (written <= 0) {
                        underruns++;
                        AndroidDebugLog.log("DownlinkPlayer write=" + written + " · underruns=" + underruns);
                        break;
                    }
                    pos += written;
                }
                packetsPlayed++;
                if (listener != null) {
                    try { listener.onAudio(pcm); } catch (Exception ignored) { }
                    try { listener.onPlayed(pcm); } catch (Exception ignored) { }
                }

                long now = System.currentTimeMillis();
                if (now - lastStats >= 5000) {
                    lastStats = now;
                    AndroidDebugLog.log("DownlinkPlayer stats · queued=" + queuedBytes.get() + " bytes · in=" + packetsIn + " · played=" + packetsPlayed + " · underruns=" + underruns + " · dropped=" + dropped);
                }
            }
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        } catch (Exception e) {
            AndroidDebugLog.log("DownlinkPlayer error: " + e);
        } finally {
            AudioTrack t = track;
            track = null;
            if (t != null) {
                try { t.pause(); t.flush(); t.stop(); } catch (Exception ignored) { }
                try { t.release(); } catch (Exception ignored) { }
            }
            if (listener != null) {
                try { listener.onStopped(); } catch (Exception ignored) { }
            }
            AndroidDebugLog.log("DownlinkPlayer STOP · in=" + packetsIn + " · played=" + packetsPlayed + " · underruns=" + underruns + " · dropped=" + dropped);
        }
    }

    @Override public void close() {
        if (!running.getAndSet(false)) return;
        queue.clear();
        queuedBytes.set(0);
        thread.interrupt();
        try { thread.join(300); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
    }
}
