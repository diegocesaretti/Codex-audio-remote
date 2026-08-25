package com.bwa3d.codexremote;

import android.content.Context;
import android.graphics.Typeface;
import android.util.AttributeSet;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/** Live partial/final Vosk hypothesis monitor for wake-word tuning. */
public class VoskLiveDetectionsView extends LinearLayout implements VoskDetectionBus.Listener {
    private final StringBuilder buffer = new StringBuilder();
    private TextView logView;
    private TextView latestPartial;
    private TextView latestFinal;
    private ScrollView scroller;
    private boolean attached;

    public VoskLiveDetectionsView(Context context) { super(context); init(); }
    public VoskLiveDetectionsView(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public VoskLiveDetectionsView(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        setOrientation(VERTICAL);
        setPadding(0, dp(8), 0, dp(8));

        TextView title = new TextView(getContext());
        title.setText("Vosk · detecciones en vivo");
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        addView(title, fullWrap());

        TextView help = new TextView(getContext());
        help.setText("Muestra exactamente qué hipótesis llega al gate. PARTIAL = hipótesis provisional; FINAL = resultado cerrado por Vosk.");
        addView(help, fullWrap());

        latestPartial = new TextView(getContext());
        latestPartial.setText("Último PARTIAL: —");
        latestPartial.setTypeface(Typeface.MONOSPACE);
        addView(latestPartial, fullWrap());

        latestFinal = new TextView(getContext());
        latestFinal.setText("Último FINAL: —");
        latestFinal.setTypeface(Typeface.MONOSPACE);
        addView(latestFinal, fullWrap());

        scroller = new ScrollView(getContext());
        logView = new TextView(getContext());
        logView.setTypeface(Typeface.MONOSPACE);
        logView.setTextSize(12f);
        logView.setPadding(dp(8), dp(8), dp(8), dp(8));
        logView.setText("Esperando hipótesis Vosk…");
        scroller.addView(logView, new ScrollView.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));
        LayoutParams scrollParams = new LayoutParams(LayoutParams.MATCH_PARENT, dp(230));
        scrollParams.topMargin = dp(5);
        addView(scroller, scrollParams);

        Button clear = new Button(getContext());
        clear.setText("Limpiar detecciones");
        clear.setGravity(Gravity.CENTER);
        clear.setOnClickListener(v -> {
            VoskDetectionBus.clear();
            buffer.setLength(0);
            latestPartial.setText("Último PARTIAL: —");
            latestFinal.setText("Último FINAL: —");
            logView.setText("Esperando hipótesis Vosk…");
        });
        addView(clear, fullWrap());
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (attached) return;
        attached = true;
        VoskDetectionBus.addListener(this);
        renderHistory(VoskDetectionBus.snapshot());
    }

    @Override protected void onDetachedFromWindow() {
        if (attached) {
            VoskDetectionBus.removeListener(this);
            attached = false;
        }
        super.onDetachedFromWindow();
    }

    @Override public void onVoskDetection(VoskDetectionBus.Event event) {
        if (event == null) return;
        post(() -> append(event));
    }

    private void renderHistory(List<VoskDetectionBus.Event> history) {
        buffer.setLength(0);
        if (history == null || history.isEmpty()) {
            logView.setText("Esperando hipótesis Vosk…");
            return;
        }
        for (VoskDetectionBus.Event event : history) appendInternal(event, false);
        logView.setText(buffer.toString());
        scrollBottom();
    }

    private void append(VoskDetectionBus.Event event) {
        appendInternal(event, true);
        logView.setText(buffer.toString());
        scrollBottom();
    }

    private void appendInternal(VoskDetectionBus.Event e, boolean trim) {
        String type = e.isFinal ? "FINAL  " : "PARTIAL";
        String text = e.text.isEmpty() ? "<vacío>" : e.text;
        String result = e.accepted ? "ACCEPT" : "reject";
        String conf = e.confidence < 0.0 ? "--" : String.format(Locale.US, "%.2f", e.confidence);
        String dur = e.durationMs < 0 ? "--" : e.durationMs + "ms";
        String line = time(e.timestampMs) + " " + type + "  \"" + text + "\"\n"
                + "  " + result + " · conf " + conf + " · dur " + dur
                + " · rms " + e.rms + " · floor " + e.noiseFloor + "\n"
                + "  gate: " + e.reason + "\n\n";

        buffer.append(line);
        if (e.isFinal) latestFinal.setText("Último FINAL: " + text + " · " + result + " · conf " + conf);
        else latestPartial.setText("Último PARTIAL: " + text + " · " + result);

        if (trim && buffer.length() > 14000) {
            int cut = buffer.indexOf("\n\n", 3500);
            if (cut > 0) buffer.delete(0, cut + 2);
        }
    }

    private void scrollBottom() {
        scroller.post(() -> scroller.fullScroll(View.FOCUS_DOWN));
    }

    private static String time(long ms) {
        return new SimpleDateFormat("HH:mm:ss.SSS", Locale.US).format(new Date(ms));
    }

    private LayoutParams fullWrap() { return new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
