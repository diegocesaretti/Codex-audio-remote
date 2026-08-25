package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;

import java.util.Locale;

final class VoskWakeGate {
    static final class Decision {
        final boolean accepted;
        final boolean loggable;
        final String reason;
        Decision(boolean accepted, boolean loggable, String reason) {
            this.accepted = accepted; this.loggable = loggable; this.reason = reason;
        }
        static Decision accept(String reason) { return new Decision(true, true, reason); }
        static Decision reject(String reason, boolean loggable) { return new Decision(false, loggable, reason); }
    }

    private String candidateText = "";
    private int stableHits;
    private int exactPartialHits;
    private long candidateSinceMs;
    private long lastCandidateMs;
    private long lastAcceptedMs;
    private boolean prefixSeen;
    private long prefixSeenMs;
    private double noiseFloor = 90.0;
    private int peakRms;
    private long peakRmsMs;

    synchronized void reset() { clearCandidate(); peakRms = 0; peakRmsMs = 0L; }
    synchronized void noteFinalSilence() { clearCandidate(); }

    synchronized void observeAudio(int rms, long nowMs) {
        if (rms < 0) return;
        Config c = Config.load(60);
        double upwardClamp = Math.max(noiseFloor + 120.0, noiseFloor * 2.5);
        double observed = Math.min(rms, upwardClamp);
        double alpha = observed < noiseFloor ? 0.06 : 0.012;
        noiseFloor = (noiseFloor * (1.0 - alpha)) + (observed * alpha);
        noiseFloor = Math.max(25.0, Math.min(3500.0, noiseFloor));
        if (rms >= peakRms || nowMs - peakRmsMs > c.audioWindowMs) {
            peakRms = rms; peakRmsMs = nowMs;
        }
    }

    synchronized Decision evaluate(String text, String target, int sensitivity, boolean isFinal,
                                   double confidence, long resultDurationMs, boolean requireAudioEvidence) {
        long now = System.currentTimeMillis();
        Config c = Config.load(sensitivity);
        text = clean(text); target = clean(target);
        if (text.isEmpty() || target.isEmpty())
            return report(Decision.reject("empty", false), now, text, target, isFinal, confidence, resultDurationMs);
        if (now - lastAcceptedMs < c.cooldownMs)
            return report(Decision.reject("cooldown", false), now, text, target, isFinal, confidence, resultDurationMs);
        if (lastCandidateMs > 0 && now - lastCandidateMs > c.candidateTimeoutMs) clearCandidate();

        String[] targetParts = target.split(" ");
        boolean multiWord = targetParts.length >= 2;
        boolean exact = text.equals(target);
        boolean validPrefix = target.startsWith(text + " ");

        if (!exact && !validPrefix) {
            boolean related = sharesWakeToken(text, targetParts);
            clearCandidate();
            return report(Decision.reject("lexical mismatch: " + text, related), now, text, target,
                    isFinal, confidence, resultDurationMs);
        }

        if (validPrefix) {
            if (!prefixSeen) { prefixSeen = true; prefixSeenMs = now; candidateSinceMs = now; }
            lastCandidateMs = now;
            if (!text.equals(candidateText)) { candidateText = text; stableHits = 1; } else stableHits++;
            return report(Decision.reject("prefix " + text + " · hits=" + stableHits, false), now, text,
                    target, isFinal, confidence, resultDurationMs);
        }

        if (!text.equals(candidateText)) {
            candidateText = text; stableHits = 1;
            if (candidateSinceMs == 0L) candidateSinceMs = now;
        } else stableHits++;
        lastCandidateMs = now;

        if (!isFinal) {
            exactPartialHits++;
            if (c.requireFinal)
                return report(Decision.reject("exact partial evidence · hits=" + exactPartialHits, false), now,
                        text, target, false, confidence, resultDurationMs);
        }

        if (multiWord && c.requirePrefix) {
            if (!prefixSeen) {
                clearCandidate();
                return report(Decision.reject((isFinal ? "final" : "partial") + " exact without prefix progression", true),
                        now, text, target, isFinal, confidence, resultDurationMs);
            }
            long age = now - prefixSeenMs;
            if (age < c.prefixMinMs || age > c.prefixMaxMs) {
                clearCandidate();
                return report(Decision.reject("implausible prefix timing " + age + "ms", true), now, text,
                        target, isFinal, confidence, resultDurationMs);
            }
        }

        int requiredPartials = c.advanced ? c.minExactPartials : (multiWord ? 0 : 3);
        if (exactPartialHits < requiredPartials) {
            if (isFinal) clearCandidate();
            return report(Decision.reject("exact partials " + exactPartialHits + " < " + requiredPartials, true),
                    now, text, target, isFinal, confidence, resultDurationMs);
        }

        long observedDuration = candidateSinceMs > 0 ? now - candidateSinceMs : -1L;
        long duration = resultDurationMs > 0 ? resultDurationMs : observedDuration;
        if (duration > 0 && duration < c.minDurationMs) {
            clearCandidate();
            return report(Decision.reject("too short " + duration + "ms < " + c.minDurationMs + "ms", true),
                    now, text, target, isFinal, confidence, duration);
        }
        if (duration > c.maxDurationMs) {
            clearCandidate();
            return report(Decision.reject("too long " + duration + "ms > " + c.maxDurationMs + "ms", true),
                    now, text, target, isFinal, confidence, duration);
        }

        if (requireAudioEvidence && c.audioEvidence) {
            long peakAge = peakRmsMs <= 0 ? Long.MAX_VALUE : now - peakRmsMs;
            int adaptive = c.adaptiveNoise ? (int)Math.round(noiseFloor * c.noiseFactor) : 0;
            int requiredPeak = Math.max(c.minRms, adaptive);
            if (peakAge > c.audioWindowMs || peakRms < requiredPeak) {
                clearCandidate();
                return report(Decision.reject("weak audio rms=" + peakRms + " need=" + requiredPeak
                        + " floor=" + (int)noiseFloor + " factor=" + String.format(Locale.US, "%.2f", c.noiseFactor), true),
                        now, text, target, isFinal, confidence, duration);
            }
        }

        if (confidence >= 0.0 && confidence < c.minConfidence) {
            clearCandidate();
            return report(Decision.reject(String.format(Locale.US, "low confidence %.2f < %.2f", confidence, c.minConfidence), true),
                    now, text, target, isFinal, confidence, duration);
        }

        Decision decision = accept(now, (isFinal ? "final" : "partial") + " exact"
                + (c.requirePrefix ? " + progression" : "")
                + " · partials=" + exactPartialHits + " · rms=" + peakRms
                + " · floor=" + (int)noiseFloor + confidenceSuffix(confidence)
                + (c.advanced ? " · advanced" : ""));
        return report(decision, now, text, target, isFinal, confidence, duration);
    }

    private Decision report(Decision decision, long now, String text, String target, boolean isFinal,
                            double confidence, long durationMs) {
        VoskDetectionBus.publish(new VoskDetectionBus.Event(now, text, target, isFinal, confidence,
                durationMs, peakRms, (int)Math.round(noiseFloor), decision.accepted, decision.reason));
        return decision;
    }

    private Decision accept(long now, String reason) { lastAcceptedMs = now; clearCandidate(); return Decision.accept(reason); }
    private void clearCandidate() {
        candidateText = ""; stableHits = 0; exactPartialHits = 0; candidateSinceMs = 0L;
        lastCandidateMs = 0L; prefixSeen = false; prefixSeenMs = 0L;
    }
    private static boolean sharesWakeToken(String text, String[] parts) {
        String padded = " " + text + " ";
        for (String part : parts) if (!part.isEmpty() && padded.contains(" " + part + " ")) return true;
        return false;
    }
    private static String confidenceSuffix(double confidence) {
        return confidence < 0.0 ? "" : String.format(Locale.US, " · conf=%.2f", confidence);
    }
    private static String clean(String value) {
        return value == null ? "" : value.trim().toLowerCase(Locale.ROOT).replaceAll("\\s+", " ");
    }

    private static final class Config {
        final boolean advanced, requireFinal, requirePrefix, audioEvidence, adaptiveNoise;
        final int minExactPartials, minRms;
        final long candidateTimeoutMs, audioWindowMs, cooldownMs, minDurationMs, maxDurationMs, prefixMinMs, prefixMaxMs;
        final double noiseFactor, minConfidence;

        Config(boolean advanced, boolean requireFinal, boolean requirePrefix, boolean audioEvidence,
               boolean adaptiveNoise, int minExactPartials, int minRms, long candidateTimeoutMs,
               long audioWindowMs, long cooldownMs, long minDurationMs, long maxDurationMs,
               long prefixMinMs, long prefixMaxMs, double noiseFactor, double minConfidence) {
            this.advanced = advanced; this.requireFinal = requireFinal; this.requirePrefix = requirePrefix;
            this.audioEvidence = audioEvidence; this.adaptiveNoise = adaptiveNoise;
            this.minExactPartials = minExactPartials; this.minRms = minRms;
            this.candidateTimeoutMs = candidateTimeoutMs; this.audioWindowMs = audioWindowMs;
            this.cooldownMs = cooldownMs; this.minDurationMs = minDurationMs; this.maxDurationMs = maxDurationMs;
            this.prefixMinMs = prefixMinMs; this.prefixMaxMs = prefixMaxMs;
            this.noiseFactor = noiseFactor; this.minConfidence = minConfidence;
        }

        static Config load(int sensitivity) {
            int s = ci(sensitivity, 0, 100);
            double defaultFactor = 1.95 + ((1.35 - 1.95) * (s / 100.0));
            double defaultConfidence = 0.90 - (s * 0.0018);
            Context context = AndroidDebugLog.context();
            if (context == null) return base(defaultFactor, defaultConfidence);
            SharedPreferences p = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
            if (!p.getBoolean("vosk_advanced", false)) return base(defaultFactor, defaultConfidence);

            long minDur = ci(p.getInt("vosk_min_duration_ms", 300), 0, 1500);
            long maxDur = ci(p.getInt("vosk_max_duration_ms", 1900), 500, 5000);
            if (maxDur < minDur) maxDur = minDur;
            long prefixMin = ci(p.getInt("vosk_prefix_min_ms", 120), 0, 1000);
            long prefixMax = ci(p.getInt("vosk_prefix_max_ms", 1800), 250, 4000);
            if (prefixMax < prefixMin) prefixMax = prefixMin;

            return new Config(true,
                    p.getBoolean("vosk_require_final", true), p.getBoolean("vosk_require_prefix", true),
                    p.getBoolean("vosk_audio_evidence", true), p.getBoolean("vosk_adaptive_noise", true),
                    ci(p.getInt("vosk_min_exact_partials", 0), 0, 5), ci(p.getInt("vosk_min_rms", 70), 0, 1200),
                    ci(p.getInt("vosk_candidate_timeout_ms", 2400), 300, 5000),
                    ci(p.getInt("vosk_audio_window_ms", 1800), 250, 4000),
                    ci(p.getInt("vosk_cooldown_ms", 3500), 0, 10000), minDur, maxDur, prefixMin, prefixMax,
                    ci(p.getInt("vosk_noise_factor_pct", 159), 100, 300) / 100.0,
                    ci(p.getInt("vosk_min_conf_pct", 79), 0, 100) / 100.0);
        }

        static Config base(double factor, double confidence) {
            return new Config(false, true, true, true, true, 0, 70,
                    2400, 1800, 3500, 300, 1900, 120, 1800, factor, confidence);
        }
        static int ci(int v, int min, int max) { return Math.max(min, Math.min(max, v)); }
    }
}
