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
                // The old independent oximeter still owns its original HUD path.
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

            if (coordinator == null)
            {
                SetText(ConnectedPrefix + "PPG: processamento ausente", "NO_COORDINATOR");
                return;
            }

            if (coordinator.JsonCallbacksReceived == 0)
            {
                SetText(ConnectedPrefix + (serial != null && serial.JsonMessagesReceived > 0
                    ? "PPG: JSON nao encaminhado"
                    : "PPG: aguardando JSON"), "WAIT_JSON");
                return;
            }

            if (coordinator.ReceivedSampleFrames == 0)
            {
                SetText(ConnectedPrefix + "PPG: JSON sem amostras RED/IR", "WAIT_SAMPLES");
                return;
            }

            // The coordinator alone authorizes estimates. PlacementState only
            // selects an informative message when that coordinator has no contact.
            if (!coordinator.OpticalContactConfirmed)
            {
                SetText(FormatNoContactStatus(placement != null && placement.SessionActive
                    ? placement.State
                    : FingerPlacementState.WaitingForData), "NO_CONTACT");
                return;
            }

            if (!coordinator.Gate01Approved)
            {
                // G1 expiry/rejection blocks the actual measurement; never
                // show numeric values as if the prior estimate were current.
                string reason = coordinator.Gate01Status == "Failed"
                    ? "G1 reprovado: aguardando sinal valido"
                    : "aguardando aprovacao G1";
                SetText(ConnectedPrefix + FormatPitaco24Values(null, null) +
                    "\nPPG: " + reason, "G1_BLOCK");
                return;
            }

            if (coordinator.HudHoldingShortContactGap)
            {
                // Bounded, explicitly labeled visual hold for 1-2 false-contact
                // frames only. The Mixer continues reading LastMeasurement,
                // which has already been invalidated by the coordinator.
                var held = coordinator.HudMeasurement;
                SetText(ConnectedPrefix + FormatPitaco24Values(
                    held.BpmEstimate, held.SpO2DemoPercent) +
                    "\nPPG: ultima leitura (contato instavel)", "HOLD_VISUAL_ONLY");
                return;
            }

            if (coordinator.HudHoldingRejectedBpm)
            {
                // First/second invalid sliding-window verdict: previous BPM
                // is shown as an explicitly historical value, never as live.
                // Bridge/Mixer continue to see null for rejected BPM.
                var latest = coordinator.HudMeasurement;
                SetText(ConnectedPrefix + FormatPitaco24Values(
                    coordinator.HudRetainedBpmVisualOnly,
                    latest != null ? latest.SpO2DemoPercent : null) +
                    "\nPPG: BPM ultima leitura (estimador reavaliando)",
                    "BPM_HOLD_VISUAL_ONLY");
                return;
            }

            if (!coordinator.HudHasRecentLiveMeasurement)
            {
                // Explicit unavailable state instead of an unexplained HUD
                // reset. No clinical or gameplay authority is inferred here.
                SetText(ConnectedPrefix + FormatPitaco24Values(null, null) +
                    "\nPPG: aguardando estimativa valida", "WAIT_ESTIMATE");
                return;
            }

            // Display the coordinator's most recent published estimate, not
            // the Player's independent 1-second visual cache. The same validated
            // measurement now reaches all three game scenes without a stale
            // intermediary or misleading last-value lag.
            var live = coordinator.HudMeasurement;
            SetText(ConnectedPrefix + FormatPitaco24Values(
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
