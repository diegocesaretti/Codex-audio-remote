package com.diegocesaretti.solwake;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

public class DebugActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView header;
    private TextView log;
    private ScrollView logScroll;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(14), dp(14), dp(14), dp(14));

        TextView title = new TextView(this);
        title.setText("Debug Vosk");
        title.setTextSize(24);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(title);

        header = new TextView(this);
        header.setTextSize(14);
        header.setPadding(0, dp(8), 0, dp(8));
        root.addView(header);

        LinearLayout buttons = new LinearLayout(this);
        buttons.setOrientation(LinearLayout.HORIZONTAL);

        Button clear = new Button(this);
        clear.setText("Limpiar");
        clear.setOnClickListener(v -> {
            DebugLog.clear();
            DebugLog.add("LOG limpiado por usuario");
            refresh();
        });
        buttons.addView(clear, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));

        Button copy = new Button(this);
        copy.setText("Copiar");
        copy.setOnClickListener(v -> {
            ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm != null) {
                cm.setPrimaryClip(ClipData.newPlainText("Sol Wake debug", DebugLog.snapshot()));
                Toast.makeText(this, "Debug copiado", Toast.LENGTH_SHORT).show();
            }
        });
        buttons.addView(copy, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(buttons);

        logScroll = new ScrollView(this);
        log = new TextView(this);
        log.setTextSize(12);
        log.setTypeface(Typeface.MONOSPACE);
        log.setTextIsSelectable(true);
        log.setPadding(dp(8), dp(8), dp(8), dp(8));
        logScroll.addView(log);
        root.addView(logScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        setContentView(root);
    }

    @Override protected void onResume() {
        super.onResume();
        handler.post(ticker);
    }

    @Override protected void onPause() {
        handler.removeCallbacks(ticker);
        super.onPause();
    }

    private final Runnable ticker = new Runnable() {
        @Override public void run() {
            refresh();
            handler.postDelayed(this, 350);
        }
    };

    private void refresh() {
        header.setText("Servicio: " + (WakeService.isRunning() ? "ACTIVO" : "DETENIDO")
                + "\nEstado: " + WakeService.getStatus()
                + "\nDecí la frase varias veces y mirá AUDIO / PARTIAL / FINAL / MATCH.");
        String snapshot = DebugLog.snapshot();
        if (!snapshot.equals(log.getText().toString())) {
            log.setText(snapshot);
            logScroll.post(() -> logScroll.fullScroll(ScrollView.FOCUS_DOWN));
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
