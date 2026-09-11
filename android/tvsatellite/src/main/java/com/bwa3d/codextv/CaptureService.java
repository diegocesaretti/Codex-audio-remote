package com.bwa3d.codextv;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
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
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public class CaptureService extends Service {
    public static final String EXTRA_RESULT_CODE = "result_code";
    public static final String EXTRA_RESULT_DATA = "result_data";

    private static final int NOTIFICATION_ID = 4103;
    private static final String CHANNEL_ID = "codex_tv_capture";
    private static final AtomicReference<byte[]> LAST_JPEG = new AtomicReference<>();
    private static volatile long lastFrameAt = 0L;
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
            if (projection != null) {
                try { projection.stop(); } catch (Throwable ignored) {}
            }
            projection = next;
            projection.registerCallback(new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    synchronized (captureLock) {
                        projection = null;
                        LAST_JPEG.set(null);
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

    public static byte[] captureOnce(long timeoutMs) {
        CaptureService service = instance;
        if (service == null) return null;
        return service.captureOnceInternal(timeoutMs);
    }

    private byte[] captureOnceInternal(long timeoutMs) {
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
            final AtomicReference<byte[]> result = new AtomicReference<>();
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
                    Bitmap cropped = Bitmap.createBitmap(padded, 0, 0, width, height);
                    padded.recycle();

                    Bitmap output = cropped;
                    if (cropped.getWidth() > 960) {
                        int scaledHeight = Math.max(1, Math.round(cropped.getHeight() * (960f / cropped.getWidth())));
                        output = Bitmap.createScaledBitmap(cropped, 960, scaledHeight, true);
                    }

                    ByteArrayOutputStream baos = new ByteArrayOutputStream();
                    output.compress(Bitmap.CompressFormat.JPEG, 68, baos);
                    byte[] jpeg = baos.toByteArray();
                    result.set(jpeg);
                    LAST_JPEG.set(jpeg);
                    lastFrameAt = System.currentTimeMillis();

                    if (output != cropped) output.recycle();
                    cropped.recycle();
                } catch (Throwable ignored) {
                } finally {
                    if (image != null) image.close();
                    if (result.get() != null) latch.countDown();
                }
            }, captureHandler);

            VirtualDisplay display = null;
            try {
                display = projection.createVirtualDisplay(
                        "CodexTvSatelliteOnce",
                        width,
                        height,
                        density,
                        DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                        reader.getSurface(),
                        null,
                        captureHandler);
                latch.await(Math.max(250L, Math.min(4000L, timeoutMs)), TimeUnit.MILLISECONDS);
                return result.get();
            } catch (Throwable ignored) {
                return null;
            } finally {
                if (display != null) {
                    try { display.release(); } catch (Throwable ignored) {}
                }
                try { reader.close(); } catch (Throwable ignored) {}
            }
        }
    }

    public static byte[] getLatestJpeg() {
        return LAST_JPEG.get();
    }

    public static boolean hasFrame() {
        return LAST_JPEG.get() != null && System.currentTimeMillis() - lastFrameAt < 5000;
    }

    public static long getLastFrameAt() {
        return lastFrameAt;
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) {
                NotificationChannel channel = new NotificationChannel(
                        CHANNEL_ID,
                        "Codex TV capture",
                        NotificationManager.IMPORTANCE_LOW);
                nm.createNotificationChannel(channel);
            }
        }
    }

    private Notification buildNotification() {
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder = new Notification.Builder(this, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(this);
        }
        return builder
                .setContentTitle("Codex TV Satellite")
                .setContentText("Captura bajo demanda preparada")
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setOngoing(true)
                .build();
    }

    @Override
    public void onDestroy() {
        synchronized (captureLock) {
            if (projection != null) {
                try { projection.stop(); } catch (Throwable ignored) {}
            }
            projection = null;
        }
        LAST_JPEG.set(null);
        lastFrameAt = 0L;
        if (captureThread != null) captureThread.quitSafely();
        captureHandler = null;
        captureThread = null;
        instance = null;
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
