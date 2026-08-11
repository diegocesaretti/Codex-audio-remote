package com.bwa3d.codexremote;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Build;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;

public class OverlayController {
    private final Context context;
    private final WindowManager windowManager;
    private OverlayView view;

    public OverlayController(Context context) {
        this.context = context.getApplicationContext();
        this.windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
    }

    public boolean canShow() {
        return Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(context);
    }

    public void show(String state) {
        if (!canShow()) return;
        if (view == null) {
            view = new OverlayView(context);
            int type = Build.VERSION.SDK_INT >= 26
                    ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                    : WindowManager.LayoutParams.TYPE_PHONE;
            WindowManager.LayoutParams lp = new WindowManager.LayoutParams(
                    dp(190), dp(74), type,
                    WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                            | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                            | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                    android.graphics.PixelFormat.TRANSLUCENT);
            lp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            lp.y = dp(42);
            windowManager.addView(view, lp);
        }
        view.setState(state);
    }

    public void setLevel(float level) {
        if (view != null) view.setLevel(level);
    }

    public void hide() {
        if (view == null) return;
        try { windowManager.removeView(view); } catch (Exception ignored) { }
        view = null;
    }

    private int dp(int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    private static class OverlayView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private String state = "Escuchando";
        private float level = 0.15f;

        OverlayView(Context context) { super(context); }

        void setState(String value) {
            state = value;
            postInvalidate();
        }

        void setLevel(float value) {
            level = Math.max(0.05f, Math.min(1f, value));
            postInvalidate();
        }

        @Override protected void onDraw(Canvas c) {
            super.onDraw(c);
            float d = getResources().getDisplayMetrics().density;
            paint.setColor(0xD9212121);
            c.drawRoundRect(new RectF(0, 0, getWidth(), getHeight()), 24*d, 24*d, paint);

            paint.setColor(Color.WHITE);
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(14*d);
            c.drawText(state, getWidth()/2f, 24*d, paint);

            float base = 58*d;
            float center = getWidth()/2f;
            float barW = 5*d;
            float gap = 6*d;
            for (int i = 0; i < 5; i++) {
                float factor = 0.35f + ((i == 2) ? 0.65f : (i == 1 || i == 3 ? 0.45f : 0.25f));
                float h = (8 + 25 * level * factor) * d;
                float x = center + (i - 2) * (barW + gap);
                c.drawRoundRect(new RectF(x, base-h, x+barW, base), 3*d, 3*d, paint);
            }
        }
    }
}
