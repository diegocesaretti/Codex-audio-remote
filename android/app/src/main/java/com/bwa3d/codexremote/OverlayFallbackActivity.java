package com.bwa3d.codexremote;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Window;
import android.view.WindowManager;
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
    private TextView label;
    private boolean ending;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        Window w = getWindow();
        w.setBackgroundDrawableResource(android.R.color.transparent);
        w.setDimAmount(0f);
        w.addFlags(WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL);
        WindowManager.LayoutParams lp = w.getAttributes();
        lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        lp.width = Math.round(220 * getResources().getDisplayMetrics().density);
        lp.height = Math.round(80 * getResources().getDisplayMetrics().density);
        lp.y = Math.round(42 * getResources().getDisplayMetrics().density);
        w.setAttributes(lp);

        label = new TextView(this);
        label.setGravity(Gravity.CENTER);
        label.setTextColor(Color.WHITE);
        label.setTextSize(14f);
        label.setPadding(18, 8, 18, 8);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xE6212121);
        bg.setCornerRadius(24 * getResources().getDisplayMetrics().density);
        label.setBackground(bg);
        setContentView(label);
        updateState(getIntent());

        label.setOnClickListener(view -> endSessionDirectly());
        AndroidDebugLog.log("Fallback overlay activity shown");
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        updateState(intent);
    }

    private void updateState(Intent intent) {
        String state = intent != null ? intent.getStringExtra(EXTRA_STATE) : null;
        if (STATE_HIDE.equals(state)) {
            finish();
            return;
        }
        if (state == null || state.trim().isEmpty()) state = "Escuchando";
        if (label != null && !ending) label.setText(state + " · tocar para finalizar");
    }

    private void endSessionDirectly() {
        if (ending) return;
        ending = true;
        if (label != null) label.setText("Finalizando…");
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
                // Keep the existing RemoteService alive; do not kill/restart it.
                ending = false;
                runOnUiThread(() -> {
                    if (label != null) label.setText("No se pudo finalizar · tocar otra vez");
                });
                client.dispatcher().executorService().shutdown();
            }
        });
    }
}
