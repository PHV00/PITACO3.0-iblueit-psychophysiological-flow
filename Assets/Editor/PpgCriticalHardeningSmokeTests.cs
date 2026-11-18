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

            // Exercise the unchanged 8-second estimator on deterministic
            // synthetic PPG, independently of the USB worker and scene.
            var clean = new System.Collections.Generic.List<PpgSample>();
            for (int i = 0; i <= 200; ++i)
            {
                double phase = 2.0 * Math.PI * 55.0 * (i / 25.0) / 60.0;
                int red = 97000 + (int)(240.0 * Math.Sin(phase));
                int ir = 112000 + (int)(400.0 * Math.Sin(phase));
                clean.Add(new PpgSample((ushort)i, (uint)(i * 40), red, ir));
            }
            PpgProcessedWindow processed;
            string reason;
            Check("8-second clean PPG processing succeeds",
                PpgSignalProcessor.TryProcess(clean, out processed, out reason));
            double bpm, corr;
            Check("clean synthetic BPM recovered",
                BpmEstimator.TryEstimate(processed, out bpm, out corr) &&
                Math.Abs(bpm - 55.0) < 4.0 && corr > 0.57);

            var shortWindow = new System.Collections.Generic.List<PpgSample>(clean);
            shortWindow.RemoveAt(shortWindow.Count - 1);
            Check("shorter than 8 seconds is not authorized",
                !PpgSignalProcessor.TryProcess(shortWindow,
                    out processed, out reason));

            var brokenSequence = new System.Collections.Generic.List<PpgSample>(clean);
            PpgSample broken = brokenSequence[100];
            brokenSequence[100] = new PpgSample((ushort)(broken.Sequence + 4),
                broken.TimeMs, broken.Red, broken.Ir);
            Check("sequence discontinuity is rejected",
                !PpgSignalProcessor.TryProcess(brokenSequence,
                    out processed, out reason));

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
