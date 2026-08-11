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

public class OverlayFallbackActivity extends Activity {
    public static final String EXTRA_STATE = "state";

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

        TextView v = new TextView(this);
        v.setGravity(Gravity.CENTER);
        v.setTextColor(Color.WHITE);
        v.setTextSize(14f);
        v.setPadding(18, 8, 18, 8);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0xE6212121);
        bg.setCornerRadius(24 * getResources().getDisplayMetrics().density);
        v.setBackground(bg);
        setContentView(v);
        updateState(getIntent());

        v.setOnClickListener(view -> {
            AndroidDebugLog.log("Fallback overlay tapped · restarting service to end session");
            Intent service = new Intent(this, RemoteService.class);
            stopService(service);
            SharedPreferences p = getSharedPreferences("settings", MODE_PRIVATE);
            Intent restart = new Intent(this, RemoteService.class);
            restart.setAction(RemoteService.ACTION_START);
            restart.putExtra("ip", p.getString("ip", "192.168.1.100"));
            restart.putExtra("port", p.getInt("port", 8765));
            startService(restart);
            finish();
        });
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        updateState(intent);
    }

    private void updateState(Intent intent) {
        String state = intent != null ? intent.getStringExtra(EXTRA_STATE) : null;
        if (state == null || state.trim().isEmpty()) state = "Escuchando";
        TextView v = (TextView) findViewById(android.R.id.content).getRootView().findViewById(android.R.id.content);
        if (getWindow().getDecorView().findViewById(android.R.id.content) instanceof android.view.ViewGroup) {
            android.view.ViewGroup root = (android.view.ViewGroup)getWindow().getDecorView().findViewById(android.R.id.content);
            if (root.getChildCount() > 0 && root.getChildAt(0) instanceof TextView) {
                ((TextView)root.getChildAt(0)).setText(state + " · tocar para finalizar");
            }
        }
    }
}
