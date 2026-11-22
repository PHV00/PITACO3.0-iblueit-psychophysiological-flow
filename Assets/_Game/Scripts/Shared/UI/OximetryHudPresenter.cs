using System.Globalization;
using Ibit.Core.Oximetry;
using Ibit.Core.Oximetry.Placement;
using Ibit.Core.Serial;
using UnityEngine;
using UnityEngine.UI;

namespace Ibit.Core.Oximetry.UI
{
    /// <summary>
    /// Shared text presenter for the existing StatusDevices/measures UI.
    /// Reads measurement/placement state, but never changes the G1 policy,
    /// PPG buffer, serial connection or the finger-placement pause.
    /// Scene-specific OximeterMeasures scripts adapt their legacy Player only.
    /// </summary>
    [RequireComponent(typeof(Text))]
    public abstract class OximetryHudPresenter : MonoBehaviour
    {
        private const string ConnectedPrefix = "PITACO 2.4: conectado\n";
        private Text hud;
        private PitacoConnectionManager cachedManager;
        private PpgProcessingCoordinator coordinator;
        private SerialControllerPitaco24 serial;
        private FingerPlacementMonitor placement;
        private string lastRenderState;
        private float nextRenderDiagnosticAt;

        // Only legacy oximeter uses the per-scene Player text. PITACO 2.4 HUD
        // reads coordinator measurements directly, avoiding a second 1s cache.
        // Mixer continues to use the separate source-aware Bridge.
        protected abstract string ReadPlayerText(bool pitaco24Connected);

        protected virtual void Update()
        {
            if (hud == null)
                hud = GetComponent<Text>();
            if (hud == null)
                return;

            var manager = PitacoConnectionManager.Active;
            bool isPitaco24 = manager != null &&
                manager.ConnectedVersion == PitacoConnectionManager.Version.Pitaco24;

            if (!isPitaco24)
            {
                // Preserve the independent legacy oximeter's existing HUD.
                SetText(ReadPlayerText(false) ?? string.Empty, "LEGACY");
                return;
            }

            if (manager != cachedManager || coordinator == null)
            {
                cachedManager = manager;
                coordinator = manager.GetComponent<PpgProcessingCoordinator>();
                serial = manager.GetComponent<SerialControllerPitaco24>();
                placement = manager.GetComponent<FingerPlacementMonitor>();
            }

            // The existing scene Text fits only about three lines. Keep the
            // status and both measurements visible in every state, rather than
            // appending diagnostics after a long table outside the text bounds.
            if (coordinator == null)
            {
                SetText(FormatPitaco24Compact("sem processamento", null, null),
                    "NO_COORDINATOR");
                return;
            }

            if (coordinator.JsonCallbacksReceived == 0)
            {
                string label = serial != null && serial.JsonMessagesReceived > 0
                    ? "JSON nao encaminhado" : "aguardando JSON";
                SetText(FormatPitaco24Compact(label, null, null), "WAIT_JSON");
                return;
            }

            if (coordinator.ReceivedSampleFrames == 0)
            {
                SetText(FormatPitaco24Compact("aguardando RED/IR", null, null),
                    "WAIT_SAMPLES");
                return;
            }

            if (!coordinator.OpticalContactConfirmed)
            {
                FingerPlacementState state = placement != null &&
                    placement.SessionActive
                        ? placement.State : FingerPlacementState.WaitingForData;
                string label;
                switch (state)
                {
                    case FingerPlacementState.Warning:
                    case FingerPlacementState.Critical:
                    case FingerPlacementState.PauseRequired:
                        label = "reposicione dedo";
                        break;
                    case FingerPlacementState.Recovering:
                        label = "estabilizando";
                        break;
                    case FingerPlacementState.TelemetryLost:
                        label = "sem telemetria";
                        break;
                    case FingerPlacementState.DeviceDisconnected:
                        label = "sem conexao";
                        break;
                    default:
                        label = "sem contato";
                        break;
                }
                SetText(FormatPitaco24Compact(label, null, null), "NO_CONTACT");
                return;
            }

            if (!coordinator.Gate01Approved)
            {
                string label = coordinator.Gate01Status == "Failed"
                    ? "G1 reprovado" : "aguardando G1";
                SetText(FormatPitaco24Compact(label, null, null), "G1_BLOCK");
                return;
            }

            if (coordinator.HudHoldingShortContactGap)
            {
                // Held values are marked *, never presented as current.
                // The Mixer uses LastMeasurement, not HudMeasurement.
                var held = coordinator.HudMeasurement;
                SetText(FormatPitaco24Compact("contato instavel",
                    held.BpmEstimate, held.SpO2DemoPercent, true, true),
                    "HOLD_VISUAL_ONLY");
                return;
            }

            if (coordinator.HudHoldingRejectedBpm)
            {
                var latest = coordinator.HudMeasurement;
                SetText(FormatPitaco24Compact("BPM reavaliando",
                    coordinator.HudRetainedBpmVisualOnly,
                    latest != null ? latest.SpO2DemoPercent : null,
                    true, false), "BPM_HOLD_VISUAL_ONLY");
                return;
            }

            if (!coordinator.HudHasRecentLiveMeasurement)
            {
                SetText(FormatPitaco24Compact("aguardando leitura", null, null),
                    "WAIT_ESTIMATE");
                return;
            }

            var live = coordinator.HudMeasurement;
            SetText(FormatPitaco24Compact("conectado",
                live.BpmEstimate, live.SpO2DemoPercent), "LIVE");
        }

        private void SetText(string value, string state)
        {
            if (hud.text != value)
                hud.text = value;
            // Visual proof, distinct from Bridge/Mixer consumption. Transitions
            // are logged immediately; unchanged state is sampled every 10 s.
            if (lastRenderState != state ||
                Time.unscaledTime >= nextRenderDiagnosticAt)
            {
                lastRenderState = state;
                nextRenderDiagnosticAt = Time.unscaledTime + 10f;
                var measurement = coordinator != null ? coordinator.HudMeasurement : null;
                Debug.Log("[PPG-HUD] t=" + Time.unscaledTime.ToString("F2") +
                    " | scene=" + gameObject.scene.name +
                    " | object=" + gameObject.name +
                    " | frame=" + Time.frameCount +
                    " | state=" + state +
                    " | contact=" + (coordinator != null && coordinator.OpticalContactConfirmed) +
                    " | g1=" + (coordinator != null ? coordinator.Gate01Status : "none") +
                    " | bpmRejectStreak=" +
                    (coordinator != null ? coordinator.ConsecutiveBpmRejections : 0) +
                    " | bpmThreshold=" +
                    (coordinator != null
                        ? coordinator.ActiveBpmAutocorrelationThreshold.ToString("F2")
                        : "--") +
                    " | bpmHeldVisualOnly=" +
                    (coordinator != null && coordinator.HudHoldingRejectedBpm) +
                    " | bpmHeldValue=" +
                    (coordinator != null && coordinator.HudRetainedBpmVisualOnly.HasValue
                        ? coordinator.HudRetainedBpmVisualOnly.Value.ToString("F1") : "--") +
                    " | candidateBpm=" +
                    (measurement != null && measurement.BpmEstimate.HasValue
                        ? measurement.BpmEstimate.Value.ToString("F1") : "--") +
                    " | candidateSpO2Demo=" +
                    (measurement != null && measurement.SpO2DemoPercent.HasValue
                        ? measurement.SpO2DemoPercent.Value.ToString("F1") : "--") +
                    " | rendered=" + (value ?? string.Empty).Replace("\n", " / "));
            }
        }

        public static string FormatNoContactStatus(FingerPlacementState state)
        {
            switch (state)
            {
                case FingerPlacementState.Warning:
                case FingerPlacementState.Critical:
                case FingerPlacementState.PauseRequired:
                    return ConnectedPrefix + "PPG: reposicione o dedo";
                case FingerPlacementState.Recovering:
                    return ConnectedPrefix + "PPG: estabilizando contato";
                case FingerPlacementState.TelemetryLost:
                    return ConnectedPrefix + "PPG: telemetria ausente";
                case FingerPlacementState.DeviceDisconnected:
                    return ConnectedPrefix + "PPG: aguardando reconexao";
                default:
                    return ConnectedPrefix + "PPG: sem contato optico";
            }
        }

        // Exactly three visible lines in the existing StatusDevices Text:
        // connection/state, BPM (current | approved rolling 60s mean), and
        // SpO2 DEMO (current | approved rolling 60s mean).
        // The means are historical and never substitute a missing live reading.
        private string FormatPitaco24Compact(string state, double? bpm,
            double? spo2Demo, bool heldBpm = false, bool heldSpo2 = false)
        {
            double? bpmMean = null, spo2Mean = null;
            int bpmCount = 0, spo2Count = 0;
            if (coordinator != null)
                coordinator.GetHudTrend60Seconds(
                    out bpmMean, out bpmCount, out spo2Mean, out spo2Count);

            string currentBpm = FormatHudValue(bpm) +
                (heldBpm && bpm.HasValue ? "*" : "");
            string currentSpo2 = FormatHudValue(spo2Demo) +
                (heldSpo2 && spo2Demo.HasValue ? "*" : "");

            return "PITACO 2.4: " + state + "\\n" +
                "BPM atual: " + currentBpm + " | 60s: " +
                FormatHudValue(bpmMean) + "\\n" +
                "O2 DEMO: " + currentSpo2 + "% | 60s: " +
                FormatHudValue(spo2Mean) + "%";
        }

        private static string FormatHudValue(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "--";
        }

        public static string FormatPitaco24Values(double? bpm, double? spo2Demo)
        {
            string hr = bpm.HasValue
                ? bpm.Value.ToString("F1", CultureInfo.InvariantCulture) : "--";
            string spo2 = spo2Demo.HasValue
                ? spo2Demo.Value.ToString("F1", CultureInfo.InvariantCulture) : "--";
            return $"HR [2.4]: {hr} bpm\nSpO2 DEMO: {spo2} %";
        }
    }
}
