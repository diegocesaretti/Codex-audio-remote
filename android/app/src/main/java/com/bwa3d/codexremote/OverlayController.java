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

        float scale = OverlayPrefs.scale(context);
        int width = dp(Math.round(300 * scale));
        int height = dp(Math.round((context.getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("show_transcript", false) ? 180 : 110) * scale));

        for (int type : types) {
            OverlayView candidate = new OverlayView(context);
            candidate.setOnClickListener(v -> { if (tapAction != null) tapAction.run(); });
            int flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL;
            WindowManager.LayoutParams candidateParams = new WindowManager.LayoutParams(
                    width, height, type, flags, android.graphics.PixelFormat.TRANSLUCENT);
            candidateParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            candidateParams.y = dp(Math.round(32 * scale));
            try {
                windowManager.addView(candidate, candidateParams);
                view = candidate;
                layoutParams = candidateParams;
                AndroidDebugLog.log("Fixed overlay added; type=" + type + "; API=" + Build.VERSION.SDK_INT + " scale=" + scale);
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
        // Deliberately ignored. Avoid UI work on every audio chunk.
    }

    public void setTranscript(String text) {
        if (!context.getSharedPreferences("settings", Context.MODE_PRIVATE).getBoolean("show_transcript", false)) return;
        final String safe = text == null ? "" : text.trim();
        if (!onMainThread()) {
            mainHandler.post(() -> setTranscript(safe));
            return;
        }
        if (view != null) view.setTranscript(safe);
        if (activityFallback && fallbackVisible) {
            try {
                Intent i = new Intent(OverlayFallbackActivity.ACTION_TRANSCRIPT);
                i.setPackage(context.getPackageName());
                i.putExtra(OverlayFallbackActivity.EXTRA_TRANSCRIPT, safe);
                context.sendBroadcast(i);
            } catch (Exception ignored) { }
        }
    }

    public void clearTranscript() { setTranscript(""); }

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

    /** Android 6-safe lifecycle cleanup used by RemoteService.onDestroy(). */
    public void destroy() {
        if (!onMainThread()) {
            mainHandler.post(this::destroy);
            return;
        }
        hide();
        mainHandler.removeCallbacksAndMessages(null);
    }

    private int dp(int value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }

    private static class OverlayView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private String transcript = "";
        OverlayView(Context context) { super(context); setClickable(true); }
        void setTranscript(String value) { transcript = value == null ? "" : value.trim(); postInvalidate(); }
        @Override protected void onDraw(Canvas c) {
            super.onDraw(c);
            float d = getResources().getDisplayMetrics().density;
            float scale = OverlayPrefs.scale(getContext());
            paint.setColor(OverlayPrefs.color(getContext()));
            c.drawRoundRect(new RectF(0, 0, getWidth(), getHeight()), 28*d*scale, 28*d*scale, paint);
            paint.setColor(OverlayPrefs.textColor(getContext()));
            paint.setTextAlign(Paint.Align.CENTER);
            paint.setTextSize(18*d*scale);
            c.drawText("Conversación activa", getWidth()/2f, 42*d*scale, paint);
            paint.setTextSize(14*d*scale);
            c.drawText("Tocar para finalizar", getWidth()/2f, 70*d*scale, paint);

            if (!transcript.isEmpty()) {
                paint.setTextAlign(Paint.Align.LEFT);
                paint.setTextSize(12*d*scale);
                drawWrapped(c, transcript, 18*d*scale, 96*d*scale, getWidth() - 36*d*scale, 17*d*scale, 4);
            }
        }
        private void drawWrapped(Canvas c, String text, float x, float y, float maxWidth, float lineHeight, int maxLines) {
            String[] words = text.split("\\s+");
            StringBuilder line = new StringBuilder();
            int lines = 0;
            for (String word : words) {
                String candidate = line.length() == 0 ? word : line + " " + word;
                if (paint.measureText(candidate) > maxWidth && line.length() > 0) {
                    c.drawText(line.toString(), x, y + lines * lineHeight, paint);
                    if (++lines >= maxLines) return;
                    line.setLength(0); line.append(word);
                } else {
                    if (line.length() > 0) line.append(' ');
                    line.append(word);
                }
            }
            if (line.length() > 0 && lines < maxLines) c.drawText(line.toString(), x, y + lines * lineHeight, paint);
        }
    }
}
