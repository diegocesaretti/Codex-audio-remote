package com.bwa3d.codexremote;

import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.AutomaticGainControl;
import android.media.audiofx.NoiseSuppressor;

/** Optional platform DSP. Availability and quality depend on the Android device/vendor. */
public final class NativeAudioEffects implements AutoCloseable {
    private NoiseSuppressor ns;
    private AutomaticGainControl agc;
    private AcousticEchoCanceler aec;

    public NativeAudioEffects(int audioSessionId, boolean enableNs, boolean enableAgc, boolean enableAec) {
        if (audioSessionId <= 0) return;
        try {
            if (enableNs && NoiseSuppressor.isAvailable()) {
                ns = NoiseSuppressor.create(audioSessionId);
                if (ns != null) ns.setEnabled(true);
            }
        } catch (Throwable ignored) { ns = null; }
        try {
            if (enableAgc && AutomaticGainControl.isAvailable()) {
                agc = AutomaticGainControl.create(audioSessionId);
                if (agc != null) agc.setEnabled(true);
            }
        } catch (Throwable ignored) { agc = null; }
        try {
            if (enableAec && AcousticEchoCanceler.isAvailable()) {
                aec = AcousticEchoCanceler.create(audioSessionId);
                if (aec != null) aec.setEnabled(true);
            }
        } catch (Throwable ignored) { aec = null; }
    }

    public String summary() {
        return "NS " + (ns != null ? "ON" : "off") + " · AGC " + (agc != null ? "ON" : "off") + " · AEC " + (aec != null ? "ON" : "off");
    }

    @Override public void close() {
        if (ns != null) { try { ns.setEnabled(false); } catch (Throwable ignored) { } try { ns.release(); } catch (Throwable ignored) { } ns = null; }
        if (agc != null) { try { agc.setEnabled(false); } catch (Throwable ignored) { } try { agc.release(); } catch (Throwable ignored) { } agc = null; }
        if (aec != null) { try { aec.setEnabled(false); } catch (Throwable ignored) { } try { aec.release(); } catch (Throwable ignored) { } aec = null; }
    }
}
