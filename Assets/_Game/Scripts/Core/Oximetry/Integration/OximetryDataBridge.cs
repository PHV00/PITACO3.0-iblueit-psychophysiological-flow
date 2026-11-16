using System;
using Ibit.Core.Serial;
using UnityEngine;

namespace Ibit.Core.Oximetry.Integration
{
    // The source is part of the data contract. A numeric SpO2 alone does not
    // establish whether the existing saturation rules may be applied.
    public enum OximetrySource
    {
        None,
        LegacyOximeter,
        Pitaco24
    }

    /// <summary>
    /// Final numerical output for the Mixer. Quality and permission to use
    /// legacy saturation rules are independent of numerical availability.
    /// </summary>
    public struct OximetrySnapshot
    {
        public readonly OximetrySource Source;
        public readonly double? Bpm;
        public readonly double? SpO2Percent;
        public readonly bool OpticalContactConfirmed;
        public readonly bool BasicSignalQualityApproved;
        public readonly bool SpO2IsDemonstration;
        public readonly bool EligibleForLegacySpo2Rules;
        public readonly float AgeSeconds;

        public bool HasBpm { get { return Bpm.HasValue; } }
        public bool HasSpO2 { get { return SpO2Percent.HasValue; } }

        public OximetrySnapshot(OximetrySource source, double? bpm,
            double? spo2Percent, bool contact, bool quality,
            bool demonstration, bool eligibleForLegacyRules, float age)
        {
            Source = source;
            Bpm = bpm;
            SpO2Percent = spo2Percent;
            OpticalContactConfirmed = contact;
            BasicSignalQualityApproved = quality;
            SpO2IsDemonstration = demonstration;
            EligibleForLegacySpo2Rules = eligibleForLegacyRules;
            AgeSeconds = age;
        }

        public static OximetrySnapshot Unavailable(OximetrySource source)
        {
            return new OximetrySnapshot(source, null, null, false, false,
                source == OximetrySource.Pitaco24, false,
                float.PositiveInfinity);
        }
    }

    /// <summary>
    /// Pure mapping for unit tests. 'Final' means no further RED/IR processing;
    /// it does not mean clinical validation or fitness for patient alerts.
    /// </summary>
    public static class OximetrySnapshotFactory
    {
        public const float MaxPitacoAgeSeconds = 2f;
        public const float MaxLegacyAgeSeconds = 15f;

        private static bool HasValidAge(float age, float limit)
        {
            return !float.IsNaN(age) && !float.IsInfinity(age) &&
                age >= 0f && age <= limit;
        }

        private static bool IsBpm(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) &&
                !double.IsInfinity(value.Value) && value.Value > 0;
        }

        private static bool IsSpO2(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) &&
                !double.IsInfinity(value.Value) && value.Value > 0 &&
                value.Value <= 100;
        }

        public static OximetrySnapshot FromPitaco24(
            bool connected, bool contact, bool gate01Approved,
            PpgMeasurement measurement, float ageSeconds)
        {
            if (!connected || !contact || !gate01Approved ||
                measurement == null || !HasValidAge(ageSeconds, MaxPitacoAgeSeconds))
                return OximetrySnapshot.Unavailable(OximetrySource.Pitaco24);

            double? bpm = IsBpm(measurement.BpmEstimate)
                ? measurement.BpmEstimate : null;
            double? spo2 = IsSpO2(measurement.SpO2DemoPercent)
                ? measurement.SpO2DemoPercent : null;

            // A future independently validated estimator can explicitly
            // mark saturation as eligible; TryEstimateDemo never does.
            bool eligible = spo2.HasValue &&
                measurement.SpO2CalibratedForLegacyRules;
            return new OximetrySnapshot(OximetrySource.Pitaco24,
                bpm, spo2, true, true, !eligible, eligible, ageSeconds);
        }

        public static OximetrySnapshot FromLegacy(
            bool connected, double bpm, double spo2, float ageSeconds)
        {
            if (!connected || !HasValidAge(ageSeconds, MaxLegacyAgeSeconds))
                return OximetrySnapshot.Unavailable(
                    OximetrySource.LegacyOximeter);
            double? finalBpm = IsBpm(bpm) ? (double?)bpm : null;
            double? finalSpO2 = IsSpO2(spo2) ? (double?)spo2 : null;
            return new OximetrySnapshot(OximetrySource.LegacyOximeter,
                finalBpm, finalSpO2, true, true, false,
                finalSpO2.HasValue, ageSeconds);
        }
    }

    /// <summary>
    /// Persistent read-only integration. Does not open any USB/COM port.
    /// Uses existing PpgProcessingCoordinator and legacy Player inputs.
    /// </summary>
    public sealed class OximetryDataBridge : MonoBehaviour
    {
        public static OximetryDataBridge Active { get; private set; }

        private PpgProcessingCoordinator ppg;
        private SerialControllerOximetro legacyController;
        private PpgMeasurement lastPublished;
        private float lastPitacoPublicationAt = -1f;
        private float lastLegacyPublicationAt = -1f;
        private double lastLegacyBpm;
        private double lastLegacySpo2;

        private void Awake()
        {
            if (Active != null && Active != this)
            {
                Destroy(this);
                return;
            }
            Active = this;
        }

        private void OnEnable()
        {
            if (Active != this) return;
            AttachPpg();
        }

        private void AttachPpg()
        {
            if (ppg == null)
                ppg = GetComponent<PpgProcessingCoordinator>();
            if (ppg == null) return;
            ppg.OnMeasurement -= OnPpgMeasurement;
            ppg.OnMeasurement += OnPpgMeasurement;
        }

        private void OnPpgMeasurement(PpgMeasurement measurement)
        {
            lastPublished = measurement;
            lastPitacoPublicationAt = Time.unscaledTime;
        }

        // Called after the legacy Player accepts its next comma-delimited pair.
        public void RecordLegacy(double bpm, double spo2)
        {
            lastLegacyBpm = bpm;
            lastLegacySpo2 = spo2;
            lastLegacyPublicationAt = Time.unscaledTime;
        }

        public OximetrySnapshot GetSnapshot()
        {
            PitacoConnectionManager manager = PitacoConnectionManager.Active;
            if (manager != null &&
                manager.ConnectedVersion == PitacoConnectionManager.Version.Pitaco24)
            {
                if (ppg == null) AttachPpg();
                PpgMeasurement measurement = ppg != null ? ppg.LastMeasurement : null;
                // No old value after loss of contact, rejected G1, timeout or
                // a transient invalidation: LastMeasurement gets replaced,
                // but only successful processing emits OnMeasurement.
                float age = measurement != null &&
                    object.ReferenceEquals(measurement, lastPublished) &&
                    lastPitacoPublicationAt >= 0f
                    ? Mathf.Max(0f, Time.unscaledTime - lastPitacoPublicationAt)
                    : float.PositiveInfinity;
                return OximetrySnapshotFactory.FromPitaco24(
                    true, ppg != null && ppg.OpticalContactConfirmed,
                    ppg != null && ppg.Gate01Approved, measurement, age);
            }

            // Legacy oximeter remains independent: no stale values after
            // disconnection and no silent fallback while PITACO 2.4 is active.
            if (legacyController == null)
                legacyController = FindObjectOfType<SerialControllerOximetro>();
            bool connected = legacyController != null && legacyController.IsConnected;
            float legacyAge = lastLegacyPublicationAt >= 0f
                ? Mathf.Max(0f, Time.unscaledTime - lastLegacyPublicationAt)
                : float.PositiveInfinity;
            return OximetrySnapshotFactory.FromLegacy(connected,
                lastLegacyBpm, lastLegacySpo2, legacyAge);
        }

        private void OnDisable()
        {
            if (ppg != null)
                ppg.OnMeasurement -= OnPpgMeasurement;
            lastPublished = null;
            lastPitacoPublicationAt = -1f;
            if (Active == this) Active = null;
        }
    }
}
