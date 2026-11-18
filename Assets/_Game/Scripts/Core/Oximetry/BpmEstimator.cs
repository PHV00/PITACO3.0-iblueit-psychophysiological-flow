using System;

namespace Ibit.Core.Oximetry
{
    /// <summary>
    /// Experimental IR autocorrelation BPM estimator. Threshold comparison
    /// is configurable ONLY to support a controlled 0.57 vs 0.55 test.
    /// RED analysis is diagnostic-only and never publishes a BPM.
    /// </summary>
    public static class BpmEstimator
    {
        public const double MinimumBpm = 35.0;
        public const double MaximumBpm = 200.0;
        public const double BaselineMinimumAutocorrelation = 0.57;
        public const double ExperimentalMinimumAutocorrelation = 0.55;
        private const double PeakTolerance = 0.075;

        // Existing production-compatible API retains the 0.57 baseline.
        public static bool TryEstimate(PpgProcessedWindow input,
            out double bpm, out double correlationPeak)
        {
            string reason;
            double lag;
            return TryEstimateDetailed(input, out bpm, out correlationPeak,
                out lag, out reason);
        }

        public static bool TryEstimateDetailed(PpgProcessedWindow input,
            out double bpm, out double correlationPeak,
            out double selectedLag, out string reason)
        {
            double candidateBpm;
            return TryEstimateDetailedWithThreshold(input,
                BaselineMinimumAutocorrelation, out bpm, out correlationPeak,
                out selectedLag, out candidateBpm, out reason);
        }

        // Diagnostic candidate is NOT an authorized measurement when false.
        // Compute lag before applying the confidence cutoff so rejections can
        // be investigated, without ever substituting a rejected value for BPM.
        public static bool TryEstimateDetailedWithThreshold(
            PpgProcessedWindow input, double minimumCorrelation,
            out double bpm, out double correlationPeak,
            out double candidateLag, out double candidateBpm,
            out string reason)
        {
            if (input == null)
            {
                bpm = correlationPeak = candidateLag = candidateBpm = 0.0;
                reason = "INPUT_IR_AC_INSUFFICIENT";
                return false;
            }
            return TryEstimateSignal(input.IrAc, input.SamplingHz,
                minimumCorrelation, out bpm, out correlationPeak,
                out candidateLag, out candidateBpm, out reason);
        }

        // Run exactly the same autocorrelation search on RED as a shadow
        // diagnostic; its result must never enter the Bridge or the Mixer.
        public static bool TryEstimateRedDiagnostic(PpgProcessedWindow input,
            out double candidateBpm, out double correlationPeak,
            out double candidateLag, out string reason)
        {
            double ignoredPublishedBpm;
            if (input == null)
            {
                candidateBpm = correlationPeak = candidateLag = 0.0;
                reason = "INPUT_RED_AC_INSUFFICIENT";
                return false;
            }
            return TryEstimateSignal(input.RedAc, input.SamplingHz,
                BaselineMinimumAutocorrelation, out ignoredPublishedBpm,
                out correlationPeak, out candidateLag, out candidateBpm,
                out reason);
        }

        private static bool TryEstimateSignal(double[] data, double samplingHz,
            double minimumCorrelation, out double bpm,
            out double correlationPeak, out double candidateLag,
            out double candidateBpm, out string reason)
        {
            bpm = correlationPeak = candidateLag = candidateBpm = 0.0;
            reason = "INPUT_AC_INSUFFICIENT";
            if (data == null || data.Length < 70 ||
                double.IsNaN(samplingHz) || double.IsInfinity(samplingHz) ||
                samplingHz <= 0.0)
                return false;
            if (minimumCorrelation != BaselineMinimumAutocorrelation &&
                minimumCorrelation != ExperimentalMinimumAutocorrelation)
            {
                reason = "UNSUPPORTED_AUTOCORR_THRESHOLD";
                return false;
            }

            int n = data.Length;
            double average = 0.0;
            for (int i = 0; i < n; i++) average += data[i];
            average /= n;

            var centered = new double[n];
            for (int i = 0; i < n; i++) centered[i] = data[i] - average;

            int minLag = Math.Max(2, (int)(samplingHz * 60.0 / MaximumBpm));
            int maxLag = Math.Min(n / 3,
                (int)(samplingHz * 60.0 / MinimumBpm) + 1);
            if (minLag > maxLag) { reason = "LAG_RANGE_INVALID"; return false; }

            var scores = new double[maxLag + 2];
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double cross = 0.0, leftPower = 0.0, rightPower = 0.0;
                for (int i = 0; i < n - lag; i++)
                {
                    double left = centered[i], right = centered[i + lag];
                    cross += left * right;
                    leftPower += left * left;
                    rightPower += right * right;
                }
                double denominator = Math.Sqrt(leftPower * rightPower);
                scores[lag] = denominator > 0.0 ? cross / denominator : 0.0;
                correlationPeak = Math.Max(correlationPeak, scores[lag]);
            }

            // Same shortest-strong-local-peak preference as the baseline.
            int chosenLag = -1;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double left = lag > minLag ? scores[lag - 1] : -1.0;
                double right = lag < maxLag ? scores[lag + 1] : -1.0;
                if (scores[lag] >= correlationPeak - PeakTolerance &&
                    scores[lag] >= left && scores[lag] >= right)
                {
                    chosenLag = lag;
                    break;
                }
            }

            bool belowThreshold = correlationPeak < minimumCorrelation;
            if (chosenLag < 0)
            {
                reason = belowThreshold ? LowCorrelationReason(minimumCorrelation)
                    : "PEAK_NOT_FOUND";
                return false;
            }

            double refinedLag = chosenLag;
            if (chosenLag > minLag && chosenLag < maxLag)
            {
                double left = scores[chosenLag - 1];
                double center = scores[chosenLag];
                double right = scores[chosenLag + 1];
                double curvature = left - 2.0 * center + right;
                if (curvature < -1e-8)
                {
                    double offset = 0.5 * (left - right) / curvature;
                    refinedLag += Math.Max(-0.5, Math.Min(0.5, offset));
                }
            }

            candidateLag = refinedLag;
            double estimate = 60.0 * samplingHz / refinedLag;
            if (!double.IsNaN(estimate) && !double.IsInfinity(estimate) &&
                estimate >= MinimumBpm && estimate <= MaximumBpm)
                candidateBpm = Math.Round(estimate, 1);

            if (belowThreshold)
            {
                reason = LowCorrelationReason(minimumCorrelation);
                return false;
            }
            if (candidateBpm <= 0.0)
            {
                reason = "BPM_OUT_OF_RANGE";
                return false;
            }

            reason = "OK";
            bpm = candidateBpm;
            return true;
        }

        private static string LowCorrelationReason(double threshold)
        {
            return threshold == ExperimentalMinimumAutocorrelation
                ? "AUTOCORR_BELOW_0_55" : "AUTOCORR_BELOW_0_57";
        }
    }
}
