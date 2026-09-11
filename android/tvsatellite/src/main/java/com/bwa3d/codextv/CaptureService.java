package com.bwa3d.codextv;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.util.DisplayMetrics;
import android.view.WindowManager;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public class CaptureService extends Service {
    public static final String EXTRA_RESULT_CODE = "result_code";
    public static final String EXTRA_RESULT_DATA = "result_data";

    public static final class CaptureResult {
        public final byte[] jpeg;
        public final String dHash;
        public final int sourceWidth;
        public final int sourceHeight;
        public final int outputWidth;
        public final int outputHeight;
        public final String profile;
        public final double focusContrast;

        CaptureResult(byte[] jpeg, String dHash, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, String profile, double focusContrast) {
            this.jpeg = jpeg;
            this.dHash = dHash;
            this.sourceWidth = sourceWidth;
            this.sourceHeight = sourceHeight;
            this.outputWidth = outputWidth;
            this.outputHeight = outputHeight;
            this.profile = profile;
            this.focusContrast = focusContrast;
        }
    }

    private static final int NOTIFICATION_ID = 4103;
    private static final String CHANNEL_ID = "codex_tv_capture";
    private static final AtomicReference<byte[]> LAST_JPEG = new AtomicReference<>();
    private static volatile long lastFrameAt = 0L;
    private static volatile String lastDHash = null;
    private static volatile CaptureService instance;

    private final Object captureLock = new Object();
    private MediaProjection projection;
    private HandlerThread captureThread;
    private Handler captureHandler;

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        captureThread = new HandlerThread("CodexTvCapture");
        captureThread.start();
        captureHandler = new Handler(captureThread.getLooper());
        createNotificationChannel();
        startForeground(NOTIFICATION_ID, buildNotification());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;
        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        Intent resultData = intent.getParcelableExtra(EXTRA_RESULT_DATA);
        if (resultData == null) return START_NOT_STICKY;
        MediaProjectionManager manager = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        if (manager == null) return START_NOT_STICKY;
        MediaProjection next = manager.getMediaProjection(resultCode, resultData);
        if (next == null) return START_NOT_STICKY;
        synchronized (captureLock) {
            if (projection != null) try { projection.stop(); } catch (Throwable ignored) {}
            projection = next;
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() {
                    synchronized (captureLock) {
                        projection = null;
                        LAST_JPEG.set(null);
                        lastDHash = null;
                        lastFrameAt = 0L;
                    }
                }
            }, captureHandler);
        }
        return START_NOT_STICKY;
    }

    public static boolean isReady() {
        CaptureService service = instance;
        return service != null && service.projection != null;
    }

    public static CaptureResult captureOnce(long timeoutMs, String profile) {
        CaptureService service = instance;
        if (service == null) return null;
        return service.captureOnceInternal(timeoutMs, normalizeProfile(profile));
    }

    private static String normalizeProfile(String profile) {
        String p = profile == null ? "full" : profile.trim().toLowerCase(Locale.US);
        return ("preview".equals(p) || "focus".equals(p)) ? p : "full";
    }

    private CaptureResult captureOnceInternal(long timeoutMs, String profile) {
        synchronized (captureLock) {
            if (projection == null) return null;
            WindowManager wm = (WindowManager) getSystemService(WINDOW_SERVICE);
            if (wm == null) return null;
            DisplayMetrics metrics = new DisplayMetrics();
            wm.getDefaultDisplay().getRealMetrics(metrics);
            final int width = metrics.widthPixels;
            final int height = metrics.heightPixels;
            final int density = metrics.densityDpi;
            final CountDownLatch latch = new CountDownLatch(1);
            final AtomicReference<CaptureResult> result = new AtomicReference<>();
            final ImageReader reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);

            reader.setOnImageAvailableListener(r -> {
                Image image = null;
                try {
                    image = r.acquireLatestImage();
                    if (image == null || result.get() != null) return;
                    Image.Plane plane = image.getPlanes()[0];
                    ByteBuffer buffer = plane.getBuffer();
                    int pixelStride = plane.getPixelStride();
                    int rowStride = plane.getRowStride();
                    int rowPadding = rowStride - pixelStride * width;
                    int paddedWidth = width + rowPadding / pixelStride;
                    Bitmap padded = Bitmap.createBitmap(paddedWidth, height, Bitmap.Config.ARGB_8888);
                    padded.copyPixelsFromBuffer(buffer);
                    Bitmap full = Bitmap.createBitmap(padded, 0, 0, width, height);
                    padded.recycle();

                    String dHash = computeDHash(full);
                    Rect focus = TvAccessibilityService.focusedBounds();
                    double focusContrast = computeFocusContrast(full, focus);
                    Bitmap working = full;
                    if ("focus".equals(profile) && focus != null) {
                        Rect crop = expandedCrop(focus, width, height);
                        if (crop.width() > 8 && crop.height() > 8) working = Bitmap.createBitmap(full, crop.left, crop.top, crop.width(), crop.height());
                    }

                    int maxWidth = "preview".equals(profile) ? 640 : ("focus".equals(profile) ? 640 : 960);
                    int quality = "preview".equals(profile) ? 52 : ("focus".equals(profile) ? 62 : 68);
                    Bitmap output = working;
                    if (working.getWidth() > maxWidth) {
                        int scaledHeight = Math.max(1, Math.round(working.getHeight() * (maxWidth / (float) working.getWidth())));
                        output = Bitmap.createScaledBitmap(working, maxWidth, scaledHeight, true);
                    }

                    ByteArrayOutputStream baos = new ByteArrayOutputStream();
                    output.compress(Bitmap.CompressFormat.JPEG, quality, baos);
                    byte[] jpeg = baos.toByteArray();
                    CaptureResult capture = new CaptureResult(jpeg, dHash, width, height, output.getWidth(), output.getHeight(), profile, focusContrast);
                    result.set(capture);
                    LAST_JPEG.set(jpeg);
                    lastDHash = dHash;
                    lastFrameAt = System.currentTimeMillis();

                    if (output != working) output.recycle();
                    if (working != full) working.recycle();
                    full.recycle();
                } catch (Throwable ignored) {
                } finally {
                    if (image != null) image.close();
                    if (result.get() != null) latch.countDown();
                }
            }, captureHandler);

            VirtualDisplay display = null;
            try {
                display = projection.createVirtualDisplay("CodexTvSatelliteOnce", width, height, density,
                        DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, reader.getSurface(), null, captureHandler);
                latch.await(Math.max(250L, Math.min(4000L, timeoutMs)), TimeUnit.MILLISECONDS);
                return result.get();
            } catch (Throwable ignored) {
                return null;
            } finally {
                if (display != null) try { display.release(); } catch (Throwable ignored) {}
                try { reader.close(); } catch (Throwable ignored) {}
            }
        }
    }

    private static Rect expandedCrop(Rect src, int width, int height) {
        int xPad = Math.max(40, src.width());
        int yPad = Math.max(30, src.height());
        return new Rect(Math.max(0, src.left - xPad), Math.max(0, src.top - yPad), Math.min(width, src.right + xPad), Math.min(height, src.bottom + yPad));
    }

    private static String computeDHash(Bitmap bitmap) {
        Bitmap tiny = Bitmap.createScaledBitmap(bitmap, 9, 8, false);
        long hash = 0L;
        int bit = 0;
        for (int y = 0; y < 8; y++) {
            for (int x = 0; x < 8; x++) {
                int a = luminance(tiny.getPixel(x, y));
                int b = luminance(tiny.getPixel(x + 1, y));
                if (a > b) hash |= (1L << bit);
                bit++;
            }
        }
        tiny.recycle();
        return String.format(Locale.US, "%016x", hash);
    }

    private static int luminance(int c) {
        return (Color.red(c) * 299 + Color.green(c) * 587 + Color.blue(c) * 114) / 1000;
    }

    private static double computeFocusContrast(Bitmap bitmap, Rect rect) {
        if (rect == null || rect.width() < 4 || rect.height() < 4) return -1.0;
        int left = Math.max(1, rect.left), top = Math.max(1, rect.top), right = Math.min(bitmap.getWidth() - 2, rect.right), bottom = Math.min(bitmap.getHeight() - 2, rect.bottom);
        if (right <= left || bottom <= top) return -1.0;
        long border = 0, inner = 0; int bc = 0, ic = 0;
        int stepX = Math.max(1, (right - left) / 24), stepY = Math.max(1, (bottom - top) / 16);
        for (int x = left; x <= right; x += stepX) {
            border += luminance(bitmap.getPixel(x, top)) + luminance(bitmap.getPixel(x, bottom)); bc += 2;
        }
        for (int y = top; y <= bottom; y += stepY) {
            border += luminance(bitmap.getPixel(left, y)) + luminance(bitmap.getPixel(right, y)); bc += 2;
        }
        int il = left + Math.max(1, (right-left)/4), ir = right - Math.max(1, (right-left)/4), it = top + Math.max(1, (bottom-top)/4), ib = bottom - Math.max(1, (bottom-top)/4);
        for (int y = it; y <= ib; y += Math.max(1, (ib-it)/8)) for (int x = il; x <= ir; x += Math.max(1, (ir-il)/12)) { inner += luminance(bitmap.getPixel(x, y)); ic++; }
        if (bc == 0 || ic == 0) return -1.0;
        return Math.abs((border / (double) bc) - (inner / (double) ic)) / 255.0;
    }

    public static byte[] getLatestJpeg() { return LAST_JPEG.get(); }
    public static String getLastDHash() { return lastDHash; }
    public static boolean hasFrame() { return LAST_JPEG.get() != null && System.currentTimeMillis() - lastFrameAt < 5000; }
    public static long getLastFrameAt() { return lastFrameAt; }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) nm.createNotificationChannel(new NotificationChannel(CHANNEL_ID, "Codex TV capture", NotificationManager.IMPORTANCE_LOW));
        }
    }

    private Notification buildNotification() {
        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O ? new Notification.Builder(this, CHANNEL_ID) : new Notification.Builder(this);
        return builder.setContentTitle("Codex TV Satellite").setContentText("Captura bajo demanda preparada").setSmallIcon(android.R.drawable.ic_menu_view).setOngoing(true).build();
    }

    @Override
    public void onDestroy() {
        synchronized (captureLock) {
            if (projection != null) try { projection.stop(); } catch (Throwable ignored) {}
            projection = null;
        }
        LAST_JPEG.set(null); lastDHash = null; lastFrameAt = 0L;
        if (captureThread != null) captureThread.quitSafely();
        captureHandler = null; captureThread = null; instance = null;
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
