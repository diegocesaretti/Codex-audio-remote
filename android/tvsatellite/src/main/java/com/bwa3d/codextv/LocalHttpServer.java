package com.bwa3d.codextv;

import android.content.Context;
import android.os.Build;
import android.util.Base64;

import org.json.JSONArray;
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
import java.net.URLDecoder;
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

    private LocalHttpServer(Context context) { this.context = context.getApplicationContext(); }

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
                while (running) pool.execute(() -> handle(acceptSocket()));
            } catch (IOException ignored) {
            } finally { running = false; }
        }, "CodexTvHttp");
        acceptThread.setDaemon(true);
        acceptThread.start();
    }

    private Socket acceptSocket() {
        try { return serverSocket.accept(); } catch (Throwable ignored) { return null; }
    }

    public static String getLocalIpAddress() {
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nif.isUp() || nif.isLoopback()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (addr instanceof Inet4Address && !addr.isLoopbackAddress() && addr.isSiteLocalAddress()) return addr.getHostAddress();
                }
            }
        } catch (Throwable ignored) {}
        return null;
    }

    private void handle(Socket socket) {
        if (socket == null) return;
        try (Socket s = socket; InputStream rawIn = new BufferedInputStream(s.getInputStream()); OutputStream out = new BufferedOutputStream(s.getOutputStream())) {
            s.setSoTimeout(8000);
            String requestLine = readLine(rawIn);
            if (requestLine == null || requestLine.isEmpty()) return;
            String[] parts = requestLine.split(" ");
            if (parts.length < 2) { sendJson(out, 400, json(false, "bad_request")); return; }
            String method = parts[0].toUpperCase();
            String target = parts[1];
            String path = target;
            String queryText = "";
            int q = target.indexOf('?');
            if (q >= 0) { path = target.substring(0, q); queryText = target.substring(q + 1); }
            Map<String, String> query = parseQuery(queryText);

            Map<String, String> headers = new HashMap<>();
            while (true) {
                String line = readLine(rawIn);
                if (line == null || line.isEmpty()) break;
                int colon = line.indexOf(':');
                if (colon > 0) headers.put(line.substring(0, colon).trim().toLowerCase(), line.substring(colon + 1).trim());
            }
            int contentLength = 0;
            try { contentLength = Integer.parseInt(headers.getOrDefault("content-length", "0")); } catch (Throwable ignored) {}
            byte[] body = readExact(rawIn, Math.max(0, Math.min(contentLength, 1024 * 256)));

            if ("/health".equals(path)) { sendJson(out, 200, health()); return; }

            if ("GET".equals(method) && "/observe".equals(path)) {
                long since = longQuery(query, "since_sequence", -1L);
                long waitMs = longQuery(query, "wait_ms", 0L);
                JSONObject settle = waitMs > 0 ? TvAccessibilityService.waitForUiSettled(since, Math.min(220L, waitMs), waitMs) : null;
                JSONObject snapshot = observation();
                if (settle != null) snapshot.put("settle", settle);
                sendJson(out, 200, snapshot);
                return;
            }

            if ("GET".equals(method) && "/screenshot".equals(path)) {
                if (!CaptureService.isReady()) { sendJson(out, 503, json(false, "screen_capture_not_authorized")); return; }
                String profile = query.getOrDefault("profile", "full");
                CaptureService.CaptureResult capture = CaptureService.captureOnce(2500, profile);
                if (capture == null) sendJson(out, 503, json(false, "screenshot_timeout"));
                else sendCapture(out, capture);
                return;
            }

            if ("POST".equals(method) && "/action".equals(path)) {
                JSONObject req = parseBody(body, out); if (req == null) return;
                JSONObject result = executeAction(req);
                sendJson(out, result.optBoolean("ok") ? 200 : 409, result);
                return;
            }

            if ("POST".equals(method) && "/execute".equals(path)) {
                JSONObject req = parseBody(body, out); if (req == null) return;
                sendJson(out, 200, executeObserve(req));
                return;
            }

            sendJson(out, 404, json(false, "not_found"));
        } catch (Throwable ignored) {}
    }

    private JSONObject health() {
        JSONObject h = new JSONObject();
        try {
            h.put("ok", true); h.put("name", "Codex TV Satellite"); h.put("version", "0.1.5");
            h.put("android_api", Build.VERSION.SDK_INT); h.put("accessibility", TvAccessibilityService.isConnected());
            h.put("screenshot", CaptureService.isReady()); h.put("capture_mode", "on_demand"); h.put("authentication", "none");
            h.put("start_on_boot", SatellitePrefs.startOnBoot(context)); h.put("ui_event_sequence", TvAccessibilityService.eventSequence());
            h.put("execute_observe", true); h.put("progressive_capture", true); h.put("perceptual_hash", "dhash64");
        } catch (Throwable ignored) {}
        return h;
    }

    private JSONObject observation() {
        JSONObject snapshot = TvAccessibilityService.snapshot();
        try {
            snapshot.put("ok", true); snapshot.put("screenshot_ready", CaptureService.isReady()); snapshot.put("capture_mode", "on_demand");
            snapshot.put("last_screenshot_age_ms", CaptureService.getLastFrameAt() == 0L ? -1 : System.currentTimeMillis() - CaptureService.getLastFrameAt());
            if (CaptureService.getLastDHash() != null) snapshot.put("last_frame_dhash", CaptureService.getLastDHash());
            JSONObject caps = new JSONObject();
            caps.put("screenshot", true); caps.put("screenshot_on_demand", true); caps.put("screenshot_profiles", new JSONArray().put("preview").put("full").put("focus"));
            caps.put("ui_tree", true); caps.put("ui_event_wait", true); caps.put("execute_observe", true); caps.put("tap", Build.VERSION.SDK_INT >= 24);
            caps.put("set_text", true); caps.put("home_back", true); caps.put("native_dpad", Build.VERSION.SDK_INT >= 33); caps.put("legacy_dpad_fallback", "home_assistant");
            snapshot.put("capabilities", caps);
        } catch (Throwable ignored) {}
        return snapshot;
    }

    private JSONObject executeObserve(JSONObject req) {
        JSONObject out = new JSONObject();
        try {
            long before = TvAccessibilityService.eventSequence();
            JSONArray actions = req.optJSONArray("actions");
            if (actions == null) {
                actions = new JSONArray();
                if (req.has("action")) actions.put(req);
            }
            JSONArray results = new JSONArray();
            boolean allOk = true;
            for (int i = 0; i < actions.length(); i++) {
                JSONObject actionReq = actions.optJSONObject(i);
                if (actionReq == null) continue;
                JSONObject result = executeAction(actionReq);
                results.put(result);
                if (!result.optBoolean("ok")) { allOk = false; break; }
                long gap = Math.max(0L, Math.min(300L, actionReq.optLong("gap_ms", req.optLong("gap_ms", 70L))));
                if (gap > 0L && i + 1 < actions.length()) try { Thread.sleep(gap); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            }
            long quiet = Math.max(80L, Math.min(400L, req.optLong("quiet_ms", 140L)));
            long timeout = Math.max(150L, Math.min(4000L, req.optLong("wait_timeout_ms", 1500L)));
            JSONObject settle = TvAccessibilityService.waitForUiSettled(before, quiet, timeout);
            JSONObject obs = observation();
            String profile = req.optString("screenshot_profile", "preview");
            CaptureService.CaptureResult capture = req.optBoolean("screenshot", true) && CaptureService.isReady() ? CaptureService.captureOnce(2500, profile) : null;
            out.put("ok", allOk); out.put("actions", results); out.put("settle", settle); out.put("observation", obs);
            out.put("session_id", req.optString("session_id", "")); out.put("capture_mode", "on_demand");
            if (capture != null) {
                out.put("screenshot_b64", Base64.encodeToString(capture.jpeg, Base64.NO_WRAP));
                out.put("screenshot_content_type", "image/jpeg"); out.put("frame_dhash", capture.dHash); out.put("screenshot_profile", capture.profile);
                out.put("screenshot_width", capture.outputWidth); out.put("screenshot_height", capture.outputHeight); out.put("focus_contrast", capture.focusContrast);
            }
        } catch (Throwable t) {
            try { out.put("ok", false); out.put("error", t.toString()); } catch (Throwable ignored) {}
        }
        return out;
    }

    private JSONObject parseBody(byte[] body, OutputStream out) throws IOException {
        try { return new JSONObject(new String(body, StandardCharsets.UTF_8)); }
        catch (Throwable t) { sendJson(out, 400, json(false, "invalid_json")); return null; }
    }

    private JSONObject executeAction(JSONObject req) {
        String action = req.optString("action", "");
        boolean ok = false;
        JSONObject out = new JSONObject();
        try {
            switch (action) {
                case "home": ok = TvAccessibilityService.goHome(); break;
                case "back": ok = TvAccessibilityService.goBack(); break;
                case "click_focused": ok = TvAccessibilityService.clickFocused(); break;
                case "click_text": ok = TvAccessibilityService.clickText(req.optString("text", "")); break;
                case "set_text": ok = TvAccessibilityService.setText(req.optString("text", "")); break;
                case "tap": ok = TvAccessibilityService.tapNormalized((float) req.optDouble("x", -1), (float) req.optDouble("y", -1)); if (!ok) out.put("hint", "x and y must be normalized values between 0 and 1"); break;
                case "launch_app": ok = TvAccessibilityService.launchApp(context, req.optString("app", "")); break;
                case "dpad_up": ok = TvAccessibilityService.dpad("up"); break;
                case "dpad_down": ok = TvAccessibilityService.dpad("down"); break;
                case "dpad_left": ok = TvAccessibilityService.dpad("left"); break;
                case "dpad_right": ok = TvAccessibilityService.dpad("right"); break;
                case "dpad_center": case "ok": ok = TvAccessibilityService.dpad("center"); break;
                default: out.put("ok", false); out.put("error", "unknown_action"); return out;
            }
            out.put("ok", ok); out.put("action", action); out.put("ui_event_sequence", TvAccessibilityService.eventSequence());
            if (!ok && action.startsWith("dpad_") && Build.VERSION.SDK_INT < 33) { out.put("error", "native_dpad_unavailable_on_this_android_version"); out.put("fallback", "home_assistant"); }
            else if (!ok) out.put("error", "action_failed");
        } catch (Throwable t) { try { out.put("ok", false); out.put("error", t.toString()); } catch (Throwable ignored) {} }
        return out;
    }

    private static Map<String, String> parseQuery(String text) {
        Map<String, String> out = new HashMap<>();
        if (text == null || text.isEmpty()) return out;
        for (String pair : text.split("&")) {
            int eq = pair.indexOf('=');
            String k = eq < 0 ? pair : pair.substring(0, eq);
            String v = eq < 0 ? "" : pair.substring(eq + 1);
            try { out.put(URLDecoder.decode(k, "UTF-8"), URLDecoder.decode(v, "UTF-8")); } catch (Throwable ignored) { out.put(k, v); }
        }
        return out;
    }

    private static long longQuery(Map<String, String> query, String key, long fallback) {
        try { return Long.parseLong(query.getOrDefault(key, String.valueOf(fallback))); } catch (Throwable ignored) { return fallback; }
    }

    private static JSONObject json(boolean ok, String error) {
        JSONObject obj = new JSONObject(); try { obj.put("ok", ok); if (error != null) obj.put("error", error); } catch (Throwable ignored) {} return obj;
    }

    private static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream(); int prev = -1;
        while (true) { int b = in.read(); if (b < 0) break; if (prev == '\r' && b == '\n') break; if (prev >= 0) buffer.write(prev); prev = b; if (buffer.size() > 8192) throw new IOException("header too large"); }
        if (prev >= 0 && prev != '\r') buffer.write(prev); if (buffer.size() == 0 && prev < 0) return null; return buffer.toString("UTF-8");
    }

    private static byte[] readExact(InputStream in, int length) throws IOException {
        byte[] data = new byte[length]; int off = 0; while (off < length) { int n = in.read(data, off, length - off); if (n < 0) break; off += n; }
        if (off == length) return data; byte[] shorter = new byte[off]; System.arraycopy(data, 0, shorter, 0, off); return shorter;
    }

    private static void sendJson(OutputStream out, int status, JSONObject obj) throws IOException { sendBytes(out, status, "application/json; charset=utf-8", obj.toString().getBytes(StandardCharsets.UTF_8), null); }

    private static void sendCapture(OutputStream out, CaptureService.CaptureResult c) throws IOException {
        Map<String,String> headers = new HashMap<>(); headers.put("X-Codex-DHash", c.dHash); headers.put("X-Codex-Profile", c.profile);
        headers.put("X-Codex-Focus-Contrast", String.format(java.util.Locale.US, "%.4f", c.focusContrast)); headers.put("X-Codex-Ui-Sequence", String.valueOf(TvAccessibilityService.eventSequence()));
        sendBytes(out, 200, "image/jpeg", c.jpeg, headers);
    }

    private static void sendBytes(OutputStream out, int status, String contentType, byte[] body, Map<String,String> extra) throws IOException {
        String reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : status == 409 ? "Conflict" : "Service Unavailable";
        StringBuilder headers = new StringBuilder("HTTP/1.1 ").append(status).append(' ').append(reason).append("\r\nContent-Type: ").append(contentType).append("\r\nContent-Length: ").append(body.length).append("\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (extra != null) for (Map.Entry<String,String> e : extra.entrySet()) headers.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
        headers.append("\r\n"); out.write(headers.toString().getBytes(StandardCharsets.US_ASCII)); out.write(body); out.flush();
    }
}
