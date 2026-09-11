package com.bwa3d.codextv;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.graphics.Color;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

public class MainActivity extends Activity {
    private static final int REQ_CAPTURE = 4102;
    private TextView status;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        LocalHttpServer.ensureStarted(getApplicationContext());
        buildUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refreshStatus();
    }

    private void buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(48, 36, 48, 36);
        root.setGravity(Gravity.CENTER_VERTICAL);
        root.setBackgroundColor(Color.rgb(16, 16, 16));

        TextView title = new TextView(this);
        title.setText("Codex TV Satellite");
        title.setTextColor(Color.WHITE);
        title.setTextSize(28f);
        root.addView(title);

        TextView subtitle = new TextView(this);
        subtitle.setText("Android 8+ · visión + accesibilidad + control local");
        subtitle.setTextColor(Color.LTGRAY);
        subtitle.setTextSize(16f);
        subtitle.setPadding(0, 10, 0, 24);
        root.addView(subtitle);

        status = new TextView(this);
        status.setTextColor(Color.WHITE);
        status.setTextSize(15f);
        status.setPadding(0, 0, 0, 22);
        root.addView(status);

        Button accessibility = button("1. Habilitar control de accesibilidad");
        accessibility.setOnClickListener(v -> startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        root.addView(accessibility);

        Button capture = button("2. Habilitar captura de pantalla");
        capture.setOnClickListener(v -> requestCapture());
        root.addView(capture);

        Button refresh = button("Actualizar estado");
        refresh.setOnClickListener(v -> refreshStatus());
        root.addView(refresh);

        setContentView(root);
        refreshStatus();
    }

    private Button button(String text) {
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setFocusable(true);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 8, 0, 8);
        b.setLayoutParams(lp);
        return b;
    }

    private void requestCapture() {
        MediaProjectionManager manager = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        if (manager != null) {
            startActivityForResult(manager.createScreenCaptureIntent(), REQ_CAPTURE);
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_CAPTURE && resultCode == RESULT_OK && data != null) {
            Intent svc = new Intent(this, CaptureService.class);
            svc.putExtra(CaptureService.EXTRA_RESULT_CODE, resultCode);
            svc.putExtra(CaptureService.EXTRA_RESULT_DATA, data);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(svc);
            } else {
                startService(svc);
            }
            refreshStatus();
        }
    }

    private void refreshStatus() {
        if (status == null) return;
        String ip = LocalHttpServer.getLocalIpAddress();
        StringBuilder sb = new StringBuilder();
        sb.append("API: http://").append(ip == null ? "TV_IP" : ip).append(":8765\n\n");
        sb.append("Accesibilidad: ").append(TvAccessibilityService.isConnected() ? "ACTIVA" : "DESACTIVADA").append("\n");
        sb.append("Captura: ").append(CaptureService.hasFrame() ? "ACTIVA" : "SIN IMAGEN").append("\n");
        sb.append("Android API: ").append(Build.VERSION.SDK_INT).append("\n\n");
        sb.append("Autenticación API: ninguna · red local");
        status.setText(sb.toString());
    }
}
