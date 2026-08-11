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
    private final Runnable tapAction;
    private OverlayView view;
    private WindowManager.LayoutParams layoutParams;

    public OverlayController(Context context, Runnable tapAction) {
        this.context = context.getApplicationContext();
        this.windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        this.tapAction = tapAction;
    }

    public boolean canShow() {
        return Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(context);
    }

    public void show(String state) {
        if (!canShow()) {
            DebugLog.log(context, "Overlay show skipped: Settings.canDrawOverlays=false");
            return;
        }
        if (view == null) {
            OverlayView candidate = new OverlayView(context);
            candidate.setOnClickListener(v -> { if (tapAction != null) tapAction.run(); });
            int type;
            if (Build.VERSION.SDK_INT >= 26) type = WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY;
            else if (Build.VERSION.SDK_INT >= 23) type = WindowManager.LayoutParams.TYPE_SYSTEM_ALERT;
            else type = WindowManager.LayoutParams.TYPE_PHONE;
            WindowManager.LayoutParams candidateParams = new WindowManager.LayoutParams(
                    dp(220), dp(80), type,
                    WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                            | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                    android.graphics.PixelFormat.TRANSLUCENT);
            candidateParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
            candidateParams.y = dp(42);
            try {
                windowManager.addView(candidate, candidateParams);
                view = candidate;
                layoutParams = candidateParams;
                DebugLog.log(context, "Overlay added; type=" + type + "; API=" + Build.VERSION.SDK_INT);
            } catch (SecurityException | WindowManager.BadTokenException e) {
                DebugLog.log(context, "Overlay add denied; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
                view = null;
                layoutParams = null;
                return;
            } catch (RuntimeException e) {
                DebugLog.log(context, "Overlay add failed; type=" + type + "; API=" + Build.VERSION.SDK_INT + "; " + e);
                view = null;
                layoutParams = null;
                return;
            }
        }
        view.setState(state);
    }

    public void setLevel(float level) { if (view != null) view.setLevel(level); }

    public void setTranscript(String text) {
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
        if (view == null) return;
        try { windowManager.removeView(view); } catch (Exception ignored) { }
        view = null;
        layoutParams = null;
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
