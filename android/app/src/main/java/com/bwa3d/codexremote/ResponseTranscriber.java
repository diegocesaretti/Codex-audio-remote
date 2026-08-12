package com.bwa3d.codexremote;

import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;

import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

public class ResponseTranscriber implements AutoCloseable {
    public interface Listener { void onText(String text); }

    private final Recognizer recognizer;
    private final Listener listener;
    private final StringBuilder committed = new StringBuilder();
    private final ArrayBlockingQueue<byte[]> queue = new ArrayBlockingQueue<>(48);
    private final AtomicBoolean running = new AtomicBoolean(true);
    private final Thread worker;

    public ResponseTranscriber(Model model, Listener listener) throws Exception {
        this.recognizer = new Recognizer(model, 16000.0f);
        this.listener = listener;
        worker = new Thread(this::loop, "ResponseTranscriber");
        worker.setPriority(Math.max(Thread.MIN_PRIORITY, Thread.NORM_PRIORITY - 1));
        worker.start();
    }

    public void accept(byte[] pcm) {
        if (!running.get() || pcm == null || pcm.length == 0) return;
        byte[] copy = Arrays.copyOf(pcm, pcm.length);
        if (!queue.offer(copy)) {
            queue.poll();
            queue.offer(copy);
        }
    }

    private void loop() {
        while (running.get() || !queue.isEmpty()) {
            try {
                byte[] pcm = queue.poll(150, TimeUnit.MILLISECONDS);
                if (pcm == null) continue;
                boolean done = recognizer.acceptWaveForm(pcm, pcm.length);
                JSONObject o = new JSONObject(done ? recognizer.getResult() : recognizer.getPartialResult());
                if (done) {
                    String text = o.optString("text", "").trim();
                    if (!text.isEmpty()) {
                        if (committed.length() > 0) committed.append(' ');
                        committed.append(text);
                    }
                    emit(committed.toString());
                } else {
                    String partial = o.optString("partial", "").trim();
                    if (!partial.isEmpty()) {
                        String all = committed.length() == 0 ? partial : committed + " " + partial;
                        emit(all);
                    }
                }
            } catch (InterruptedException ignored) {
                if (!running.get()) break;
            } catch (Exception ignored) { }
        }
    }

    private void emit(String text) {
        if (listener == null || text == null || text.trim().isEmpty()) return;
        String compact = text.trim();
        if (compact.length() > 500) compact = compact.substring(compact.length() - 500);
        listener.onText(compact);
    }

    @Override public void close() {
        if (!running.getAndSet(false)) return;
        worker.interrupt();
        try { worker.join(300); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
        queue.clear();
        try { recognizer.close(); } catch (Exception ignored) { }
    }
}
