#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Ibit.Core.Data;
using Ibit.Core.Oximetry;
using Ibit.Core.Oximetry.Integration;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// Tools > PITACO > Run PITACO 3.0 stage capture smoke tests
// No hardware, serial port, Atlas credentials, or Play Mode is required.
// This exercises the capture contract without changing Mixer/DeepDDA behavior.
public static class Pitaco3StageCaptureSmokeTests
{
    private static int passed;

    [MenuItem("Tools/PITACO/Run PITACO 3.0 stage capture smoke tests")]
    public static void Run()
    {
        passed = 0;
        var go = new GameObject("PITACO3 capture smoke test");
        try
        {
            var recorder = go.AddComponent<Pitaco3DataRecorder>();
            recorder.StartCapture();
            string firstRun = recorder.ClientRunId;
            Guid id;
            Check("stage has a stable unique ID", Guid.TryParse(firstRun, out id));

            var onRaw = typeof(Pitaco3DataRecorder).GetMethod(
                "RecordRaw", BindingFlags.Instance | BindingFlags.NonPublic);
            var onMeasurement = typeof(Pitaco3DataRecorder).GetMethod(
                "RecordMeasurement", BindingFlags.Instance | BindingFlags.NonPublic);
            Check("raw and estimate callbacks available",
                onRaw != null && onMeasurement != null);

            onRaw.Invoke(recorder, new object[] {
                new PpgRawSample(17, 12000, 72000, 84000, true, 0)
            });
            onRaw.Invoke(recorder, new object[] {
                new PpgRawSample(18, 12040, 71000, 83500, null, null)
            });
            onMeasurement.Invoke(recorder, new object[] {
                new PpgMeasurement(12040, 25.0, 76.4, 96.1, 0.72, 0.83,
                    "EXPERIMENTAL - NAO CALIBRADO")
            });
            // SpO2 rejected, BPM still available. Never fill from an old HUD.
            onMeasurement.Invoke(recorder, new object[] {
                new PpgMeasurement(12440, 25.0, 77.1, null, null, 0.84,
                    "EXPERIMENTAL - SEM SPO2")
            });
            recorder.StopCapture();

            var source = new FlowDataDevice {
                DeviceName = "Pitaco",
                FlowData = new List<FlowData> {
                    new FlowData { Date = DateTime.UtcNow, Value = 5f }
                }
            };
            var snapshot = recorder.BuildDeviceSnapshot(source);
            Check("modern device identified separately",
                snapshot.DeviceName == "Pitaco 3.0" && source.DeviceName == "Pitaco");
            Check("respiratory data preserved", snapshot.FlowData.Count == 1);
            Check("one record per estimator publication per metric",
                snapshot.BpmData.Count == 2 && snapshot.Spo2Data.Count == 2);
            Check("rejected saturation remains null",
                !snapshot.Spo2Data[1].Spo2Percent.HasValue &&
                 snapshot.BpmData[1].Bpm.HasValue);
            Check("experimental SpO2 never mistaken for calibrated",
                snapshot.Spo2Data[0].IsDemonstration);
            Check("raw data includes failed-contact observations",
                snapshot.PpgRawData.Count == 2 &&
                !snapshot.PpgRawData[1].Contact.HasValue);
            Check("raw device clock and RED/IR are preserved",
                snapshot.PpgRawData[0].DeviceTimeMs == 12000 &&
                snapshot.PpgRawData[0].Red == 72000 &&
                snapshot.PpgRawData[0].Ir == 84000);

            string json = JsonConvert.SerializeObject(snapshot);
            var roundTrip = JsonConvert.DeserializeObject<FlowDataDevice>(json);
            Check("four arrays survive JSON round trip",
                roundTrip.FlowData.Count == 1 &&
                roundTrip.BpmData.Count == 2 &&
                roundTrip.Spo2Data.Count == 2 &&
                roundTrip.PpgRawData.Count == 2);
            Check("legacy device omits the new optional arrays",
                !JsonConvert.SerializeObject(source).Contains("\"bpmData\"") &&
                !JsonConvert.SerializeObject(source).Contains("\"ppgRawData\""));
            Check("remote upload is disabled until backend rollout",
                !Ibit.Core.Data.Manager.DataManager.Pitaco3BackendReady);

            recorder.StartCapture();
            var second = recorder.BuildDeviceSnapshot(source);
            Check("new stage starts with cleared optical arrays",
                second.BpmData.Count == 0 &&
                second.Spo2Data.Count == 0 &&
                second.PpgRawData.Count == 0 &&
                second.FlowData.Count == 1);
            Check("new stage gets a different run ID",
                recorder.ClientRunId != firstRun);
            recorder.StopCapture();

            Debug.Log("[PITACO3-TEST] PASS: " + passed +
                " stage capture assertions (Edit Mode, no hardware).");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void Check(string name, bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("[PITACO3-TEST] FAIL: " + name);
        ++passed;
    }
}
#endif
