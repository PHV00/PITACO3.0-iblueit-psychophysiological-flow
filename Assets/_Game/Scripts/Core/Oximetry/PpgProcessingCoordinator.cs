using System;
using Ibit.Core.Serial;
using UnityEngine;

namespace Ibit.Core.Oximetry
{
    /// <summary>
    /// Diagnostic results ONLY. SpO2DemoPercent is an uncalibrated demonstration
    /// and must not be connected to patient alerts, rewards, or medical decisions.
    /// </summary>
    public sealed class PpgMeasurement
    {
        public readonly uint DeviceTimeMs;
        public readonly double SamplingHz;
        public readonly double? BpmEstimate;
        public readonly double? SpO2DemoPercent;
        public readonly double? RatioOfRatios;
        public readonly double Autocorrelation;
        public readonly string Status;
        // Reserved for a future independently calibrated and validated SpO2
        // estimator. The current TryEstimateDemo() always leaves this false.
        public readonly bool SpO2CalibratedForLegacyRules;

        public PpgMeasurement(uint timeMs, double samplingHz, double? bpm,
            double? spo2Demo, double? ratio, double autocorrelation,
            string status, bool spO2CalibratedForLegacyRules = false)
        {
            DeviceTimeMs = timeMs;
            SamplingHz = samplingHz;
            BpmEstimate = bpm;
            SpO2DemoPercent = spo2Demo;
            RatioOfRatios = ratio;
            Autocorrelation = autocorrelation;
            Status = status;
            SpO2CalibratedForLegacyRules = spO2CalibratedForLegacyRules;
        }
    }

    /// <summary>
    /// Receives PITACO 2.4 optical JSON through the existing serial event.
    /// Bootstrapped persistently by PitacoConnectionManager (no extra COM).
    /// Optical contact and the latest fresh firmware G1 PASS are both required
    /// before publishing experimental estimates. G1 runs every 5 seconds;
    /// this coordinator never reimplements its signal-quality calculations.
    /// The placement monitor keeps receiving every optical-contact observation.
    /// </summary>
    public sealed class PpgProcessingCoordinator : MonoBehaviour
    {
        [Serializable]
        private sealed class PpgPayload
        {
            public int red;
            public int ir;
        }

        [Serializable]
        private sealed class SqiPayload
        {
            public bool contact;
            // Frame-specific G1 reason is useful for detecting firmware resets.
            // Do not deserialize sample.sqi.g1 here: it can be JSON null.
            public int reason;
        }

        // Only type="g1" frames contain a non-null boolean sqi.g1.
        // A separate type avoids treating sample.sqi.g1=null as false.
        [Serializable]
        private sealed class Gate01SqiPayload
        {
            public bool g1;
            public int reason;
            public int samples;
        }

        [Serializable]
        private sealed class Gate01StatsPayload
        {
            public int red_min, red_max, red_mean, red_range;
            public int ir_min, ir_max, ir_mean, ir_range;
            public int red_clips, ir_clips;
            public bool gap;
            public int limit_mean, limit_range;
        }

        [Serializable]
        private sealed class Gate01Frame
        {
            public string type;
            public long t_ms;
            public int window;
            public Gate01SqiPayload sqi;
            public Gate01StatsPayload stats; // optional: old firmware still works
        }

        private enum Gate01State
        {
            Waiting,
            Passed,
            Failed,
            Stale
        }

        [Serializable]
        private sealed class SampleFrame
        {
            public string type;
            public int seq;
            public long t_ms;
            public PpgPayload ppg;
            public SqiPayload sqi;
        }

        [SerializeField] private SerialControllerPitaco24 serialController;
        // Test branch only: event-driven PPG diagnostics. This replaces the
        // old verbose aggregate log; no Inspector override can re-enable it.
        private const float QualityHeartbeatSeconds = 10f;
        private const float QualityCalculationTraceSeconds = 2f;
        private string lastCalculationDecision;
        private float nextCalculationTraceAt;

        private readonly PpgSampleBuffer buffer = new PpgSampleBuffer();
        private bool hasPreviousProcessingTime;
        private uint previousProcessingMs;
        private float nextSearchTime;
        private float lastSampleReceivedAt = -1f;
        private float nextLogTime;
        private long receivedSampleFrames;
        private long jsonCallbacksReceived;
        private long nonSampleJsonFrames;
        private long invalidJsonFrames;
        private long missingContactFrames;
        private long invalidSampleFrames;
        private string lastJsonType = "nenhum";
        private const uint RecalculationPeriodMs = 400;
        // 5-second G1 cadence plus 1.5 s of delivery tolerance.
        // Experimental parameter; reevaluate with real hardware logs.
        private const uint Gate01MaxAgeMs = 6500;
        private const float Gate01MaxAgeSeconds = 6.5f;
        private const int Gate01MinimumSamples = 50;
        private const int ContactLossConfirmationSamples = 3;
        // HUD-only hold: protects against a 1-2-sample contact flicker, not
        // against G1 failure, stale telemetry or a rejected PPG calculation.
        // The Bridge/Mixer MUST continue reading LastMeasurement (not this).
        private const float HudShortContactHoldMaxAgeSeconds = 2f;
        private PpgMeasurement lastHudValidMeasurement;
        private float lastHudValidAt = -1f;
        private bool hudShortContactGapActive;
        private int consecutiveNoContactSamples;
        private int pendingShortContactGaps;
        private long toleratedShortContactGaps;
        private long confirmedContactLosses;

        private Gate01State gate01State = Gate01State.Waiting;
        private bool gate01Received;
        private uint lastGate01TimeMs;
        private float lastGate01ArrivalAt = -1f;
        private int lastGate01Reason = -1;
        private int lastGate01Samples;
        private long gate01Frames;
        private long rejectedGate01Frames;
        private long blockedByGate01;

        public string Gate01Status => gate01State.ToString();
        public int LastGate01Reason => lastGate01Reason;
        public int LastGate01Samples => lastGate01Samples;
        public bool Gate01Approved => gate01State == Gate01State.Passed &&
            gate01Received && lastGate01ArrivalAt >= 0f &&
            Time.unscaledTime - lastGate01ArrivalAt <= Gate01MaxAgeSeconds;

        public PpgMeasurement LastMeasurement { get; private set; }

        // Visually keep the last fresh PPG estimate through at most two
        // consecutive false-contact frames. No held value enters the Mixer.
        public bool HudHoldingShortContactGap =>
            hudShortContactGapActive && consecutiveNoContactSamples < ContactLossConfirmationSamples &&
            OpticalContactConfirmed && Gate01Approved &&
            lastHudValidMeasurement != null && lastHudValidAt >= 0f &&
            Time.unscaledTime - lastHudValidAt <= HudShortContactHoldMaxAgeSeconds;

        public PpgMeasurement HudMeasurement =>
            HudHoldingShortContactGap ? lastHudValidMeasurement : LastMeasurement;

        public bool OpticalContactConfirmed { get; private set; }
        // Distinguish JSON-looking frames, callbacks, recognized sample frames
        // and actual valid PPG. These counters do not change the signal path.
        public long JsonCallbacksReceived => jsonCallbacksReceived;
        public long ReceivedSampleFrames => receivedSampleFrames;
        public event Action<PpgMeasurement> OnMeasurement;
        // Independent of 8-second processing: one contact observation per
        // sample. A missing sqi object is NOT treated as proof of finger-off.
        public event Action<bool> OnOpticalContactSample;

        private void OnEnable()
        {
            ResetMeasurements("Aguardando amostras RED/IR");
            AttachIfAvailable();
        }

        private void Update()
        {
            if (serialController == null && Time.unscaledTime >= nextSearchTime)
            {
                nextSearchTime = Time.unscaledTime + 1f;
                AttachIfAvailable();
            }

            if (lastSampleReceivedAt >= 0f &&
                Time.unscaledTime - lastSampleReceivedAt > 3f)
            {
                ResetMeasurements("Sem amostras PPG recentes");
            }

            if (gate01State == Gate01State.Passed &&
                lastGate01ArrivalAt >= 0f &&
                Time.unscaledTime - lastGate01ArrivalAt > Gate01MaxAgeSeconds)
                MarkGate01Stale();

            if (Time.unscaledTime >= nextLogTime)
            {
                nextLogTime = Time.unscaledTime + QualityHeartbeatSeconds;
                Trace("SUMMARY", "json=" + jsonCallbacksReceived +
                    " | samples=" + receivedSampleFrames +
                    " | invalidJson=" + invalidJsonFrames +
                    " | invalidSample=" + invalidSampleFrames +
                    " | serialDropped=" +
                    (PitacoConnectionManager.Active != null
                        ? PitacoConnectionManager.Active.DroppedLines
                        : serialController != null ? serialController.DroppedMessages : 0) +
                    " | g1Frames=" + gate01Frames +
                    " | blockedByG1=" + blockedByGate01 +
                    " | contactFalse=" + missingContactFrames +
                    " | contactLossConfirmed=" + confirmedContactLosses +
                    " | gapsTolerated=" + toleratedShortContactGaps +
                    " | buffer=" + buffer.Count +
                    " | contact=" + OpticalContactConfirmed +
                    " | g1=" + gate01State);
            }
        }

        private void AttachIfAvailable()
        {
            // The persistent manager owns the real serial controller. Prefer
            // its colocated component to avoid subscribing to a scene duplicate.
            if (serialController == null)
                serialController = GetComponent<SerialControllerPitaco24>();
            if (serialController == null)
                serialController = FindObjectOfType<SerialControllerPitaco24>();
            if (serialController == null) return;

            serialController.OnJsonReceived -= ReceiveJson;
            serialController.OnSerialDisconnected -= OnDeviceDisconnected;
            serialController.OnJsonReceived += ReceiveJson;
            serialController.OnSerialDisconnected += OnDeviceDisconnected;
        }

        private void OnDisable()
        {
            if (serialController != null)
            {
                serialController.OnJsonReceived -= ReceiveJson;
                serialController.OnSerialDisconnected -= OnDeviceDisconnected;
            }
            ResetMeasurements("Processamento suspenso");
        }

        private void OnDeviceDisconnected()
        {
            ResetMeasurements("PITACO 2.4 desconectado");
        }

        private static void Trace(string eventName, string detail)
        {
            Debug.Log("[PPG-DIAG] t=" + Time.unscaledTime.ToString("F2") +
                " | event=" + eventName + " | " + detail);
        }

        // Every new outcome is logged immediately; repeated identical outcomes
        // are sampled every two seconds to preserve metrics without flooding.
        private void TraceCalculation(string decision, string detail)
        {
            if (decision == lastCalculationDecision &&
                Time.unscaledTime < nextCalculationTraceAt)
                return;
            lastCalculationDecision = decision;
            nextCalculationTraceAt =
                Time.unscaledTime + QualityCalculationTraceSeconds;
            Trace("CALC", detail);
        }

        private void ClearHudContactHold()
        {
            hudShortContactGapActive = false;
            lastHudValidMeasurement = null;
            lastHudValidAt = -1f;
        }

        private void InvalidatePpgWindow(string status)
        {
            if (buffer.Count > 0 ||
                (LastMeasurement != null &&
                 (LastMeasurement.BpmEstimate.HasValue ||
                  LastMeasurement.SpO2DemoPercent.HasValue)))
                Trace("WINDOW_RESET", "reason=" + status +
                    " | samplesDiscarded=" + buffer.Count +
                    " | contact=" + OpticalContactConfirmed +
                    " | g1=" + gate01State);
            ClearHudContactHold();
            buffer.Clear();
            hasPreviousProcessingTime = false;
            LastMeasurement = new PpgMeasurement(0, 0, null, null, null, 0, status);
        }

        private void ResetGate01()
        {
            gate01State = Gate01State.Waiting;
            gate01Received = false;
            lastGate01TimeMs = 0;
            lastGate01ArrivalAt = -1f;
            lastGate01Reason = -1;
            lastGate01Samples = 0;
        }

        private void ResetMeasurements(string status)
        {
            InvalidatePpgWindow(status);
            OpticalContactConfirmed = false;
            consecutiveNoContactSamples = 0;
            pendingShortContactGaps = 0;
            lastSampleReceivedAt = -1f;
            ResetGate01();
        }

        private void MarkGate01Stale()
        {
            if (gate01State != Gate01State.Passed) return;
            ClearHudContactHold();
            gate01State = Gate01State.Stale;
            Trace("G1_EXPIRED", "ageS=" +
                (Time.unscaledTime - lastGate01ArrivalAt).ToString("F2") +
                " | bufferRetained=" + buffer.Count);
            // Suspend publication but retain collected samples until next G1
            // decision; no stale measurement may remain visible in the HUD.
            hasPreviousProcessingTime = false;
            LastMeasurement = new PpgMeasurement(0, 0, null, null, null, 0,
                "G1 expirado: aguardando nova avaliacao");
        }

        private void ReceiveGate01(string json)
        {
            Gate01Frame result;
            try { result = JsonUtility.FromJson<Gate01Frame>(json); }
            catch (ArgumentException)
            {
                rejectedGate01Frames++;
                return;
            }
            if (result == null || result.sqi == null || result.t_ms < 0 ||
                result.t_ms > uint.MaxValue)
            {
                rejectedGate01Frames++;
                return;
            }

            uint deviceMs = (uint)result.t_ms;
            // Compare modulo 2^32 to handle millis() wrap-around without
            // accepting replayed/out-of-order results from this device session.
            if (gate01Received &&
                unchecked((int)(deviceMs - lastGate01TimeMs)) <= 0)
            {
                rejectedGate01Frames++;
                return;
            }

            gate01Received = true;
            lastGate01TimeMs = deviceMs;
            lastGate01ArrivalAt = Time.unscaledTime;
            lastGate01Reason = result.sqi.reason;
            lastGate01Samples = result.sqi.samples;
            gate01Frames++;

            string stats = result.stats == null
                ? "metrics=UNAVAILABLE_OLD_FIRMWARE"
                : "redMean=" + result.stats.red_mean +
                  " | redRange=" + result.stats.red_range +
                  " | irMean=" + result.stats.ir_mean +
                  " | irRange=" + result.stats.ir_range +
                  " | redMin=" + result.stats.red_min +
                  " | redMax=" + result.stats.red_max +
                  " | irMin=" + result.stats.ir_min +
                  " | irMax=" + result.stats.ir_max +
                  " | redClips=" + result.stats.red_clips +
                  " | irClips=" + result.stats.ir_clips +
                  " | acquisitionGap=" + result.stats.gap +
                  " | minMean=" + result.stats.limit_mean +
                  " | minRange=" + result.stats.limit_range;

            bool approved = result.sqi.g1 && result.sqi.reason == 0 &&
                result.sqi.samples >= Gate01MinimumSamples;
            gate01State = approved ? Gate01State.Passed : Gate01State.Failed;
            Trace(approved ? "G1_PASS" : "G1_REJECT",
                "window=" + result.window + " | devMs=" + deviceMs +
                " | samples=" + lastGate01Samples +
                " | reason=" + lastGate01Reason +
                " | bufferBefore=" + buffer.Count + " | " + stats);
            if (!approved)
            {
                // A failed 5-second period must never leave the previous
                // 8-second result on the player's HUD.
                InvalidatePpgWindow("G1 reprovado: reason=" + lastGate01Reason);
            }
        }

        private bool IsGate01ApprovedForSample(uint sampleTimeMs)
        {
            if (gate01State != Gate01State.Passed || !gate01Received)
                return false;
            if (lastGate01ArrivalAt < 0f ||
                Time.unscaledTime - lastGate01ArrivalAt > Gate01MaxAgeSeconds ||
                unchecked(sampleTimeMs - lastGate01TimeMs) > Gate01MaxAgeMs)
            {
                MarkGate01Stale();
                return false;
            }
            return true;
        }

        private void ReceiveJson(string json)
        {
            // Handle firmware G1 window events and optical sample frames
            // independently; no second USB stream or software G1 calculation.
            if (string.IsNullOrEmpty(json)) return;
            jsonCallbacksReceived++;

            SampleFrame frame;
            try { frame = JsonUtility.FromJson<SampleFrame>(json); }
            catch (ArgumentException)
            {
                invalidJsonFrames++;
                lastJsonType = "JSON-malformado";
                return;
            }

            if (frame == null)
            {
                invalidJsonFrames++;
                lastJsonType = "nulo";
                return;
            }

            lastJsonType = string.IsNullOrEmpty(frame.type) ? "sem-type" : frame.type;
            if (frame.type == "g1")
            {
                ReceiveGate01(json);
                return;
            }
            if (frame.type != "sample" || frame.ppg == null)
            {
                nonSampleJsonFrames++;
                return;
            }

            receivedSampleFrames++;

            // Contact is a sample-level observation, independent of G1.
            // Missing SQI is a protocol error, not evidence of finger-off.
            if (frame.sqi == null)
            {
                invalidSampleFrames++;
                ResetMeasurements("Amostra sem SQI de contato");
                return;
            }

            bool hasContact = frame.sqi.contact;
            // Keep the finger-placement monitor's existing timing unchanged.
            OnOpticalContactSample?.Invoke(hasContact);
            if (!hasContact)
            {
                missingContactFrames++;
                if (OpticalContactConfirmed && consecutiveNoContactSamples == 0)
                    Trace("CONTACT_GLITCH", "seq=" + frame.seq +
                        " | devMs=" + frame.t_ms +
                        " | reason=" + frame.sqi.reason +
                        " | bufferRetained=" + buffer.Count);
                if (consecutiveNoContactSamples < ContactLossConfirmationSamples)
                    ++consecutiveNoContactSamples;

                if (consecutiveNoContactSamples >= ContactLossConfirmationSamples)
                {
                    // Confirmed removal: clear buffer and revoke previous G1.
                    // Further false frames must not repeatedly reset the state.
                    if (OpticalContactConfirmed)
                    {
                        ++confirmedContactLosses;
                        Trace("CONTACT_LOST", "seq=" + frame.seq +
                            " | devMs=" + frame.t_ms +
                            " | consecutive=" + consecutiveNoContactSamples +
                            " | confirmedLosses=" + confirmedContactLosses);
                        ResetMeasurements("Sem contato optico");
                        consecutiveNoContactSamples = ContactLossConfirmationSamples;
                    }
                    else
                        LastMeasurement = new PpgMeasurement(0, 0, null, null,
                            null, 0, "Sem contato optico");
                    return;
                }

                // Quarantine 1-2 short failures, preserving the previous good
                // samples but never adding suspicious RED/IR to the buffer.
                if (OpticalContactConfirmed)
                {
                    // Keep the last genuinely published estimate for DISPLAY
                    // only. A fresh G1, contact and a 2-second age limit are
                    // still required; the normal measurement is invalidated.
                    if (consecutiveNoContactSamples == 1)
                    {
                        hudShortContactGapActive = lastHudValidMeasurement != null &&
                            object.ReferenceEquals(LastMeasurement, lastHudValidMeasurement);
                        Trace("HUD_HOLD_START",
                            "eligible=" + hudShortContactGapActive +
                            " | maxAgeS=" + HudShortContactHoldMaxAgeSeconds +
                            " | visualOnly=true | mixerUsesLiveMeasurement=true");
                    }
                    ++pendingShortContactGaps;
                    LastMeasurement = new PpgMeasurement(0, 0, null, null,
                        null, 0, "Contato optico instavel (" +
                        consecutiveNoContactSamples + "/3)");
                }
                return;
            }

            // Only confirmed existing contact can bridge 1-2 skipped frames.
            int toleratedGaps = OpticalContactConfirmed ? pendingShortContactGaps : 0;
            if (!OpticalContactConfirmed || toleratedGaps > 0)
                Trace("CONTACT_PRESENT", "seq=" + frame.seq +
                    " | devMs=" + frame.t_ms +
                    " | recoveredGaps=" + toleratedGaps +
                    " | previousConfirmed=" + OpticalContactConfirmed);
            consecutiveNoContactSamples = 0;
            pendingShortContactGaps = 0;
            OpticalContactConfirmed = true;

            // Firmware resets the current G1 evaluation to reason=1 on a
            // new sampling session. Revoke an older PASS even if USB stayed
            // connected. Never treat the null per-sample g1 as a boolean.
            if (gate01State == Gate01State.Passed &&
                frame.sqi != null && frame.sqi.reason != 0)
            {
                Trace("G1_REVOKED", "seq=" + frame.seq +
                    " | sampleReason=" + frame.sqi.reason);
                ResetGate01();
                InvalidatePpgWindow("G1 reiniciado/invalidado pelo firmware");
            }

            PpgSample sample;
            if (!PpgSample.TryCreate(frame.seq, frame.t_ms,
                frame.ppg.red, frame.ppg.ir, out sample))
            {
                ClearHudContactHold();
                invalidSampleFrames++;
                Trace("SAMPLE_INVALID", "seq=" + frame.seq +
                    " | devMs=" + frame.t_ms +
                    " | red=" + frame.ppg.red + " | ir=" + frame.ppg.ir);
                LastMeasurement = new PpgMeasurement(0, 0, null, null,
                    null, 0, "Amostra PPG invalida: seq/t_ms/RED/IR");
                return;
            }

            if (toleratedGaps > 0)
            {
                sample = new PpgSample(sample.Sequence, sample.TimeMs,
                    sample.Red, sample.Ir, (byte)toleratedGaps);
                toleratedShortContactGaps += toleratedGaps;
            }

            bool continuous = buffer.Add(sample);
            lastSampleReceivedAt = Time.unscaledTime;
            if (!continuous)
            {
                Trace("SEQUENCE_RESET", "seq=" + sample.Sequence +
                    " | devMs=" + sample.TimeMs +
                    " | bufferNow=" + buffer.Count);
                ClearHudContactHold();
                hasPreviousProcessingTime = false;
                LastMeasurement = new PpgMeasurement(sample.TimeMs, 0,
                    null, null, null, 0, "Janela reiniciada: sequencia/timestamp");
            }

            if (!IsGate01ApprovedForSample(sample.TimeMs))
            {
                ClearHudContactHold();
                blockedByGate01++;
                string gateBlockReason = gate01State == Gate01State.Waiting
                    ? "Aguardando aprovacao G1"
                    : gate01State == Gate01State.Failed
                        ? "G1 reprovado: reason=" + lastGate01Reason
                        : "G1 expirado: aguardando nova avaliacao";
                LastMeasurement = new PpgMeasurement(sample.TimeMs, 0,
                    null, null, null, 0, gateBlockReason);
                TraceCalculation("G1_" + gate01State, "stage=G1_BLOCK" +
                    " | reason=" + gateBlockReason +
                    " | seq=" + sample.Sequence +
                    " | devMs=" + sample.TimeMs +
                    " | buffer=" + buffer.Count);
                return;
            }

            if (hasPreviousProcessingTime &&
                unchecked(sample.TimeMs - previousProcessingMs) < RecalculationPeriodMs)
                return;

            PpgProcessedWindow processed;
            string reason;
            if (!PpgSignalProcessor.TryProcess(buffer.Samples, out processed, out reason))
            {
                ClearHudContactHold();
                LastMeasurement = new PpgMeasurement(sample.TimeMs, 0,
                    null, null, null, 0, reason);
                TraceCalculation("WINDOW_" + reason,
                    "stage=WINDOW_REJECT | reason=" + reason +
                    " | seq=" + sample.Sequence +
                    " | devMs=" + sample.TimeMs +
                    " | buffer=" + buffer.Count);
                return;
            }

            previousProcessingMs = sample.TimeMs;
            hasPreviousProcessingTime = true;
            double bpm, autocorrelation, spo2Demo, ratio, selectedLag;
            string bpmReason, spo2Reason;
            bool hasBpm = BpmEstimator.TryEstimateDetailed(processed, out bpm,
                out autocorrelation, out selectedLag, out bpmReason);
            bool hasSpO2 = Spo2Estimator.TryEstimateDemoDetailed(processed,
                out spo2Demo, out ratio, out spo2Reason);
            TraceCalculation("BPM_" + bpmReason + "_SPO2_" + spo2Reason,
                "stage=ESTIMATORS | seq=" + sample.Sequence +
                " | devMs=" + sample.TimeMs +
                " | buffer=" + buffer.Count +
                " | fs=" + processed.SamplingHz.ToString("F2") +
                " | reconstructed=" + processed.ReconstructedSamples +
                " | redDC=" + processed.RedDc.ToString("F1") +
                " | irDC=" + processed.IrDc.ToString("F1") +
                " | redACrms=" + processed.RedAcRms.ToString("F2") +
                " | irACrms=" + processed.IrAcRms.ToString("F2") +
                " | bpmReason=" + bpmReason +
                " | autocorr=" + autocorrelation.ToString("F3") +
                " | lag=" + selectedLag.ToString("F2") +
                " | bpm=" + (hasBpm ? bpm.ToString("F1") : "--") +
                " | spo2Reason=" + spo2Reason +
                " | ratioR=" + ratio.ToString("F4") +
                " | spo2Demo=" + (hasSpO2 ? spo2Demo.ToString("F1") : "--"));
            LastMeasurement = new PpgMeasurement(sample.TimeMs, processed.SamplingHz,
                hasBpm ? (double?)bpm : null,
                hasSpO2 ? (double?)spo2Demo : null,
                hasSpO2 ? (double?)ratio : null,
                autocorrelation, processed.ReconstructedSamples > 0
                    ? "EXPERIMENTAL - BPM com lacunas reconstruidas; SpO2 bloqueada"
                    : "EXPERIMENTAL - NAO CALIBRADO");
            // Release a display hold once the processor reaches a new verdict.
            // A rejected SpO2 remains "--"; we never invent a fresh reading.
            ClearHudContactHold();
            if (LastMeasurement.BpmEstimate.HasValue ||
                LastMeasurement.SpO2DemoPercent.HasValue)
            {
                lastHudValidMeasurement = LastMeasurement;
                lastHudValidAt = Time.unscaledTime;
            }
            OnMeasurement?.Invoke(LastMeasurement);


        }
    }
}
