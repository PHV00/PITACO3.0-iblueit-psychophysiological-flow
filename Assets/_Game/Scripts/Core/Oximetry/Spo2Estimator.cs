using System;

namespace Ibit.Core.Oximetry
{
    /// <summary>
    /// Demonstration only: RMS AC/DC ratio of ratios followed by the
    /// Maxim/SparkFun example polynomial. NOT a PITACO-specific calibration;
    /// output must never drive clinical warnings or DeepDDA decisions.
    /// </summary>
    public static class Spo2Estimator
    {
        public static bool TryEstimateDemo(PpgProcessedWindow input,
            out double spo2DemoPercent, out double ratio)
        {
            string reason;
            return TryEstimateDemoDetailed(input, out spo2DemoPercent,
                out ratio, out reason);
        }

        // Diagnostic overload; exactly the same numerical gates and polynomial.
        public static bool TryEstimateDemoDetailed(PpgProcessedWindow input,
            out double spo2DemoPercent, out double ratio, out string reason)
        {
            spo2DemoPercent = 0.0;
            ratio = 0.0;
            reason = "INPUT_NULL";
            if (input == null) return false;
            // A synthesized RED/IR gap is not valid input for SpO2.
            if (input.ReconstructedSamples > 0)
            {
                reason = "RECONSTRUCTED_PPG_GAP";
                return false;
            }
            if (input.RedDc <= 0.0 || input.IrDc <= 0.0)
            {
                reason = "DC_NOT_POSITIVE";
                return false;
            }
            if (input.RedAcRms <= 1e-9 || input.IrAcRms <= 1e-9)
            {
                reason = "AC_RMS_TOO_LOW";
                return false;
            }

            ratio = (input.RedAcRms / input.RedDc) /
                    (input.IrAcRms / input.IrDc);
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) ||
                ratio < 0.2 || ratio > 1.4)
            {
                reason = "RATIO_OUTSIDE_0_2_TO_1_4";
                return false;
            }

            // Example curve also used in pitaco_bridge/experimental.py.
            double estimate = -45.060 * ratio * ratio + 30.354 * ratio + 94.845;
            if (double.IsNaN(estimate) || double.IsInfinity(estimate) ||
                estimate < 70.0 || estimate > 100.0)
            {
                reason = "SPO2_DEMO_OUTSIDE_70_TO_100";
                return false;
            }

            reason = "OK";
            spo2DemoPercent = Math.Round(estimate, 1);
            return true;
        }
    }
}
