package com.bwa3d.codexremote;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Movie;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;

import java.io.InputStream;

/**
 * Optional fullscreen, non-touchable wake overlay. Android remains fully usable underneath it.
 * The overlay uses the persisted user-selected GIF and a selectable solid background color.
 */
public final class WakeGifOverlayController {
    private static final long FADE_MS = 240L;

    private final Context context;
    private final WindowManager windowManager;
    private final Handler main = new Handler(Looper.getMainLooper());

    private FrameLayout root;
    private GifMovieView gifView;
    private boolean fadingOut;
    private String cachedUri = "";
    private Movie cachedMovie;
    private int loadGeneration;

    public WakeGifOverlayController(Context context) {
        this.context = context.getApplicationContext();
        this.windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
    }

    public boolean canShow() {
        return Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(context);
    }

    public void showWake() { showInternal(false); }

    /** Settings preview: ignores the enabled toggle, but still requires a selected GIF. */
    public void showPreview() { showInternal(true); }

    private void showInternal(boolean preview) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            main.post(() -> showInternal(preview));
            return;
        }
        if (!preview && !WakeGifPrefs.enabled(context)) return;
        Uri uri = WakeGifPrefs.uri(context);
        if (uri == null) {
            AndroidDebugLog.log("Wake GIF overlay skipped · no GIF selected");
            return;
        }
        if (!canShow()) {
            AndroidDebugLog.log("Wake GIF overlay skipped · draw-over-apps permission missing");
            return;
        }

        if (root != null) {
            fadingOut = false;
            root.animate().cancel();
            root.setBackgroundColor(WakeGifPrefs.backgroundColor(context));
            root.setAlpha(1f);
            applyMovie(uri);
            return;
        }

        FrameLayout container = new FrameLayout(context);
        container.setBackgroundColor(WakeGifPrefs.backgroundColor(context));
        container.setClickable(false);
        container.setFocusable(false);
        container.setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);

        GifMovieView movieView = new GifMovieView(context);
        container.addView(movieView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));

        int[] types;
        if (Build.VERSION.SDK_INT >= 26) {
            types = new int[]{WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY};
        } else if (Build.VERSION.SDK_INT >= 23) {
            types = new int[]{WindowManager.LayoutParams.TYPE_SYSTEM_ALERT, WindowManager.LayoutParams.TYPE_PHONE};
        } else {
            types = new int[]{WindowManager.LayoutParams.TYPE_PHONE};
        }

        int flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL
                | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS;

        for (int type : types) {
            WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                    WindowManager.LayoutParams.MATCH_PARENT,
                    WindowManager.LayoutParams.MATCH_PARENT,
                    type,
                    flags,
                    android.graphics.PixelFormat.TRANSLUCENT);
            params.gravity = Gravity.TOP | Gravity.START;
            try {
                container.setAlpha(0f);
                windowManager.addView(container, params);
                root = container;
                gifView = movieView;
                fadingOut = false;
                AndroidDebugLog.log("Wake GIF fullscreen overlay shown · type=" + type
                        + " · bg=" + WakeGifPrefs.colorHex(context)
                        + " · scale=" + WakeGifPrefs.scaleMode(context));
                applyMovie(uri);
                container.animate().alpha(1f).setDuration(FADE_MS).start();
                return;
            } catch (SecurityException | WindowManager.BadTokenException e) {
                AndroidDebugLog.log("Wake GIF overlay add denied · type=" + type + " · " + e.getMessage());
            } catch (RuntimeException e) {
                AndroidDebugLog.log("Wake GIF overlay add failed · type=" + type + " · " + e.getMessage());
            }
        }
    }

    /** Called from the authoritative Realtime state transition. */
    public void onServerState(String state) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            main.post(() -> onServerState(state));
            return;
        }
        if (root == null) return;
        if ("activating".equals(state)) return;
        if ("listening".equals(state) && WakeGifPrefs.keepDuringListening(context)) return;
        hide();
    }

    public void hide() {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            main.post(this::hide);
            return;
        }
        final FrameLayout current = root;
        if (current == null || fadingOut) return;
        fadingOut = true;
        current.animate().cancel();
        current.animate().alpha(0f).setDuration(FADE_MS).withEndAction(() -> {
            if (root != current) return;
            try { windowManager.removeView(current); } catch (Exception ignored) { }
            root = null;
            gifView = null;
            fadingOut = false;
            AndroidDebugLog.log("Wake GIF fullscreen overlay hidden");
        }).start();
    }

    public void destroy() {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            main.post(this::destroy);
            return;
        }
        loadGeneration++;
        if (root != null) {
            root.animate().cancel();
            try { windowManager.removeView(root); } catch (Exception ignored) { }
            root = null;
            gifView = null;
        }
        fadingOut = false;
        main.removeCallbacksAndMessages(null);
    }

    private void applyMovie(Uri uri) {
        final GifMovieView target = gifView;
        if (target == null) return;
        final String raw = uri.toString();
        target.setScaleMode(WakeGifPrefs.scaleMode(context));
        if (raw.equals(cachedUri) && cachedMovie != null) {
            target.setMovie(cachedMovie);
            return;
        }

        final int generation = ++loadGeneration;
        target.setMovie(null);
        new Thread(() -> {
            Movie movie = null;
            try (InputStream input = context.getContentResolver().openInputStream(uri)) {
                if (input != null) movie = Movie.decodeStream(input);
            } catch (Exception e) {
                AndroidDebugLog.log("Wake GIF decode failed · " + e.getMessage());
            }
            final Movie decoded = movie;
            main.post(() -> {
                if (generation != loadGeneration || gifView != target) return;
                if (decoded == null) {
                    AndroidDebugLog.log("Wake GIF decode returned null");
                    return;
                }
                cachedUri = raw;
                cachedMovie = decoded;
                target.setMovie(decoded);
                AndroidDebugLog.log("Wake GIF decoded · " + decoded.width() + "x" + decoded.height()
                        + " · duration=" + decoded.duration() + "ms");
            });
        }, "wake-gif-loader").start();
    }

    private static final class GifMovieView extends View {
        private Movie movie;
        private long startedAt;
        private String scaleMode = "contain";

        GifMovieView(Context context) {
            super(context);
            setBackgroundColor(Color.TRANSPARENT);
            setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        }

        void setScaleMode(String mode) {
            scaleMode = "cover".equals(mode) ? "cover" : "contain";
            invalidate();
        }

        void setMovie(Movie value) {
            movie = value;
            startedAt = android.os.SystemClock.uptimeMillis();
            invalidate();
        }

        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            Movie m = movie;
            if (m == null || m.width() <= 0 || m.height() <= 0) return;

            int duration = m.duration();
            if (duration <= 0) duration = 1000;
            long now = android.os.SystemClock.uptimeMillis();
            int time = (int)((now - startedAt) % duration);
            m.setTime(time);

            float sx = getWidth() / (float)m.width();
            float sy = getHeight() / (float)m.height();
            float scale = "cover".equals(scaleMode) ? Math.max(sx, sy) : Math.min(sx, sy);
            if (scale <= 0f) scale = 1f;
            float dx = (getWidth() - m.width() * scale) * 0.5f;
            float dy = (getHeight() - m.height() * scale) * 0.5f;

            int save = canvas.save();
            canvas.translate(dx, dy);
            canvas.scale(scale, scale);
            m.draw(canvas, 0f, 0f);
            canvas.restoreToCount(save);
            postInvalidateOnAnimation();
        }
    }
}
