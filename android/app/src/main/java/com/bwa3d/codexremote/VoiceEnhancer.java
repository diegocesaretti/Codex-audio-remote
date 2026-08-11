package com.bwa3d.codexremote;

/**
 * Lightweight stateful PCM16 voice enhancer designed for old Android devices.
 * Pipeline: high-pass/DC blocker -> soft compressor -> slow AGC -> soft limiter.
 * No allocations are performed while processing audio chunks.
 */
public final class VoiceEnhancer {
    public static final String OFF = "off";
    public static final String SOFT = "soft";
    public static final String NORMAL = "normal";
    public static final String STRONG = "strong";

    private final boolean enabled;
    private final double hpA;
    private final double threshold;
    private final double ratio;
    private final double targetRms;
    private final double maxAgc;
    private final double attack;
    private final double release;
    private final double limiter;

    private double prevX;
    private double prevY;
    private double envelope = 0.05;
    private double agcGain = 1.0;

    public VoiceEnhancer(String mode, int sampleRate) {
        String m = mode == null ? OFF : mode;
        enabled = !OFF.equals(m);
        double cutoffHz = 80.0;
        hpA = Math.exp(-2.0 * Math.PI * cutoffHz / Math.max(8000.0, sampleRate));

        if (STRONG.equals(m)) {
            threshold = 0.16; ratio = 4.0; targetRms = 0.20; maxAgc = 4.0;
            attack = 0.16; release = 0.010; limiter = 0.94;
        } else if (NORMAL.equals(m)) {
            threshold = 0.20; ratio = 3.0; targetRms = 0.16; maxAgc = 3.0;
            attack = 0.12; release = 0.008; limiter = 0.95;
        } else {
            threshold = 0.25; ratio = 2.0; targetRms = 0.12; maxAgc = 2.0;
            attack = 0.09; release = 0.006; limiter = 0.96;
        }
    }

    public void processInPlace(byte[] data, int count) {
        if (!enabled || data == null || count < 2) return;

        for (int i = 0; i + 1 < count; i += 2) {
            int raw = (short)((data[i] & 0xff) | (data[i + 1] << 8));
            double x = raw / 32768.0;

            // One-pole high-pass / DC blocker.
            double y = hpA * (prevY + x - prevX);
            prevX = x;
            prevY = y;

            double abs = Math.abs(y);
            double envCoeff = abs > envelope ? attack : release;
            envelope += (abs - envelope) * envCoeff;

            // Soft compressor above threshold.
            double sign = y < 0 ? -1.0 : 1.0;
            double mag = Math.abs(y);
            if (mag > threshold) mag = threshold + (mag - threshold) / ratio;
            y = sign * mag;

            // Slow AGC based on envelope. Avoid boosting near-silence/noise floor.
            double desired = 1.0;
            if (envelope > 0.015) desired = Math.min(maxAgc, targetRms / envelope);
            double agcCoeff = desired < agcGain ? 0.025 : 0.0025;
            agcGain += (desired - agcGain) * agcCoeff;
            y *= agcGain;

            // Smooth limiter using tanh; avoids hard clipping artifacts.
            y = limiter * Math.tanh(y / limiter);
            int out = (int)Math.round(y * 32767.0);
            if (out > 32767) out = 32767;
            if (out < -32768) out = -32768;
            data[i] = (byte)(out & 0xff);
            data[i + 1] = (byte)((out >> 8) & 0xff);
        }
    }
}
