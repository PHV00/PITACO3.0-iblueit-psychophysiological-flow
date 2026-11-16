using System;
using System.Collections;
using Assets._Game.Scripts.Core.Api;
using Ibit.Core.Data;
using Ibit.Core.Data.Manager;
using Ibit.Core.Util;
using Ibit.Core.Database;
using Ibit.Plataform;
using UnityEngine;
using UnityEngine.UI;
using Ibit.Core.Serial;
using Ibit.Core.Oximetry.Integration;
//using MixerFusion;

namespace Ibit.Core.Game
{
    public partial class MixerManager : MonoBehaviour
    {
        public Parameters CurrentParameters;

        private SerialControllerPitaco scp;
        private SerialControllerMano scm;
        private SerialControllerCinta scc;
        private SerialControllerOximetro sco;
        private OximetryDataBridge oximetryBridge;
        // Inspectable contract. No new ML observations are introduced.
        public OximetrySnapshot CurrentOximetry { get; private set; }
        public OximetrySnapshot CurrentLegacyRulesOximetry { get; private set; }

        void Start()
        {
            if (scp == null)
                scp = FindObjectOfType<SerialControllerPitaco>();
    
            if (scm == null)
                scm = FindObjectOfType<SerialControllerMano>();

            if (scc == null)
                scc = FindObjectOfType<SerialControllerCinta>();
            
            if (sco == null)
                sco = FindObjectOfType<SerialControllerOximetro>();
            oximetryBridge = OximetryDataBridge.Active;

            // DeepDDA: evento
            DeepDDAManager.OnDifficultyAdjustmentSpeed += SpeedAdjustment;
            CurrentParameters = ParametersDb.parameters;
        }

        void Update()
        {
            

            if (scp.IsConnected || scm.IsConnected || scc.IsConnected) // Só executa caso algum dispositivo de controle esteja conectado
            {
                // Consume final computed readings, never RED/IR or a second COM.
                // Existing DeepDDA ONNX, observations, actions and rewards
                // remain unchanged.
                if (oximetryBridge == null)
                    oximetryBridge = OximetryDataBridge.Active;
                CurrentOximetry = oximetryBridge != null
                    ? oximetryBridge.GetSnapshot()
                    : OximetrySnapshotFactory.FromLegacy(
                        sco != null && sco.IsConnected,
                        Player.sensorValueOxiHR, Player.sensorValueOxiSPO, 0f);

                // Preserve the standalone oximeter's historical saturation
                // rules if both devices are present; otherwise the same
                // PITACO snapshot is eligible only if independently calibrated.
                CurrentLegacyRulesOximetry = oximetryBridge != null
                    ? oximetryBridge.GetLegacyRulesSnapshot()
                    : CurrentOximetry;

                FusionManager(Player.sensorValuePitaco, Player.sensorValueMano,
                    Player.sensorValueCinta,
                    CurrentOximetry.HasSpO2
                        ? (float)CurrentOximetry.SpO2Percent.Value : 0f,
                    CurrentOximetry.HasBpm
                        ? (float)CurrentOximetry.Bpm.Value : 0f);
                AdaptationGrid(Player.sensorValuePitaco, Player.sensorValueMano,
                    Player.sensorValueCinta, CurrentLegacyRulesOximetry);
                
                // Debug.Log($"Pitaco: {Player.sensorValuePitaco}");
                // Debug.Log($"Mano: {Player.sensorValueMano}");
                // Debug.Log($"Cinta: {Player.sensorValueCinta}");
                // Debug.Log($"OxímetroManager: {Player.sensorValueOxiSPO}");

                // FusionManager();
                // Debug.Log("Passei FusionManager");
                // AdaptationGrid();
                // Debug.Log("Passei AdaptationGrid");
                // FissionManager();
                // Debug.Log("Passei FissionManager");
            }
        }

        // DeepDDA: Ajuste de Volocidade
        private void SpeedAdjustment(int adjustSpeed)
        {
            if(_spawner != null && _spawner.SpawnedObjects != null)
            {
                foreach (var obj in _spawner.SpawnedObjects)
                {
                    var moveObject = obj?.GetComponent<MoveObject>();
                    if(moveObject != null && adjustSpeed < 0)
                        moveObject.Speed -= 1.00f;
                    else if(moveObject != null && adjustSpeed > 0)
                        moveObject.Speed += 1.00f;
                }
            }
            else
            {
                Debug.LogError("SpeedAdjustment chamado, mas _spawner é nulo ou SpawnedObjects é nulo.");
            }
        }
        
        protected void OnDestroy()
        {
            DeepDDAManager.OnDifficultyAdjustmentSpeed -= SpeedAdjustment;
        } 
    }
}