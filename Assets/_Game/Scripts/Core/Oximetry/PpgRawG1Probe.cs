using System;
using System.Collections.Generic;

namespace Ibit.Core.Oximetry
{
    /// <summary>
    /// READ-ONLY diagnostic reconstruction from the *unmodified* firmware
    /// type=sample JSON stream. Does not approve/reject G1 or change the
    /// processing buffer, contact monitor, BPM, SpO2 or gameplay.
    ///
    /// The firmware can reset its G1 accumulator on contact/FIFO events that
    /// are not exactly reconstructible from Unity. A matching sample count and
    /// uninterrupted sequence improve coverage but do NOT prove identity
    /// with the firmware's private 5-second window.
    /// </summary>
    public sealed class PpgRawG1Probe
    {
        private struct Raw
        {
            public readonly ushort Sequence;
            public readonly uint TimeMs;
            public readonly int Red;
            public readonly int Ir;
            public readonly bool Contact;

            public Raw(ushort sequence, uint timeMs, int red, int ir, bool contact)
            {
                Sequence = sequence;
                TimeMs = timeMs;
                Red = red;
                Ir = ir;
                Contact = contact;
            }
        }

        // A bounded diagnostic history. G1 is nominally 5s, but the firmware
        // may publish after a scheduling delay or contact-triggered restart.
        private const uint MaximumHistoryMs = 6500;
        private const int MaximumHistorySamples = 384;
        private const int AdcMaximum = 262143;
        private const int ClipStart = 260095;
        private readonly List<Raw> history = new List<Raw>(192);

        public void Clear() { history.Clear(); }

        public void Observe(int sequence, long timeMs, int red, int ir,
            bool contact)
        {
            if (sequence < 0 || sequence > ushort.MaxValue ||
                timeMs < 0 || timeMs > uint.MaxValue ||
                red < 0 || red > AdcMaximum || ir < 0 || ir > AdcMaximum)
                return;

            uint time = (uint)timeMs;
            if (history.Count > 0 &&
                unchecked((int)(time - history[history.Count - 1].TimeMs)) < 0)
            {
                // Time restarted/reordered; no cross-session statistics.
                history.Clear();
            }

            history.Add(new Raw((ushort)sequence, time, red, ir, contact));

            while (history.Count > 0 &&
                unchecked(time - history[0].TimeMs) > MaximumHistoryMs)
                history.RemoveAt(0);
            while (history.Count > MaximumHistorySamples)
                history.RemoveAt(0);
        }

        public PpgRawG1Summary Summarize(uint eventTimeMs, int firmwareSamples)
        {
            // We deliberately select at most the firmware-reported sample
            // count from the tail of the received stream, not a 5s mean of
            // an independent Unity clock. No "G1 PASS" is inferred from it.
            int expected = Math.Max(0, firmwareSamples);
            int available = 0;
            for (int i = history.Count - 1; i >= 0; --i)
            {
                uint age = unchecked(eventTimeMs - history[i].TimeMs);
                if (age > MaximumHistoryMs)
                    break;
                ++available;
            }

            int selected = Math.Min(available, expected);
            if (selected == 0)
                return new PpgRawG1Summary(expected, 0, available, 0, 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, true);

            int start = history.Count - selected;
            int redMin = int.MaxValue, redMax = 0, irMin = int.MaxValue, irMax = 0;
            long redSum = 0, irSum = 0;
            int redClips = 0, irClips = 0, contactFalse = 0, sequenceGaps = 0;
            for (int i = start; i < history.Count; i++)
            {
                Raw p = history[i];
                if (p.Red < redMin) redMin = p.Red;
                if (p.Red > redMax) redMax = p.Red;
                if (p.Ir < irMin) irMin = p.Ir;
                if (p.Ir > irMax) irMax = p.Ir;
                redSum += p.Red;
                irSum += p.Ir;
                if (p.Red >= ClipStart) ++redClips;
                if (p.Ir >= ClipStart) ++irClips;
                if (!p.Contact) ++contactFalse;
                if (i > start &&
                    ((history[i - 1].Sequence + 1) & 0xFFFF) != p.Sequence)
                    ++sequenceGaps;
            }

            return new PpgRawG1Summary(expected, selected, available, redMin,
                redMax, irMin, irMax, (int)(redSum / selected),
                (int)(irSum / selected), redClips, irClips, contactFalse,
                sequenceGaps, unchecked(eventTimeMs - history[start].TimeMs),
                false);
        }
    }

    /// <summary>
    /// Observed RED/IR statistics, NOT the authoritative firmware G1 metrics.
    /// Acquisition FIFO/I2C gap is not observable from type=sample alone.
    /// </summary>
    public sealed class PpgRawG1Summary
    {
        public readonly int FirmwareReportedSamples;
        public readonly int ObservedSamples;
        public readonly int AvailableHistorySamples;
        public readonly int RedMin, RedMax, IrMin, IrMax, RedMean, IrMean;
        public readonly int RedClips, IrClips, ContactFalse, SequenceGaps;
        public readonly uint ObservedSpanMs;
        public readonly bool Empty;

        public int RedRange { get { return Empty ? 0 : RedMax - RedMin; } }
        public int IrRange { get { return Empty ? 0 : IrMax - IrMin; } }
        public bool CountCovered =>
            ObservedSamples == FirmwareReportedSamples && !Empty;
        public bool SequenceContinuous => SequenceGaps == 0;
        public bool CountAndSequenceCovered => CountCovered && SequenceContinuous;

        internal PpgRawG1Summary(int expected, int observed, int available,
            int redMin, int redMax, int irMin, int irMax, int redMean, int irMean,
            int redClips, int irClips, int contactFalse, int sequenceGaps,
            uint spanMs, bool empty)
        {
            FirmwareReportedSamples = expected;
            ObservedSamples = observed;
            AvailableHistorySamples = available;
            RedMin = redMin;
            RedMax = redMax;
            IrMin = irMin;
            IrMax = irMax;
            RedMean = redMean;
            IrMean = irMean;
            RedClips = redClips;
            IrClips = irClips;
            ContactFalse = contactFalse;
            SequenceGaps = sequenceGaps;
            ObservedSpanMs = spanMs;
            Empty = empty;
        }

        public string ToDiagnosticString()
        {
            if (Empty)
                return "metrics=UNITY_RAW_SHADOW | observed=0" +
                    " | reported=" + FirmwareReportedSamples +
                    " | acquisitionGap=UNOBSERVABLE | match=UNKNOWN";

            return "metrics=UNITY_RAW_SHADOW_NOT_FIRMWARE" +
                " | observed=" + ObservedSamples +
                " | reported=" + FirmwareReportedSamples +
                " | availableHistory=" + AvailableHistorySamples +
                " | countCovered=" + CountCovered +
                " | sequenceGaps=" + SequenceGaps +
                " | spanMs=" + ObservedSpanMs +
                " | redMin=" + RedMin + " | redMax=" + RedMax +
                " | redMean=" + RedMean + " | redRange=" + RedRange +
                " | irMin=" + IrMin + " | irMax=" + IrMax +
                " | irMean=" + IrMean + " | irRange=" + IrRange +
                " | redClips=" + RedClips + " | irClips=" + IrClips +
                " | falseContactSamples=" + ContactFalse +
                " | refMinRange=500 | refMinMean=5000" +
                " | acquisitionGap=UNOBSERVABLE" +
                " | match=" + (CountAndSequenceCovered
                    ? "CANDIDATE_ONLY" : "INCOMPLETE");
        }
    }
}
