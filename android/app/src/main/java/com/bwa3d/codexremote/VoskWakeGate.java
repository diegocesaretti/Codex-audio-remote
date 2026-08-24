package com.bwa3d.codexremote;

import java.util.Locale;

/**
 * Conservative decision layer in front of Vosk wake recognition.
 * Vosk is an ASR rather than a dedicated keyword spotter, so a narrow grammar may still map
 * vaguely similar sounds to the configured phrase. This gate requires independent evidence:
 * exact token progression, temporal stability, plausible duration and, on API23 direct capture,
 * real acoustic energy over an adaptive noise floor.
 */
final class VoskWakeGate {
    static final class Decision {
        final boolean accepted;
        final boolean loggable;
        final String reason;

        Decision(boolean accepted, boolean loggable, String reason) {
            this.accepted = accepted;
            this.loggable = loggable;
            this.reason = reason;
        }

        static Decision accept(String reason) { return new Decision(true, true, reason); }
        static Decision reject(String reason, boolean loggable) { return new Decision(false, loggable, reason); }
    }

    private static final long CANDIDATE_TIMEOUT_MS = 2400L;
    private static final long AUDIO_WINDOW_MS = 1800L;
    private static final long COOLDOWN_MS = 3500L;
    private static final long MIN_WAKE_DURATION_MS = 260L;
    private static final long MAX_WAKE_DURATION_MS = 2300L;

    private String candidateText = "";
    private int stableHits;
    private long candidateSinceMs;
    private long lastCandidateMs;
    private long lastAcceptedMs;

    private double noiseFloor = 90.0;
    private int peakRms;
    private long peakRmsMs;

    synchronized void reset() {
        clearCandidate();
        peakRms = 0;
        peakRmsMs = 0L;
    }

    synchronized void noteFinalSilence() {
        clearCandidate();
    }

    synchronized void observeAudio(int rms, long nowMs) {
        if (rms < 0) return;

        // Asymmetric adaptive floor: follows quieter conditions quickly, rises slowly when the
        // room stays noisy, and clamps each upward step so one speech burst cannot poison it.
        double upwardClamp = Math.max(noiseFloor + 120.0, noiseFloor * 2.5);
        double observed = Math.min(rms, upwardClamp);
        double alpha = observed < noiseFloor ? 0.06 : 0.012;
        noiseFloor = (noiseFloor * (1.0 - alpha)) + (observed * alpha);
        noiseFloor = Math.max(35.0, Math.min(3500.0, noiseFloor));

        if (rms >= peakRms || nowMs - peakRmsMs > AUDIO_WINDOW_MS) {
            peakRms = rms;
            peakRmsMs = nowMs;
        }
    }

    synchronized Decision evaluate(String text, String target, int sensitivity, boolean isFinal,
                                   double confidence, long resultDurationMs, boolean requireAudioEvidence) {
        long now = System.currentTimeMillis();
        text = clean(text);
        target = clean(target);
        if (text.isEmpty() || target.isEmpty()) return Decision.reject("empty", false);
        if (now - lastAcceptedMs < COOLDOWN_MS) return Decision.reject("cooldown", false);
        if (lastCandidateMs > 0 && now - lastCandidateMs > CANDIDATE_TIMEOUT_MS) clearCandidate();

        String[] targetParts = target.split(" ");
        boolean exact = text.equals(target);
        boolean validPrefix = target.startsWith(text + " ");

        if (!exact && !validPrefix) {
            boolean related = sharesWakeToken(text, targetParts);
            clearCandidate();
            return Decision.reject("lexical mismatch: " + text, related);
        }

        if (!text.equals(candidateText)) {
            candidateText = text;
            stableHits = 1;
            if (candidateSinceMs == 0L) candidateSinceMs = now;
        } else {
            stableHits++;
        }
        lastCandidateMs = now;

        if (!exact) return Decision.reject("prefix " + text + " · hits=" + stableHits, false);

        long observedDuration = candidateSinceMs > 0 ? now - candidateSinceMs : -1L;
        long duration = resultDurationMs > 0 ? resultDurationMs : observedDuration;
        if (duration > 0 && duration < MIN_WAKE_DURATION_MS)
            return Decision.reject("too short " + duration + "ms", true);
        if (duration > MAX_WAKE_DURATION_MS)
            return Decision.reject("too long " + duration + "ms", true);

        if (requireAudioEvidence) {
            long peakAge = peakRmsMs <= 0 ? Long.MAX_VALUE : now - peakRmsMs;
            double factor = 2.35 - (Math.max(0, Math.min(100, sensitivity)) * 0.0075);
            int requiredPeak = (int)Math.max(120.0, noiseFloor * factor);
            if (peakAge > AUDIO_WINDOW_MS || peakRms < requiredPeak) {
                return Decision.reject("weak audio rms=" + peakRms + " need=" + requiredPeak + " floor=" + (int)noiseFloor, true);
            }
        }

        if (isFinal) {
            double minConfidence = 0.91 - (Math.max(0, Math.min(100, sensitivity)) * 0.0026);
            if (confidence >= 0.0 && confidence < minConfidence)
                return Decision.reject(String.format(Locale.US, "low confidence %.2f < %.2f", confidence, minConfidence), true);
            if (targetParts.length == 1 && stableHits < 2 && resultDurationMs <= 0)
                return Decision.reject("single-word final lacks stability", true);
            return accept(now, "final exact · hits=" + stableHits + confidenceSuffix(confidence));
        }

        int requiredHits = targetParts.length >= 2 ? 2 : 3;
        long minObserved = targetParts.length >= 2 ? 300L : 420L;
        if (stableHits < requiredHits || observedDuration < minObserved)
            return Decision.reject("partial exact · hits=" + stableHits + " · " + observedDuration + "ms", false);

        return accept(now, "stable partial exact · hits=" + stableHits + " · " + observedDuration + "ms");
    }

    private Decision accept(long now, String reason) {
        lastAcceptedMs = now;
        clearCandidate();
        return Decision.accept(reason);
    }

    private void clearCandidate() {
        candidateText = "";
        stableHits = 0;
        candidateSinceMs = 0L;
        lastCandidateMs = 0L;
    }

    private static boolean sharesWakeToken(String text, String[] targetParts) {
        String padded = " " + text + " ";
        for (String part : targetParts) {
            if (!part.isEmpty() && padded.contains(" " + part + " ")) return true;
        }
        return false;
    }

    private static String confidenceSuffix(double confidence) {
        return confidence < 0.0 ? "" : String.format(Locale.US, " · conf=%.2f", confidence);
    }

    private static String clean(String value) {
        if (value == null) return "";
        return value.trim().toLowerCase(Locale.ROOT).replaceAll("\\s+", " ");
    }
}
