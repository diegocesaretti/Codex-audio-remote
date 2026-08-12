package com.bwa3d.codexremote;

import android.content.Context;
import android.content.Intent;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;

public class OverlayController {
    private final Context context;
    private final WindowManager windowManager;
    private final Runnable tapAction;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private OverlayView view;
    private WindowManager.LayoutParams layoutParams;
    private boolean activityFallback;
    private boolean fallbackVisible;

    public OverlayController(Context context, Runnable tapAction) {
        this.context = context.getApplicationContext();
        this.windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        this.tapAction = tapAction;
    }

    public boolean canShow() {
        return Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(context);
    }

    private boolean onMainThread() {
        return Looper.myLooper() == Looper.getMainLooper();
    }

    public void show(String state) {
        if (!onMainThread()) {
            mainHandler.post(() -> show(state));
            return;
        }
        if (activityFallback) {
            showFallbackActivityOnce();
            return;
        }
        if (!canShow()) {
            AndroidDebugLog.log("Overlay permission false; using fixed fallback activity");
            activityFallback = true;
            showFallbackActivityOnce();
            return;
        }
        if (view == null && !tryAddOverlay()) {
            activityFallback = true;
            showFallbackActivityOnce();
            return;
        }
    }

    private boolean tryAddOverlay() {
        int[] types;
        if (Build.VERSION.SDK_INT >= 26) {
            types = new int[]{ WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY };
        } else if (Build.VERSION.SDK_INT >= 23) {
            types = new int[]{ WindowManager.LayoutParams.TYPE_SYSTEM_ALERT, WindowManager.LayoutParams.TYPE_PHONE };
        } else {
            types = new int[]{ WindowManager.LayoutParams.TYPE_PHONE };
        }

        for (int type : types) {
            OverlayView candidate = new OverlayView(context);
            candidate.setOnClickListener(v -> { if (tapAction != null) tapAction.run(); });
            int flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL;
            WindowManager.LayoutParams candidateParams = new WindowManager.LayoutParams(
                    dp(300), dp(110), type, flags,
                    android.graphics.PixelFormat.TRANSLUCENT);
            candidateParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            candidateParams.y = dp(32);
            try {
                windowManager.addView(candidate, candidateParams);
                view = candidate;
                layoutParams = candidateParams;
                AndroidDebugLog.log("Fixed overlay added; type=" + type + "; API=" + Build.VERSION.SDK_INT);
                return true;
            } catch (SecurityException | WindowManager.BadTokenException e) {
                AndroidDebugLog.log("Overlay add denied; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
            } catch (RuntimeException e) {
                AndroidDebugLog.log("Overlay add failed; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
            }
        }
        view = null;
        layoutParams = null;
        AndroidDebugLog.log("WindowManager overlay unavailable · switching to fixed fallback activity");
        return false;
    }

    private void showFallbackActivityOnce() {
        if (fallbackVisible) return;
        try {
            Intent i = new Intent(context, OverlayFallbackActivity.class);
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                    | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
                    | Intent.FLAG_ACTIVITY_SINGLE_TOP
                    | Intent.FLAG_ACTIVITY_NO_ANIMATION);
            context.startActivity(i);
            fallbackVisible = true;
            AndroidDebugLog.log("Fixed fallback overlay shown once");
        } catch (Exception e) {
            AndroidDebugLog.log("Fallback overlay activity failed: " + e);
        }
    }

    public void setLevel(float level) {
        // Deliberately ignored in fixed mode. Avoid UI work on audio callbacks.
    }

    public void setTranscript(String text) {
        // Fixed overlay on legacy devices: no text/state updates while audio is running.
    }

    public void clearTranscript() { }

    public void hide() {
        if (!onMainThread()) {
            mainHandler.post(this::hide);
            return;
        }
        if (view != null) {
            try { windowManager.removeView(view); } catch (Exception ignored) { }
            view = null;
            layoutParams = null;
        }
        if (activityFallback && fallbackVisible) {
            try {
                Intent i = new Intent(context, OverlayFallbackActivity.class);
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                        | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
                        | Intent.FLAG_ACTIVITY_SINGLE_TOP
                        | Intent.FLAG_ACTIVITY_NO_ANIMATION);
                i.putExtra(OverlayFallbackActivity.EXTRA_STATE, OverlayFallbackActivity.STATE_HIDE);
                context.startActivity(i);
            } catch (Exception ignored) { }
        }
        fallbackVisible = false;
    }

    private int dp(int value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }

    private static class OverlayView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        OverlayView(Context context) { super(context); setClickable(true); }
        @Override protected void onDraw(Canvas c) {
            super.onDraw(c);
            float d = getResources().getDisplayMetrics().density;
            paint.setColor(0xE6212121);
            c.drawRoundRect(new RectF(0, 0, getWidth(), getHeight()), 28*d, 28*d, paint);
            paint.setColor(Color.WHITE);
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(18*d);
            c.drawText("Conversación activa", getWidth()/2f, 44*d, paint);
            paint.setTextSize(14*d);
            c.drawText("Tocar para finalizar", getWidth()/2f, 72*d, paint);
        }
    }
}
