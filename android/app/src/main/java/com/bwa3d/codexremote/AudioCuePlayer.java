package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.media.MediaMetadataRetriever;
import android.media.MediaPlayer;
import android.net.Uri;
import android.provider.OpenableColumns;

import java.util.ArrayList;
import java.util.List;
import java.util.Random;

/** Local lifecycle sounds. Wake can randomly choose among up to three persisted user audio URIs. */
public final class AudioCuePlayer {
    public enum Cue { WAKE, UPLINK, LISTEN_END, END }

    private static final int SAMPLE_RATE = 44100;
    private static final String PREFS = "settings";
    private static final Random RANDOM = new Random();
    private static int lastWakeCandidate = -1;

    private AudioCuePlayer() { }

    public static void play(Context context, Cue cue) {
        if (context == null || cue == null) return;
        if (cue == Cue.WAKE) {
            playWakeRandom(context);
            return;
        }
        Context app = context.getApplicationContext();
        SharedPreferences prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (!isEnabled(prefs, cue)) return;
        int volume = Math.max(0, Math.min(100, prefs.getInt("cue_volume", 45)));
        if (volume <= 0) return;

        String customUri = prefs.getString(uriPrefKey(cue), "");
        if (customUri != null && !customUri.trim().isEmpty()) {
            playCustom(app, cue, Uri.parse(customUri), volume);
            return;
        }
        new Thread(() -> playBuiltInBlocking(cue, volume), "AudioCue-" + cue.name()).start();
    }

    /** Plays one random configured wake greeting and returns the local mic suppression duration. */
    public static int playWakeRandom(Context context) {
        if (context == null) return 0;
        Context app = context.getApplicationContext();
        SharedPreferences prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (!isEnabled(prefs, Cue.WAKE)) return 0;
        int volume = Math.max(0, Math.min(100, prefs.getInt("cue_volume", 45)));
        if (volume <= 0) return 0;

        List<WakeCandidate> candidates = configuredWakeCandidates(prefs);
        if (candidates.isEmpty()) {
            new Thread(() -> playBuiltInBlocking(Cue.WAKE, volume), "AudioCue-WAKE").start();
            return builtInDurationMs(Cue.WAKE) + 100;
        }

        int choice;
        synchronized (AudioCuePlayer.class) {
            choice = RANDOM.nextInt(candidates.size());
            if (candidates.size() > 1 && choice == lastWakeCandidate)
                choice = (choice + 1 + RANDOM.nextInt(candidates.size() - 1)) % candidates.size();
            lastWakeCandidate = choice;
        }
        WakeCandidate selected = candidates.get(choice);
        Uri uri = Uri.parse(selected.uri);
        playCustom(app, Cue.WAKE, uri, volume);
        int suppressMs = durationForUri(app, uri, 450);
        AndroidDebugLog.log("Wake greeting random · slot=" + selected.slot + " · candidates=" + candidates.size() + " · suppress=" + suppressMs + "ms");
        return suppressMs;
    }

    /** Test one specific wake slot without affecting random selection. */
    public static void playWakeSlot(Context context, int slot) {
        if (context == null) return;
        Context app = context.getApplicationContext();
        SharedPreferences prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        int volume = Math.max(0, Math.min(100, prefs.getInt("cue_volume", 45)));
        String value = prefs.getString(wakeUriPrefKey(slot), "");
        if (value != null && !value.trim().isEmpty()) {
            playCustom(app, Cue.WAKE, Uri.parse(value), volume);
        } else if (slot == 1) {
            new Thread(() -> playBuiltInBlocking(Cue.WAKE, volume), "AudioCue-WAKE-Test").start();
        }
    }

    /** How long mic uplink should be locally suppressed so a cue is not fed back into Realtime. */
    public static int suggestedSuppressionMs(Context context, Cue cue) {
        if (context == null || cue == null) return 220;
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String customUri = prefs.getString(uriPrefKey(cue), "");
        if (customUri == null || customUri.trim().isEmpty()) return builtInDurationMs(cue) + 100;
        return durationForUri(context, Uri.parse(customUri), 350);
    }

    private static int durationForUri(Context context, Uri uri, int fallback) {
        MediaMetadataRetriever retriever = new MediaMetadataRetriever();
        try {
            retriever.setDataSource(context, uri);
            String duration = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION);
            int ms = duration == null ? fallback : Integer.parseInt(duration);
            return Math.max(180, Math.min(5000, ms + 90));
        } catch (Exception ignored) {
            return fallback;
        } finally {
            try { retriever.release(); } catch (Exception ignored) { }
        }
    }

    public static String uriPrefKey(Cue cue) {
        switch (cue) {
            case WAKE: return wakeUriPrefKey(1);
            case UPLINK: return "cue_uplink_uri";
            case LISTEN_END: return "cue_listen_end_uri";
            case END:
            default: return "cue_end_uri";
        }
    }

    public static String wakeUriPrefKey(int slot) {
        if (slot == 2) return "cue_wake_uri_2";
        if (slot == 3) return "cue_wake_uri_3";
        return "cue_wake_uri";
    }

    public static void setCustomUri(Context context, Cue cue, Uri uri) {
        if (context == null || cue == null || uri == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(uriPrefKey(cue), uri.toString()).apply();
    }

    public static void setCustomWakeUri(Context context, int slot, Uri uri) {
        if (context == null || uri == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(wakeUriPrefKey(slot), uri.toString()).apply();
    }

    public static void clearCustomUri(Context context, Cue cue) {
        if (context == null || cue == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().remove(uriPrefKey(cue)).apply();
    }

    public static void clearCustomWakeUri(Context context, int slot) {
        if (context == null) return;
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().remove(wakeUriPrefKey(slot)).apply();
    }

    public static boolean hasCustomWakeUri(Context context, int slot) {
        if (context == null) return false;
        String value = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString(wakeUriPrefKey(slot), "");
        return value != null && !value.trim().isEmpty();
    }

    public static String describeSelection(Context context, Cue cue) {
        if (cue == Cue.WAKE) return describeWakeSelection(context, 1);
        if (context == null || cue == null) return "Chime interno";
        String value = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString(uriPrefKey(cue), "");
        if (value == null || value.trim().isEmpty()) return "Chime interno";
        return describeUri(context, Uri.parse(value));
    }

    public static String describeWakeSelection(Context context, int slot) {
        if (context == null) return slot == 1 ? "Chime interno" : "Sin archivo";
        String value = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString(wakeUriPrefKey(slot), "");
        if (value == null || value.trim().isEmpty()) return slot == 1 ? "Chime interno" : "Sin archivo";
        return describeUri(context, Uri.parse(value));
    }

    private static String describeUri(Context context, Uri uri) {
        Cursor cursor = null;
        try {
            cursor = context.getContentResolver().query(uri,
                    new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null);
            if (cursor != null && cursor.moveToFirst()) {
                int index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (index >= 0) {
                    String name = cursor.getString(index);
                    if (name != null && !name.trim().isEmpty()) return name;
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (cursor != null) try { cursor.close(); } catch (Exception ignored) { }
        }
        String last = uri.getLastPathSegment();
        return last == null || last.trim().isEmpty() ? "Audio personalizado" : last;
    }

    private static List<WakeCandidate> configuredWakeCandidates(SharedPreferences prefs) {
        List<WakeCandidate> out = new ArrayList<>();
        for (int slot = 1; slot <= 3; slot++) {
            String value = prefs.getString(wakeUriPrefKey(slot), "");
            if (value != null && !value.trim().isEmpty()) out.add(new WakeCandidate(slot, value));
        }
        return out;
    }

    private static boolean isEnabled(SharedPreferences prefs, Cue cue) {
        switch (cue) {
            case WAKE: return prefs.getBoolean("cue_wake_enabled", true);
            case UPLINK: return prefs.getBoolean("cue_uplink_enabled", true);
            case LISTEN_END: return prefs.getBoolean("cue_listen_end_enabled", true);
            case END: return prefs.getBoolean("cue_end_enabled", true);
            default: return true;
        }
    }

    private static void playCustom(Context context, Cue cue, Uri uri, int volumePct) {
        new Thread(() -> {
            MediaPlayer player = null;
            try {
                player = new MediaPlayer();
                player.setAudioStreamType(AudioManager.STREAM_MUSIC);
                player.setDataSource(context, uri);
                player.setOnCompletionListener(mp -> { try { mp.release(); } catch (Exception ignored) { } });
                player.setOnErrorListener((mp, what, extra) -> {
                    AndroidDebugLog.log("Custom cue playback error · " + cue + " · what=" + what + " extra=" + extra);
                    try { mp.release(); } catch (Exception ignored) { }
                    return true;
                });
                player.prepare();
                float volume = Math.max(0f, Math.min(1f, volumePct / 100f));
                player.setVolume(volume, volume);
                player.start();
                AndroidDebugLog.log("Audio cue custom · " + cue + " · " + uri);
                player = null;
            } catch (Exception e) {
                AndroidDebugLog.log("Custom audio cue unavailable · " + cue + " · " + e.getMessage() + " · using built-in");
                if (player != null) try { player.release(); } catch (Exception ignored) { }
                playBuiltInBlocking(cue, volumePct);
            }
        }, "AudioCueCustom-" + cue.name()).start();
    }

    private static void playBuiltInBlocking(Cue cue, int volumePct) {
        AudioTrack track = null;
        try {
            short[] pcm;
            switch (cue) {
                case WAKE: pcm = sequence(volumePct, note(880.0, 42), silence(18), note(1174.7, 62)); break;
                case UPLINK: pcm = sequence(volumePct, note(1046.5, 38), silence(12), note(1396.9, 58)); break;
                case LISTEN_END: pcm = sequence(volumePct, note(932.3, 42), silence(12), note(698.5, 58)); break;
                case END:
                default: pcm = sequence(volumePct, note(784.0, 48), silence(18), note(523.3, 82)); break;
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

    private static int builtInDurationMs(Cue cue) {
        switch (cue) {
            case WAKE: return 122;
            case UPLINK: return 108;
            case LISTEN_END: return 112;
            case END:
            default: return 148;
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
            if (s.silence) { pos += count; continue; }
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

    private static final class WakeCandidate {
        final int slot;
        final String uri;
        WakeCandidate(int slot, String uri) { this.slot = slot; this.uri = uri; }
    }

    private static final class Segment {
        final double hz;
        final int ms;
        final boolean silence;
        Segment(double hz, int ms, boolean silence) { this.hz = hz; this.ms = ms; this.silence = silence; }
    }
}
