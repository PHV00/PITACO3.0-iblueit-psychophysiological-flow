using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ibit.Core.Data
{
    // The legacy respiratory contract is unchanged. The optical arrays are
    // optional, and only PITACO 3.0 stage recordings populate them.
    public class FlowDataDevice
    {
        [JsonProperty("deviceName")]
        public string DeviceName { get; set; }

        [JsonProperty("flowData")]
        public List<FlowData> FlowData { get; set; } = new List<FlowData>();

        [JsonProperty("schemaVersion", NullValueHandling = NullValueHandling.Ignore)]
        public int? SchemaVersion { get; set; }

        [JsonProperty("bpmData", NullValueHandling = NullValueHandling.Ignore)]
        public List<BpmData> BpmData { get; set; }

        [JsonProperty("spo2Data", NullValueHandling = NullValueHandling.Ignore)]
        public List<Spo2Data> Spo2Data { get; set; }

        [JsonProperty("ppgRawData", NullValueHandling = NullValueHandling.Ignore)]
        public List<PpgRawData> PpgRawData { get; set; }

        [JsonProperty("rawSamplesOmitted", NullValueHandling = NullValueHandling.Ignore)]
        public int? RawSamplesOmitted { get; set; }
    }

    public class BpmData
    {
        // Timestamp when Unity published the estimate, not an ADC sample time.
        [JsonProperty("timestamp")] public DateTime TimestampUtc { get; set; }
        [JsonProperty("deviceTimeMs")] public long DeviceTimeMs { get; set; }
        [JsonProperty("bpm")] public double? Bpm { get; set; }
        [JsonProperty("autocorrelation")] public double? Autocorrelation { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("contactConfirmed")] public bool ContactConfirmed { get; set; }
        [JsonProperty("g1Approved")] public bool G1Approved { get; set; }
        [JsonProperty("queueLagMs")] public int QueueLagMs { get; set; }
    }

    public class Spo2Data
    {
        // Experimental uncalibrated result. Never evidence of clinical safety.
        [JsonProperty("timestamp")] public DateTime TimestampUtc { get; set; }
        [JsonProperty("deviceTimeMs")] public long DeviceTimeMs { get; set; }
        [JsonProperty("spo2Percent")] public double? Spo2Percent { get; set; }
        [JsonProperty("ratioOfRatios")] public double? RatioOfRatios { get; set; }
        [JsonProperty("isDemonstration")] public bool IsDemonstration { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("contactConfirmed")] public bool ContactConfirmed { get; set; }
        [JsonProperty("g1Approved")] public bool G1Approved { get; set; }
        [JsonProperty("queueLagMs")] public int QueueLagMs { get; set; }
    }

    public class PpgRawData
    {
        // Timestamp of Unity dispatch. deviceTimeMs is the acquisition clock.
        [JsonProperty("timestamp")] public DateTime TimestampUtc { get; set; }
        [JsonProperty("deviceTimeMs")] public long DeviceTimeMs { get; set; }
        [JsonProperty("seq")] public int Sequence { get; set; }
        [JsonProperty("red")] public int Red { get; set; }
        [JsonProperty("ir")] public int Ir { get; set; }
        [JsonProperty("contact")] public bool? Contact { get; set; }
        [JsonProperty("sqiReason")] public int? SqiReason { get; set; }
        [JsonProperty("queueLagMs")] public int QueueLagMs { get; set; }
    }
}
