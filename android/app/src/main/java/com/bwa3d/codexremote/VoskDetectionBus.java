package com.bwa3d.codexremote;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;

/** In-process diagnostic stream for tuning Vosk wake detection. */
final class VoskDetectionBus {
    interface Listener { void onVoskDetection(Event event); }

    static final class Event {
        final long timestampMs;
        final String text;
        final String target;
        final boolean isFinal;
        final double confidence;
        final long durationMs;
        final int rms;
        final int noiseFloor;
        final boolean accepted;
        final String reason;

        Event(long timestampMs, String text, String target, boolean isFinal, double confidence,
              long durationMs, int rms, int noiseFloor, boolean accepted, String reason) {
            this.timestampMs = timestampMs;
            this.text = text == null ? "" : text;
            this.target = target == null ? "" : target;
            this.isFinal = isFinal;
            this.confidence = confidence;
            this.durationMs = durationMs;
            this.rms = rms;
            this.noiseFloor = noiseFloor;
            this.accepted = accepted;
            this.reason = reason == null ? "" : reason;
        }
    }

    private static final int HISTORY_LIMIT = 40;
    private static final CopyOnWriteArrayList<Listener> LISTENERS = new CopyOnWriteArrayList<>();
    private static final ArrayDeque<Event> HISTORY = new ArrayDeque<>();

    private VoskDetectionBus() { }

    static void publish(Event event) {
        if (event == null) return;
        synchronized (HISTORY) {
            HISTORY.addLast(event);
            while (HISTORY.size() > HISTORY_LIMIT) HISTORY.removeFirst();
        }
        for (Listener listener : LISTENERS) {
            try { listener.onVoskDetection(event); } catch (Exception ignored) { }
        }
    }

    static void addListener(Listener listener) {
        if (listener != null && !LISTENERS.contains(listener)) LISTENERS.add(listener);
    }

    static void removeListener(Listener listener) {
        if (listener != null) LISTENERS.remove(listener);
    }

    static List<Event> snapshot() {
        synchronized (HISTORY) { return new ArrayList<>(HISTORY); }
    }

    static void clear() {
        synchronized (HISTORY) { HISTORY.clear(); }
    }
}
