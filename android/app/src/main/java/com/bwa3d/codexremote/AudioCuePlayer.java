package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;

/** Lightweight local chimes for wake/session lifecycle feedback. */
public final class AudioCuePlayer {
    public enum Cue { WAKE, UPLINK, END }

    private static final int SAMPLE_RATE = 44100;
    private static final String PREFS = "settings";

    private AudioCuePlayer() { }

    public static void play(Context context, Cue cue) {
        if (context == null || cue == null) return;
        Context app = context.getApplicationContext();
        SharedPreferences prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (!isEnabled(prefs, cue)) return;
        int volume = Math.max(0, Math.min(100, prefs.getInt("cue_volume", 45)));
        if (volume <= 0) return;

        new Thread(() -> playBlocking(cue, volume), "AudioCue-" + cue.name()).start();
    }

    private static boolean isEnabled(SharedPreferences prefs, Cue cue) {
        switch (cue) {
            case WAKE: return prefs.getBoolean("cue_wake_enabled", true);
            case UPLINK: return prefs.getBoolean("cue_uplink_enabled", true);
            case END: return prefs.getBoolean("cue_end_enabled", true);
            default: return true;
        }
    }

    private static void playBlocking(Cue cue, int volumePct) {
        AudioTrack track = null;
        try {
            short[] pcm;
            switch (cue) {
                case WAKE:
                    pcm = sequence(volumePct,
                            note(880.0, 42), silence(18), note(1174.7, 62));
                    break;
                case UPLINK:
                    pcm = sequence(volumePct,
                            note(1046.5, 38), silence(12), note(1396.9, 58));
                    break;
                case END:
                default:
                    pcm = sequence(volumePct,
                            note(784.0, 48), silence(18), note(523.3, 82));
                    break;
            }

            int bytes = pcm.length * 2;
            int min = AudioTrack.getMinBufferSize(SAMPLE_RATE,
                    AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
            int bufferSize = Math.max(bytes, min > 0 ? min : bytes);
            track = new AudioTrack(AudioManager.STREAM_MUSIC, SAMPLE_RATE,
                    AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize, AudioTrack.MODE_STATIC);
            if (track.getState() != AudioTrack.STATE_INITIALIZED) return;
            int written = track.write(pcm, 0, pcm.length);
            if (written <= 0) return;
            track.play();
            long durationMs = Math.max(30L, pcm.length * 1000L / SAMPLE_RATE);
            try { Thread.sleep(durationMs + 45L); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
        } catch (Exception e) {
            AndroidDebugLog.log("Audio cue error · " + cue + " · " + e.getMessage());
        } finally {
            if (track != null) {
                try { track.stop(); } catch (Exception ignored) { }
                try { track.release(); } catch (Exception ignored) { }
            }
        }
    }

    private static Segment note(double hz, int ms) { return new Segment(hz, ms, false); }
    private static Segment silence(int ms) { return new Segment(0.0, ms, true); }

    private static short[] sequence(int volumePct, Segment... segments) {
        int total = 0;
        for (Segment s : segments) total += Math.max(1, SAMPLE_RATE * s.ms / 1000);
        short[] out = new short[total];
        int pos = 0;
        double master = (volumePct / 100.0) * 0.24;

        for (Segment s : segments) {
            int count = Math.max(1, SAMPLE_RATE * s.ms / 1000);
            if (s.silence) {
                pos += count;
                continue;
            }
            int fade = Math.min(count / 3, Math.max(1, SAMPLE_RATE * 5 / 1000));
            for (int i = 0; i < count; i++) {
                double envelope = 1.0;
                if (i < fade) envelope = i / (double)fade;
                else if (i >= count - fade) envelope = (count - 1 - i) / (double)fade;
                envelope = Math.max(0.0, Math.min(1.0, envelope));
                double sample = Math.sin(2.0 * Math.PI * s.hz * i / SAMPLE_RATE);
                out[pos + i] = (short)Math.round(sample * envelope * master * Short.MAX_VALUE);
            }
            pos += count;
        }
        return out;
    }

    private static final class Segment {
        final double hz;
        final int ms;
        final boolean silence;
        Segment(double hz, int ms, boolean silence) {
            this.hz = hz;
            this.ms = ms;
            this.silence = silence;
        }
    }
}
