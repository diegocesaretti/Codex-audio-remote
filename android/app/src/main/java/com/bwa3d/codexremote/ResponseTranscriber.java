package com.bwa3d.codexremote;

import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;

public class ResponseTranscriber implements AutoCloseable {
    public interface Listener { void onText(String text); }

    private final Recognizer recognizer;
    private final Listener listener;
    private final StringBuilder committed = new StringBuilder();

    public ResponseTranscriber(Model model, Listener listener) throws Exception {
        this.recognizer = new Recognizer(model, 16000.0f);
        this.listener = listener;
    }

    public synchronized void accept(byte[] pcm) {
        if (pcm == null || pcm.length == 0) return;
        try {
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
        } catch (Exception ignored) { }
    }

    private void emit(String text) {
        if (listener == null || text == null || text.trim().isEmpty()) return;
        String compact = text.trim();
        if (compact.length() > 500) compact = compact.substring(compact.length() - 500);
        listener.onText(compact);
    }

    @Override public synchronized void close() {
        try { recognizer.close(); } catch (Exception ignored) { }
    }
}
