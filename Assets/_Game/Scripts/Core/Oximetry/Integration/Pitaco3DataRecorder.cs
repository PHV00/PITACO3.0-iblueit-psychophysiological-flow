using System;
using System.Collections.Generic;
using Ibit.Core.Data;
using Ibit.Core.Serial;
using UnityEngine;

namespace Ibit.Core.Oximetry.Integration
{
    /// <summary>
    /// Stage-scoped historian ONLY. It does not feed the Mixer, modify a gate,
    /// re-estimate physiology or authorize SpO2 use in gameplay.
    /// Attached to the scene's PlataformLogger; never opens a serial port.
    /// </summary>
    public sealed class Pitaco3DataRecorder : MonoBehaviour
    {
        public const string DeviceName = "Pitaco 3.0";
        public const int DataSchemaVersion = 1;

        [SerializeField] private bool captureRawPpg = true;
        [SerializeField] private int maxRawSamplesPerStage = 12000;

        private readonly List<BpmData> bpm = new List<BpmData>();
        private readonly List<Spo2Data> spo2 = new List<Spo2Data>();
        private readonly List<PpgRawData> raw = new List<PpgRawData>();

        private PpgProcessingCoordinator source;
        private bool recording;
        private bool observedModernDevice;
        private int rawSamplesOmitted;
        private float nextAttachAttempt;
        private string clientRunId;

        public string ClientRunId => clientRunId;
        public bool HasObservedModernDevice => observedModernDevice;
        public bool IsRecording => recording;

        // Called by PlataformLogger, which owns the existing stage lifecycle.
        public void StartCapture()
        {
            if (recording) return;
            bpm.Clear();
            spo2.Clear();
            raw.Clear();
            rawSamplesOmitted = 0;
            observedModernDevice = false;
            clientRunId = Guid.NewGuid().ToString("N");
            recording = true;
            TryAttach();
            CheckModernConnection();
        }

        public void StopCapture()
        {
            if (!recording) return;
            recording = false;
            Detach();
            Debug.Log("[PITACO3-RECORD] run=" + clientRunId +
                      " | bpm=" + bpm.Count +
                      " | spo2=" + spo2.Count +
                      " | raw=" + raw.Count +
                      " | rawOmitted=" + rawSamplesOmitted);
        }

        private void Update()
        {
            if (!recording) return;
            CheckModernConnection();
            if (source == null && Time.unscaledTime >= nextAttachAttempt)
            {
                nextAttachAttempt = Time.unscaledTime + 1f;
                TryAttach();
            }
        }

        private void CheckModernConnection()
        {
            var manager = PitacoConnectionManager.Active;
            if (manager != null &&
                manager.ConnectedVersion == PitacoConnectionManager.Version.Pitaco24)
                observedModernDevice = true;
        }

        private void TryAttach()
        {
            if (!recording || source != null) return;
            var candidate = FindObjectOfType<PpgProcessingCoordinator>();
            if (candidate == null) return;
            source = candidate;
            source.OnRawSample += RecordRaw;
            source.OnMeasurement += RecordMeasurement;
        }

        private void Detach()
        {
            if (source == null) return;
            source.OnRawSample -= RecordRaw;
            source.OnMeasurement -= RecordMeasurement;
            source = null;
        }

        private void RecordRaw(PpgRawSample sample)
        {
            if (!recording || sample == null) return;
            observedModernDevice = true;
            if (!captureRawPpg) return;
            if (raw.Count >= Math.Max(0, maxRawSamplesPerStage))
            {
                if (rawSamplesOmitted < int.MaxValue) ++rawSamplesOmitted;
                return;
            }

            var manager = PitacoConnectionManager.Active;
            raw.Add(new PpgRawData
            {
                TimestampUtc = DateTime.UtcNow,
                DeviceTimeMs = sample.DeviceTimeMs,
                Sequence = sample.Sequence,
                Red = sample.Red,
                Ir = sample.Ir,
                Contact = sample.Contact,
                SqiReason = sample.SqiReason,
                QueueLagMs = manager != null ? manager.CurrentDispatchQueueLagMs : 0
            });
        }

        private static double? Finite(double? value)
        {
            return value.HasValue &&
                   !double.IsNaN(value.Value) &&
                   !double.IsInfinity(value.Value)
                ? value : null;
        }

        private void RecordMeasurement(PpgMeasurement measurement)
        {
            if (!recording || measurement == null) return;
            observedModernDevice = true;
            var now = DateTime.UtcNow;
            var contact = source != null && source.OpticalContactConfirmed;
            var g1 = source != null && source.Gate01Approved;
            var queueLag = source != null ? source.LastPublishedQueueLagMs : 0;

            // Record null when a metric is rejected, not the last HUD-held value.
            bpm.Add(new BpmData
            {
                TimestampUtc = now,
                DeviceTimeMs = measurement.DeviceTimeMs,
                Bpm = Finite(measurement.BpmEstimate),
                Autocorrelation = Finite(measurement.Autocorrelation),
                Status = measurement.Status,
                ContactConfirmed = contact,
                G1Approved = g1,
                QueueLagMs = queueLag
            });

            spo2.Add(new Spo2Data
            {
                TimestampUtc = now,
                DeviceTimeMs = measurement.DeviceTimeMs,
                Spo2Percent = Finite(measurement.SpO2DemoPercent),
                RatioOfRatios = Finite(measurement.RatioOfRatios),
                IsDemonstration = !measurement.SpO2CalibratedForLegacyRules,
                Status = measurement.Status,
                ContactConfirmed = contact,
                G1Approved = g1,
                QueueLagMs = queueLag
            });
        }

        // Snapshot contains copies; subsequent sensor callbacks cannot mutate
        // a DTO being written to disk or serialized for the HTTP client.
        public FlowDataDevice BuildDeviceSnapshot(FlowDataDevice respiratory)
        {
            return new FlowDataDevice
            {
                DeviceName = DeviceName,
                SchemaVersion = DataSchemaVersion,
                FlowData = respiratory != null && respiratory.FlowData != null
                    ? new List<FlowData>(respiratory.FlowData)
                    : new List<FlowData>(),
                BpmData = new List<BpmData>(bpm),
                Spo2Data = new List<Spo2Data>(spo2),
                PpgRawData = captureRawPpg ? new List<PpgRawData>(raw) : null,
                RawSamplesOmitted = rawSamplesOmitted
            };
        }

        private void OnDestroy()
        {
            Detach();
        }
    }
}
