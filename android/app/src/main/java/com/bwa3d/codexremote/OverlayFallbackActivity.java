package com.bwa3d.codexremote;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Window;
import android.view.WindowManager;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.concurrent.TimeUnit;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

public class OverlayFallbackActivity extends Activity {
    public static final String EXTRA_STATE = "state";
    public static final String STATE_HIDE = "__hide__";
    public static final String ACTION_TRANSCRIPT = "com.bwa3d.codexremote.OVERLAY_TRANSCRIPT";
    public static final String EXTRA_TRANSCRIPT = "transcript";
    private TextView title;
    private TextView subtitle;
    private TextView transcript;
    private boolean ending;
    private boolean receiverRegistered;

    private final BroadcastReceiver transcriptReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (intent == null || !ACTION_TRANSCRIPT.equals(intent.getAction()) || transcript == null) return;
            String value = intent.getStringExtra(EXTRA_TRANSCRIPT);
            transcript.setText(value == null ? "" : value.trim());
        }
    };

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        SharedPreferences prefs = getSharedPreferences("settings", MODE_PRIVATE);
        float scale = OverlayPrefs.scale(this);
        boolean showTranscript = prefs.getBoolean("show_transcript", false);

        Window w = getWindow();
        w.setBackgroundDrawableResource(android.R.color.transparent);
        w.setDimAmount(0f);
        w.addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL);
        WindowManager.LayoutParams lp = w.getAttributes();
        lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        lp.width = Math.round(300 * scale * getResources().getDisplayMetrics().density);
        lp.height = Math.round((showTranscript ? 180 : 110) * scale * getResources().getDisplayMetrics().density);
        lp.y = Math.round(32 * scale * getResources().getDisplayMetrics().density);
        w.setAttributes(lp);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        int pad = Math.round(14 * scale * getResources().getDisplayMetrics().density);
        root.setPadding(pad, pad, pad, pad);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(OverlayPrefs.color(this));
        bg.setCornerRadius(28 * scale * getResources().getDisplayMetrics().density);
        root.setBackground(bg);

        title = new TextView(this);
        title.setGravity(Gravity.CENTER);
        title.setTextColor(OverlayPrefs.textColor(this));
        title.setTextSize(20f * scale);
        title.setText("Conversación activa");

        subtitle = new TextView(this);
        subtitle.setGravity(Gravity.CENTER);
        subtitle.setTextColor(0xFFD0D0D0);
        subtitle.setTextSize(15f * scale);
        subtitle.setText("Tocar para finalizar");

        root.addView(title, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        root.addView(subtitle, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        if (showTranscript) {
            transcript = new TextView(this);
            transcript.setGravity(Gravity.CENTER);
            transcript.setTextColor(Color.WHITE);
            transcript.setTextSize(13f * scale);
            transcript.setMaxLines(4);
            LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f);
            tp.topMargin = Math.round(8 * scale * getResources().getDisplayMetrics().density);
            root.addView(transcript, tp);
            registerTranscriptReceiver();
        }

        setContentView(root);

        if (getIntent() != null && STATE_HIDE.equals(getIntent().getStringExtra(EXTRA_STATE))) {
            finish();
            return;
        }

        root.setOnClickListener(view -> endSessionDirectly());
        AndroidDebugLog.log("Fixed fallback overlay activity shown · scale=" + scale + " · transcript=" + showTranscript);
    }

    private void registerTranscriptReceiver() {
        try {
            IntentFilter filter = new IntentFilter(ACTION_TRANSCRIPT);
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(transcriptReceiver, filter, RECEIVER_NOT_EXPORTED);
            else registerReceiver(transcriptReceiver, filter);
            receiverRegistered = true;
        } catch (Exception e) {
            AndroidDebugLog.log("Overlay transcript receiver error: " + e);
        }
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (intent != null && STATE_HIDE.equals(intent.getStringExtra(EXTRA_STATE))) finish();
    }

    @Override protected void onDestroy() {
        if (receiverRegistered) {
            try { unregisterReceiver(transcriptReceiver); } catch (Exception ignored) { }
            receiverRegistered = false;
        }
        super.onDestroy();
    }

    private void endSessionDirectly() {
        if (ending) return;
        ending = true;
        AndroidDebugLog.log("Fallback overlay tapped · direct end_session requested");

        SharedPreferences p = getSharedPreferences("settings", MODE_PRIVATE);
        String ip = p.getString("ip", "192.168.1.100");
        int port = p.getInt("port", 8765);
        OkHttpClient client = new OkHttpClient.Builder()
                .connectTimeout(2, TimeUnit.SECONDS)
                .readTimeout(2, TimeUnit.SECONDS)
                .writeTimeout(2, TimeUnit.SECONDS)
                .build();
        Request request = new Request.Builder().url("ws://" + ip + ":" + port + "/ws/").build();
        client.newWebSocket(request, new WebSocketListener() {
            @Override public void onOpen(WebSocket webSocket, Response response) {
                AndroidDebugLog.log("Fallback end socket open");
                webSocket.send("{\"type\":\"end_session\",\"reason\":\"overlay_tap\"}");
                webSocket.close(1000, "overlay end sent");
                runOnUiThread(() -> finish());
                client.dispatcher().executorService().shutdown();
            }

            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                AndroidDebugLog.log("Fallback direct end failed: " + t);
                ending = false;
                client.dispatcher().executorService().shutdown();
            }
        });
    }
}
