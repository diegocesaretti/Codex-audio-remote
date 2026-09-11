package com.bwa3d.codextv;

import android.content.Context;
import android.os.Build;

import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class LocalHttpServer {
    private static final int PORT = 8765;
    private static volatile LocalHttpServer instance;

    private final Context context;
    private final ExecutorService pool = Executors.newCachedThreadPool();
    private ServerSocket serverSocket;
    private volatile boolean running;

    private LocalHttpServer(Context context) {
        this.context = context.getApplicationContext();
    }

    public static synchronized void ensureStarted(Context context) {
        if (instance != null && instance.running) return;
        instance = new LocalHttpServer(context);
        instance.start();
    }

    private void start() {
        running = true;
        Thread acceptThread = new Thread(() -> {
            try {
                serverSocket = new ServerSocket(PORT);
                while (running) {
                    Socket socket = serverSocket.accept();
                    pool.execute(() -> handle(socket));
                }
            } catch (IOException ignored) {
            } finally {
                running = false;
            }
        }, "CodexTvHttp");
        acceptThread.setDaemon(true);
        acceptThread.start();
    }

    public static String getLocalIpAddress() {
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nif.isUp() || nif.isLoopback()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (addr instanceof Inet4Address && !addr.isLoopbackAddress() && addr.isSiteLocalAddress()) {
                        return addr.getHostAddress();
                    }
                }
            }
        } catch (Throwable ignored) {
        }
        return null;
    }

    private void handle(Socket socket) {
        try (Socket s = socket;
             InputStream rawIn = new BufferedInputStream(s.getInputStream());
             OutputStream out = new BufferedOutputStream(s.getOutputStream())) {
            s.setSoTimeout(5000);
            String requestLine = readLine(rawIn);
            if (requestLine == null || requestLine.isEmpty()) return;
            String[] parts = requestLine.split(" ");
            if (parts.length < 2) {
                sendJson(out, 400, json(false, "bad_request"));
                return;
            }
            String method = parts[0].toUpperCase();
            String path = parts[1];

            Map<String, String> headers = new HashMap<>();
            while (true) {
                String line = readLine(rawIn);
                if (line == null || line.isEmpty()) break;
                int colon = line.indexOf(':');
                if (colon > 0) {
                    headers.put(line.substring(0, colon).trim().toLowerCase(), line.substring(colon + 1).trim());
                }
            }

            int contentLength = 0;
            try { contentLength = Integer.parseInt(headers.getOrDefault("content-length", "0")); } catch (Throwable ignored) {}
            byte[] body = readExact(rawIn, Math.max(0, Math.min(contentLength, 1024 * 128)));

            if ("/health".equals(path)) {
                JSONObject health = new JSONObject();
                health.put("ok", true);
                health.put("name", "Codex TV Satellite");
                health.put("version", "0.1.3");
                health.put("android_api", Build.VERSION.SDK_INT);
                health.put("accessibility", TvAccessibilityService.isConnected());
                health.put("screenshot", CaptureService.hasFrame());
                health.put("authentication", "none");
                sendJson(out, 200, health);
                return;
            }

            if ("GET".equals(method) && "/observe".equals(path)) {
                JSONObject snapshot = TvAccessibilityService.snapshot();
                snapshot.put("ok", true);
                snapshot.put("screenshot_available", CaptureService.hasFrame());
                snapshot.put("screenshot_age_ms", CaptureService.getLastFrameAt() == 0L ? -1 : System.currentTimeMillis() - CaptureService.getLastFrameAt());
                JSONObject caps = new JSONObject();
                caps.put("screenshot", true);
                caps.put("ui_tree", true);
                caps.put("tap", Build.VERSION.SDK_INT >= 24);
                caps.put("set_text", true);
                caps.put("home_back", true);
                caps.put("native_dpad", Build.VERSION.SDK_INT >= 33);
                caps.put("legacy_dpad_fallback", "home_assistant");
                snapshot.put("capabilities", caps);
                sendJson(out, 200, snapshot);
                return;
            }

            if ("GET".equals(method) && "/screenshot".equals(path)) {
                byte[] jpeg = CaptureService.getLatestJpeg();
                if (jpeg == null) {
                    sendJson(out, 503, json(false, "screenshot_not_ready"));
                } else {
                    sendBytes(out, 200, "image/jpeg", jpeg);
                }
                return;
            }

            if ("POST".equals(method) && "/action".equals(path)) {
                JSONObject req;
                try {
                    req = new JSONObject(new String(body, StandardCharsets.UTF_8));
                } catch (Throwable t) {
                    sendJson(out, 400, json(false, "invalid_json"));
                    return;
                }
                JSONObject result = executeAction(req);
                sendJson(out, result.optBoolean("ok") ? 200 : 409, result);
                return;
            }

            sendJson(out, 404, json(false, "not_found"));
        } catch (Throwable ignored) {
        }
    }

    private JSONObject executeAction(JSONObject req) {
        String action = req.optString("action", "");
        boolean ok = false;
        JSONObject out = new JSONObject();
        try {
            switch (action) {
                case "home":
                    ok = TvAccessibilityService.goHome();
                    break;
                case "back":
                    ok = TvAccessibilityService.goBack();
                    break;
                case "click_focused":
                    ok = TvAccessibilityService.clickFocused();
                    break;
                case "click_text":
                    ok = TvAccessibilityService.clickText(req.optString("text", ""));
                    break;
                case "set_text":
                    ok = TvAccessibilityService.setText(req.optString("text", ""));
                    break;
                case "tap":
                    ok = TvAccessibilityService.tapNormalized((float) req.optDouble("x", -1), (float) req.optDouble("y", -1));
                    if (!ok) out.put("hint", "x and y must be normalized values between 0 and 1");
                    break;
                case "launch_app":
                    ok = TvAccessibilityService.launchApp(context, req.optString("app", ""));
                    break;
                case "dpad_up":
                    ok = TvAccessibilityService.dpad("up");
                    break;
                case "dpad_down":
                    ok = TvAccessibilityService.dpad("down");
                    break;
                case "dpad_left":
                    ok = TvAccessibilityService.dpad("left");
                    break;
                case "dpad_right":
                    ok = TvAccessibilityService.dpad("right");
                    break;
                case "dpad_center":
                case "ok":
                    ok = TvAccessibilityService.dpad("center");
                    break;
                default:
                    out.put("ok", false);
                    out.put("error", "unknown_action");
                    return out;
            }
            out.put("ok", ok);
            out.put("action", action);
            if (!ok && action.startsWith("dpad_") && Build.VERSION.SDK_INT < 33) {
                out.put("error", "native_dpad_unavailable_on_this_android_version");
                out.put("fallback", "home_assistant");
            } else if (!ok) {
                out.put("error", "action_failed");
            }
        } catch (Throwable t) {
            try {
                out.put("ok", false);
                out.put("error", t.toString());
            } catch (Throwable ignored) {}
        }
        return out;
    }

    private static JSONObject json(boolean ok, String error) {
        JSONObject obj = new JSONObject();
        try {
            obj.put("ok", ok);
            if (error != null) obj.put("error", error);
        } catch (Throwable ignored) {}
        return obj;
    }

    private static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        int prev = -1;
        while (true) {
            int b = in.read();
            if (b < 0) break;
            if (prev == '\r' && b == '\n') break;
            if (prev >= 0) buffer.write(prev);
            prev = b;
            if (buffer.size() > 8192) throw new IOException("header too large");
        }
        if (prev >= 0 && prev != '\r') buffer.write(prev);
        if (buffer.size() == 0 && prev < 0) return null;
        return buffer.toString("UTF-8");
    }

    private static byte[] readExact(InputStream in, int length) throws IOException {
        byte[] data = new byte[length];
        int off = 0;
        while (off < length) {
            int n = in.read(data, off, length - off);
            if (n < 0) break;
            off += n;
        }
        if (off == length) return data;
        byte[] shorter = new byte[off];
        System.arraycopy(data, 0, shorter, 0, off);
        return shorter;
    }

    private static void sendJson(OutputStream out, int status, JSONObject obj) throws IOException {
        sendBytes(out, status, "application/json; charset=utf-8", obj.toString().getBytes(StandardCharsets.UTF_8));
    }

    private static void sendBytes(OutputStream out, int status, String contentType, byte[] body) throws IOException {
        String reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : status == 409 ? "Conflict" : "Service Unavailable";
        String headers = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.length + "\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n\r\n";
        out.write(headers.getBytes(StandardCharsets.US_ASCII));
        out.write(body);
        out.flush();
    }
}
