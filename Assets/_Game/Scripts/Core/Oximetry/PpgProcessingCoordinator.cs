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
        private sealed class Gate01Frame
        {
            public string type;
            public long t_ms;
            public int window;
            public Gate01SqiPayload sqi;
        }

        private enum Gate01State
        {
            Waiting,
            Passed,
            Failed,
            Stale
        }

        // A single decision for each optically accepted sample. The G1
        // firmware verdict remains authoritative: this is not a second SQI.
        private enum PpgAction
        {
            Allow,   // Fresh G1 PASS; estimation may run.
            Suspend, // Collect candidate samples; never publish until PASS.
            Reset    // Firmware indicated a new G1 acquisition/session.
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
        // Diagnostics only, never feeds any gate, measurement or game decision.
        private readonly PpgRawG1Probe rawG1Probe = new PpgRawG1Probe();
        public PpgRawG1Summary LastG1ShadowSummary { get; private set; }
        private bool hasPreviousProcessingTime;
        private uint previousProcessingMs;
        private float nextSearchTime;
        // Receive-time heartbeat of ALL optical JSON sample frames, including
        // those without contact. Never infer telemetry loss from finger-off.
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
        // Device t_ms, not Unity frame time, decides G1 age per sample.
        private const float TelemetryFreshnessSeconds = 3f;
        private const double SerialQueueDiagnosticThresholdMs = 250.0;
        private const double SerialQueuePublicationLimitMs = 2000.0;
        private double lastJsonQueueAgeMs;
        private double maxJsonQueueAgeMs;
        private long jsonFramesOverQueueAgeLimit;
        private bool serialQueueDelayed;
        private uint lastObservedSampleDeviceMs;
        private bool hasObservedSampleDeviceMs;
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
        public bool Gate01Approved
        {
            get
            {
                if (gate01State != Gate01State.Passed || !gate01Received ||
                    lastSampleReceivedAt < 0f ||
                    Time.unscaledTime - lastSampleReceivedAt > TelemetryFreshnessSeconds ||
                    lastJsonQueueAgeMs > SerialQueuePublicationLimitMs)
                    return false;

                // A sample may precede the G1 verdict by a few milliseconds.
                if (hasObservedSampleDeviceMs)
                {
                    int deviceAge = unchecked((int)(lastObservedSampleDeviceMs -
                        lastGate01TimeMs));
                    if (deviceAge > (int)Gate01MaxAgeMs)
                        return false;
                }
                return true;
            }
        }

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
            rawG1Probe.Clear();
            LastG1ShadowSummary = null;
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
                Time.unscaledTime - lastSampleReceivedAt > TelemetryFreshnessSeconds)
            {
                // ResetMeasurements sets the heartbeat to -1, so this event
                // fires only once until optical frames resume.
                Trace("TELEMETRY_LOST",
                    "ageS=" + (Time.unscaledTime - lastSampleReceivedAt).ToString("F2") +
                    " | lastJsonType=" + lastJsonType +
                    " | contactBefore=" + OpticalContactConfirmed);
                ResetMeasurements("Sem amostras PPG recentes");
            }

            // No Unity-wall-clock G1 expiry: sample device timestamps
            // determine G1 validity. The 3s telemetry watchdog above
            // independently handles genuinely missing samples.

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
                    " | queuePending=" + (PitacoConnectionManager.Active != null
                        ? PitacoConnectionManager.Active.PendingLines : 0) +
                    " | queueAgeMs=" + lastJsonQueueAgeMs.ToString("F1") +
                    " | queuePeakMs=" + maxJsonQueueAgeMs.ToString("F1") +
                    " | queuedOver2s=" + jsonFramesOverQueueAgeLimit +
                    " | linesLastFrame=" + (PitacoConnectionManager.Active != null
                        ? PitacoConnectionManager.Active.LastFrameLineCount : 0) +
                    " | linesPeakFrame=" + (PitacoConnectionManager.Active != null
                        ? PitacoConnectionManager.Active.PeakFrameLineCount : 0) +
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
            rawG1Probe.Clear();
            LastG1ShadowSummary = null;
            if (serialController != null)
            {
                serialController.OnJsonReceived -= ReceiveJson;
                serialController.OnSerialDisconnected -= OnDeviceDisconnected;
            }
            ResetMeasurements("Processamento suspenso");
        }

        private void OnDeviceDisconnected()
        {
            rawG1Probe.Clear();
            LastG1ShadowSummary = null;
            ResetMeasurements("PITACO 2.4 desconectado");
        }

        private static void Trace(string eventName, string detail)
        {
            Debug.Log("[PPG-DIAG] t=" + Time.unscaledTime.ToString("F2") +
                " | event=" + eventName + " | " + detail);
        }

        // Every new outcome is logged immediately; repeated identical outcomes
        // are sampled every two seconds to preserve metrics without flooding.
        private bool ShouldTraceCalculation(string decision)
        {
            return decision != lastCalculationDecision ||
                Time.unscaledTime >= nextCalculationTraceAt;
        }

        private void TraceCalculation(string decision, string detail,
            bool everyCall = false)
        {
            if (!everyCall && decision == lastCalculationDecision &&
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
            hasObservedSampleDeviceMs = false;
            lastJsonQueueAgeMs = 0.0;
            serialQueueDelayed = false;
            ResetGate01();
        }

        private void MarkGate01Stale()
        {
            if (gate01State != Gate01State.Passed) return;
            ClearHudContactHold();
            gate01State = Gate01State.Stale;
            Trace("G1_EXPIRED", "clock=DEVICE | lastGateDevMs=" +
                lastGate01TimeMs + " | lastSampleDevMs=" +
                lastObservedSampleDeviceMs + " | ageDeviceMs=" +
                unchecked(lastObservedSampleDeviceMs - lastGate01TimeMs) +
                " | queueAgeMs=" + lastJsonQueueAgeMs.ToString("F1") +
                " | bufferBefore=" + buffer.Count);
            // The RAW diagnostic probe is preserved; the processing window
            // cannot mix an interval authorized by this expired PASS with
            // later candidate samples. One reset per transition to Stale.
            InvalidatePpgWindow("G1 expirado: aguardando nova avaliacao");
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

            // Firmware sends only sqi.g1/reason/samples. Compute local,
            // non-authoritative statistics from RAW frames Unity received.
            LastG1ShadowSummary = rawG1Probe.Summarize(deviceMs,
                lastGate01Samples);
            string stats = LastG1ShadowSummary.ToDiagnosticString();

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

        private PpgAction DecideG1Action(uint sampleTimeMs, int sampleReason)
        {
            // After firmware contact reacquisition, sample.sqi.reason=1
            // means its G1 accumulator was restarted. The old PASS is no
            // longer valid even if the optical contact is currently true.
            if (gate01State == Gate01State.Passed && sampleReason != 0)
                return PpgAction.Reset;
            return IsGate01ApprovedForSample(sampleTimeMs)
                ? PpgAction.Allow : PpgAction.Suspend;
        }

        private bool IsGate01ApprovedForSample(uint sampleTimeMs)
        {
            if (gate01State != Gate01State.Passed || !gate01Received)
                return false;
            int ageDeviceMs = unchecked((int)(sampleTimeMs - lastGate01TimeMs));
            if (ageDeviceMs < 0)
                return false; // Sample predates this G1 verdict; do not revoke.
            if (ageDeviceMs > (int)Gate01MaxAgeMs)
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
            // This is receipt on Unity's main thread, not acquisition time.
            // Update before evaluating contact, validity or G1; contact=false
            // still proves the JSON stream is alive.
            lastSampleReceivedAt = Time.unscaledTime;

            // Capture EVERY delivered RED/IR pair before contact/G1 filtering.
            // This history is independent of the 8s processing buffer, so a
            // rejected G1 can still be diagnosed without firmware changes.
            rawG1Probe.Observe(frame.seq, frame.t_ms, frame.ppg.red,
                frame.ppg.ir, frame.sqi != null && frame.sqi.contact);

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

            // Decide BEFORE adding to the candidate buffer. A G1 reset or
            // expiry invalidates the preceding segment only once. Waiting and
            // Failed states may accumulate candidate samples after the most
            // recent invalidation, but they never authorize estimates.
            PpgAction action = DecideG1Action(sample.TimeMs, frame.sqi.reason);
            if (action == PpgAction.Reset)
            {
                Trace("G1_REVOKED", "seq=" + frame.seq +
                    " | devMs=" + frame.t_ms +
                    " | sampleReason=" + frame.sqi.reason);
                ResetGate01();
                InvalidatePpgWindow("G1 reiniciado/invalidado pelo firmware");
                action = PpgAction.Suspend;
            }

            bool continuous = buffer.Add(sample);
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

            if (action != PpgAction.Allow)
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
                string decision = "G1_" + gate01State;
                // Avoid formatting repetitive detailed diagnostic strings at
                // 25 Hz: log only on state change or the 2-second heartbeat.
                if (ShouldTraceCalculation(decision))
                    TraceCalculation(decision, "stage=G1_BLOCK" +
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
                string decision = "WINDOW_" + reason;
                if (ShouldTraceCalculation(decision))
                    TraceCalculation(decision,
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
                " | spo2Demo=" + (hasSpO2 ? spo2Demo.ToString("F1") : "--"),
                true); // Audit every actual 400-ms estimator calculation.
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
