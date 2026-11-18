#if UNITY_EDITOR
using System;
using Ibit.Core.Oximetry;
using Ibit.Core.Oximetry.Integration;
using UnityEditor;
using UnityEngine;

// Run from Tools > PITACO > Run PPG critical hardening smoke tests.
// No device, serial port, or play-mode is required. Restores DEMO opt-in
// to false even when a test fails.
public static class PpgCriticalHardeningSmokeTests
{
    private static int passed;

    [MenuItem("Tools/PITACO/Run PPG critical hardening smoke tests")]
    public static void Run()
    {
        passed = 0;
        OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(false);
        try
        {
            var measure = new PpgMeasurement(1000, 25.0, 55.0,
                98.0, 0.45, 0.90, "EXPERIMENTAL - NAO CALIBRADO");

            var safe = OximetrySnapshotFactory.FromPitaco24(
                true, true, true, measure, 0.1f);
            Check("default blocks uncalibrated SpO2 game rules",
                !safe.EligibleForLegacySpo2Rules && safe.SpO2IsDemonstration);
            Check("BPM remains independently available",
                safe.HasBpm && safe.Bpm.Value == 55.0);
            Check("DEMO remains visible as DEMO for controlled HUD",
                safe.HasSpO2 && safe.SpO2Percent.Value == 98.0);

            OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(true);
            var explicitTest = OximetrySnapshotFactory.FromPitaco24(
                true, true, true, measure, 0.1f);
            Check("controlled explicit opt-in enables legacy rules",
                explicitTest.EligibleForLegacySpo2Rules);

            Check("no contact blocks opt-in",
                !OximetrySnapshotFactory.FromPitaco24(
                    true, false, true, measure, 0.1f)
                    .EligibleForLegacySpo2Rules);
            Check("failed G1 blocks opt-in",
                !OximetrySnapshotFactory.FromPitaco24(
                    true, true, false, measure, 0.1f)
                    .EligibleForLegacySpo2Rules);
            Check("stale publication blocks opt-in",
                !OximetrySnapshotFactory.FromPitaco24(
                    true, true, true, measure, 2.5f)
                    .EligibleForLegacySpo2Rules);

            float fresh = OximetrySnapshotFactory.EffectiveAgeSeconds(0.1f, 30);
            float delayed = OximetrySnapshotFactory.EffectiveAgeSeconds(0.1f, 2500);
            Check("queued age counted in publication freshness",
                Math.Abs(fresh - 0.13f) < 0.0001f && delayed > 2.5f);
            Check("queue-age lower bound is non-negative",
                Math.Abs(OximetrySnapshotFactory.EffectiveAgeSeconds(
                    0.1f, -1) - 0.1f) < 0.0001f);
            Check("invalid age fails closed",
                float.IsPositiveInfinity(
                    OximetrySnapshotFactory.EffectiveAgeSeconds(float.NaN, 0)));

            var legacy = OximetrySnapshotFactory.FromLegacy(true, 55.0, 97.0, 0.1f);
            Check("independently connected legacy oximeter still eligible",
                legacy.EligibleForLegacySpo2Rules &&
                OximetrySnapshotFactory.SelectForLegacyRules(safe, legacy).Source ==
                    OximetrySource.LegacyOximeter);

            Debug.Log("[PPG-TEST] PASS: " + passed +
                " critical-hardening smoke assertions (Edit Mode).");
        }
        finally
        {
            OximetrySnapshotFactory.ConfigureDemoSpo2LegacyRulesForTesting(false);
        }
    }

    private static void Check(string name, bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("[PPG-TEST] FAIL: " + name);
        ++passed;
    }
}
#endif
