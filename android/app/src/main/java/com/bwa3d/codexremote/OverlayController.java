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
            showFallbackActivity(state);
            return;
        }
        if (!canShow()) {
            AndroidDebugLog.log("Overlay permission false; using fallback activity");
            activityFallback = true;
            showFallbackActivity(state);
            return;
        }
        if (view == null && !tryAddOverlay(state)) {
            activityFallback = true;
            showFallbackActivity(state);
            return;
        }
        if (view != null) view.setState(state);
    }

    private boolean tryAddOverlay(String state) {
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
            candidate.setState(state);
            candidate.setOnClickListener(v -> { if (tapAction != null) tapAction.run(); });
            int flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL;
            WindowManager.LayoutParams candidateParams = new WindowManager.LayoutParams(
                    dp(220), dp(80), type, flags,
                    android.graphics.PixelFormat.TRANSLUCENT);
            candidateParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            candidateParams.y = dp(42);
            try {
                windowManager.addView(candidate, candidateParams);
                view = candidate;
                layoutParams = candidateParams;
                AndroidDebugLog.log("Overlay added; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; main=true");
                return true;
            } catch (SecurityException | WindowManager.BadTokenException e) {
                AndroidDebugLog.log("Overlay add denied; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
            } catch (RuntimeException e) {
                AndroidDebugLog.log("Overlay add failed; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
            }
        }
        view = null;
        layoutParams = null;
        AndroidDebugLog.log("WindowManager overlay unavailable · switching to fallback activity");
        return false;
    }

    private void showFallbackActivity(String state) {
        try {
            Intent i = new Intent(context, OverlayFallbackActivity.class);
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT | Intent.FLAG_ACTIVITY_NO_ANIMATION);
            i.putExtra(OverlayFallbackActivity.EXTRA_STATE, state);
            context.startActivity(i);
            AndroidDebugLog.log("Fallback overlay activity requested · state=" + state);
        } catch (Exception e) {
            AndroidDebugLog.log("Fallback overlay activity failed: " + e);
        }
    }

    public void setLevel(float level) {
        if (!onMainThread()) {
            mainHandler.post(() -> setLevel(level));
            return;
        }
        if (view != null) view.setLevel(level);
    }

    public void setTranscript(String text) {
        if (!onMainThread()) {
            mainHandler.post(() -> setTranscript(text));
            return;
        }
        if (view == null) return;
        view.setTranscript(text == null ? "" : text);
        if (layoutParams != null) {
            int wanted = (text == null || text.trim().isEmpty()) ? dp(80) : dp(150);
            if (layoutParams.height != wanted) {
                layoutParams.height = wanted;
                try { windowManager.updateViewLayout(view, layoutParams); } catch (Exception ignored) { }
            }
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
        if (activityFallback) {
            try {
                Intent i = new Intent(context, OverlayFallbackActivity.class);
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT | Intent.FLAG_ACTIVITY_NO_ANIMATION);
                i.putExtra(OverlayFallbackActivity.EXTRA_STATE, OverlayFallbackActivity.STATE_HIDE);
                context.startActivity(i);
            } catch (Exception ignored) { }
        }
    }

    private int dp(int value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }

    private static class OverlayView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private String state = "Escuchando";
        private String transcript = "";
        private float level = 0.15f;
        OverlayView(Context context) { super(context); setClickable(true); }
        void setState(String value) { state = value; postInvalidate(); }
        void setTranscript(String value) { transcript = value == null ? "" : value.trim(); postInvalidate(); }
        void setLevel(float value) { level = Math.max(0.05f, Math.min(1f, value)); postInvalidate(); }
        @Override protected void onDraw(Canvas c) {
            super.onDraw(c);
            float d = getResources().getDisplayMetrics().density;
            paint.setColor(0xE6212121);
            c.drawRoundRect(new RectF(0, 0, getWidth(), getHeight()), 24*d, 24*d, paint);
            paint.setColor(Color.WHITE); paint.setTextAlign(Paint.Align.CENTER); paint.setTextSize(14*d);
            c.drawText(state + " · tocar para finalizar", getWidth()/2f, 24*d, paint);
            if (!transcript.isEmpty()) {
                paint.setTextSize(13*d); paint.setTextAlign(Paint.Align.LEFT);
                drawWrappedText(c, transcript, 14*d, 48*d, getWidth()-28*d, 18*d, 4);
            }
            float base = getHeight() - 14*d, center = getWidth()/2f, barW = 5*d, gap = 6*d;
            for (int i=0;i<5;i++) {
                float factor = 0.35f + ((i==2)?0.65f:(i==1||i==3?0.45f:0.25f));
                float h=(8+25*level*factor)*d, x=center+(i-2)*(barW+gap);
                c.drawRoundRect(new RectF(x,base-h,x+barW,base),3*d,3*d,paint);
            }
        }
        private void drawWrappedText(Canvas c,String text,float x,float y,float maxWidth,float lineHeight,int maxLines) {
            String[] words=text.split("\\s+"); StringBuilder line=new StringBuilder(); int lines=0;
            for(String word:words){ String candidate=line.length()==0?word:line+" "+word;
                if(paint.measureText(candidate)>maxWidth&&line.length()>0){c.drawText(line.toString(),x,y+lines*lineHeight,paint);lines++;if(lines>=maxLines)return;line.setLength(0);line.append(word);}
                else {if(line.length()>0)line.append(' ');line.append(word);} }
            if(line.length()>0&&lines<maxLines)c.drawText(line.toString(),x,y+lines*lineHeight,paint);
        }
    }
}
