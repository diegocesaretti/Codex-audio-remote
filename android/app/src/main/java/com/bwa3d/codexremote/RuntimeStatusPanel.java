package com.bwa3d.codexremote;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.os.Build;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.TextView;

public class RuntimeStatusPanel extends LinearLayout {
    private final TextView pc;
    private final TextView audio;
    private boolean registered;

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (intent == null || !RemoteService.ACTION_STATUS.equals(intent.getAction())) return;
            render(intent);
        }
    };

    public RuntimeStatusPanel(Context context) { this(context, null); }

    public RuntimeStatusPanel(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        int pad = Math.round(10 * getResources().getDisplayMetrics().density);
        setPadding(0, pad, 0, pad);

        pc = new TextView(context);
        pc.setTextSize(16f);
        pc.setText("● PC: SIN DATOS");
        addView(pc, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        audio = new TextView(context);
        audio.setTextSize(16f);
        audio.setText("● WAKE: SIN DATOS");
        LayoutParams lp = new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Math.round(3 * getResources().getDisplayMetrics().density);
        addView(audio, lp);
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (registered) return;
        IntentFilter filter = new IntentFilter(RemoteService.ACTION_STATUS);
        try {
            if (Build.VERSION.SDK_INT >= 33) getContext().registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
            else getContext().registerReceiver(receiver, filter);
            registered = true;
        } catch (Exception ignored) { }
    }

    @Override protected void onDetachedFromWindow() {
        if (registered) {
            try { getContext().unregisterReceiver(receiver); } catch (Exception ignored) { }
            registered = false;
        }
        super.onDetachedFromWindow();
    }

    private void render(Intent intent) {
        boolean connected = intent.getBooleanExtra(RemoteService.EXTRA_CONNECTED, false);
        boolean connecting = intent.getBooleanExtra(RemoteService.EXTRA_CONNECTING, false);
        String state = intent.getStringExtra(RemoteService.EXTRA_SERVER_STATE);
        if (state == null) state = "disconnected";
        long revision = intent.getLongExtra(RemoteService.EXTRA_REVISION, -1);
        String session = intent.getStringExtra(RemoteService.EXTRA_SESSION_ID);
        if (session == null) session = "";

        if (connected) {
            pc.setText("● PC: CONECTADO · " + state.toUpperCase() + " · rev " + revision);
            pc.setTextColor(Color.rgb(30, 150, 60));
        } else if (connecting) {
            pc.setText("● PC: CONECTANDO…");
            pc.setTextColor(Color.rgb(210, 145, 0));
        } else {
            pc.setText("● PC: DESCONECTADO");
            pc.setTextColor(Color.rgb(190, 45, 45));
        }

        boolean wakeRunning = intent.getBooleanExtra(RemoteService.EXTRA_WAKE_RUNNING, false);
        boolean wakeHealthy = intent.getBooleanExtra(RemoteService.EXTRA_WAKE_HEALTHY, false);
        int rms = intent.getIntExtra(RemoteService.EXTRA_WAKE_RMS, 0);
        long wakeAge = intent.getLongExtra(RemoteService.EXTRA_WAKE_AUDIO_AGE, -1);
        boolean micRunning = intent.getBooleanExtra(RemoteService.EXTRA_MIC_RUNNING, false);
        boolean micHealthy = intent.getBooleanExtra(RemoteService.EXTRA_MIC_HEALTHY, false);

        if (!connected) {
            audio.setText("● WAKE: PAUSADO · sin conexión");
            audio.setTextColor(Color.rgb(190, 45, 45));
        } else if ("idle".equals(state)) {
            if (wakeHealthy) {
                audio.setText("● WAKE: ESCUCHANDO · RMS " + rms + ageText(wakeAge));
                audio.setTextColor(Color.rgb(30, 150, 60));
            } else if (wakeRunning) {
                audio.setText("● WAKE: ABIERTO · esperando audio" + ageText(wakeAge));
                audio.setTextColor(Color.rgb(210, 145, 0));
            } else {
                audio.setText("● WAKE: REARMANDO…");
                audio.setTextColor(Color.rgb(210, 145, 0));
            }
        } else if ("activating".equals(state)) {
            audio.setText("● WAKE: PAUSADO · activando Codex" + sessionText(session));
            audio.setTextColor(Color.rgb(210, 145, 0));
        } else if ("listening".equals(state)) {
            if (micHealthy) {
                audio.setText("● MIC: ESCUCHANDO · uplink OK" + sessionText(session));
                audio.setTextColor(Color.rgb(30, 150, 60));
            } else if (micRunning) {
                audio.setText("● MIC: ABIERTO · esperando audio" + sessionText(session));
                audio.setTextColor(Color.rgb(210, 145, 0));
            } else {
                audio.setText("● MIC: REABRIENDO…" + sessionText(session));
                audio.setTextColor(Color.rgb(210, 145, 0));
            }
        } else {
            audio.setText("● AUDIO: FINALIZANDO…" + sessionText(session));
            audio.setTextColor(Color.rgb(210, 145, 0));
        }
    }

    private static String ageText(long age) {
        if (age < 0) return "";
        if (age < 1000) return " · audio ahora";
        return " · audio hace " + (age / 1000) + " s";
    }

    private static String sessionText(String session) {
        if (session == null || session.isEmpty()) return "";
        return " · s:" + session.substring(0, Math.min(6, session.length()));
    }
}
