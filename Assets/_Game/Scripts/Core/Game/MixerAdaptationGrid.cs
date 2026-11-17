using Ibit.Core.Database;
using Ibit.Core.Data;
using Ibit.Plataform;
using Ibit.Plataform.UI;
using Ibit.Plataform.Manager.Spawn;
using Ibit.Plataform.Data;
using Ibit.Core.Oximetry.Integration;
using UnityEngine;

namespace Ibit.Core.Game
{
    public partial class MixerManager
    {
        private float SignalSamplesRateOxi;
        private float timeBetweenSamplesOxi;
        private float currenttimeOxi;
        private float minExtBelt;
        private float maxExpBeltPacient;
        private float maxInsBeltPacient;
        private GameObject Dolphin;

        int limitSPORegular = 2; // Número de leituras regulares para fazer adaptação
        int limitSPODanger = 2; // Número de leituras perigosas para fazer adaptação
        int countSPORegular = 0; // Contador de leituras com Oxigenação Regular
        int countSPODanger = 0; // Contador de leituras com Oxigenação Perigosa

        bool SecurityPanelOneExecuted;
        bool SecurityPanelTwoExecuted;

        [SerializeField] private CanvasManager _canvasManager;
        [SerializeField] private Spawner _spawner;

        // TEST BRANCH ONLY: the existing configuration selects profile P for
        // PITACO 2.4 with an integrated optical sensor. Profile P has 0/0
        // saturation thresholds. Match only the SpO2 thresholds of the PO
        // test profile without changing any other P gameplay parameter.
        // These are functional DEMO thresholds, NOT validated clinical limits.
        private const int Pitaco24TestMinimumNormalOxygenation = 95;
        private const int Pitaco24TestMinimumRegularOxygenation = 89;

        private static bool UsePitaco24TestOxygenationThresholds(OximetrySnapshot snapshot)
        {
            return snapshot.Source == OximetrySource.Pitaco24;
        }

        private static int MinimumRegularOxygenationFor(OximetrySnapshot snapshot)
        {
            return UsePitaco24TestOxygenationThresholds(snapshot)
                ? Pitaco24TestMinimumRegularOxygenation
                : ParametersDb.parameters.MinimumRegularOxygenation;
        }

        private static int MinimumNormalOxygenationFor(OximetrySnapshot snapshot)
        {
            return UsePitaco24TestOxygenationThresholds(snapshot)
                ? Pitaco24TestMinimumNormalOxygenation
                : ParametersDb.parameters.MinimumNormalOxygenation;
        }

        // Logging for functional integration only. Enabled in this test branch;
        // no extra serial traffic and no changes to legacy oximeter thresholds.
        [SerializeField] private bool logSpo2AdaptationTest = true;
        private bool? lastSpo2EligibleForTest;
        private float nextNoTargetsDiagnosticAt;

        // Existing gameplay returns without advancing its sample clock when
        // no targets exist; throttle this diagnostic to avoid per-frame spam.
        private void TraceNoTargets(OximetrySnapshot snapshot, string eventName)
        {
            if (!logSpo2AdaptationTest ||
                Time.unscaledTime < nextNoTargetsDiagnosticAt)
                return;
            nextNoTargetsDiagnosticAt = Time.unscaledTime + 5f;
            TraceSpo2AdaptationTest(snapshot, eventName);
        }

        private void TraceSpo2AdaptationTest(OximetrySnapshot snapshot, string eventName)
        {
            if (!logSpo2AdaptationTest)
                return;

            Debug.Log("[SpO2Grid-Test] event=" + eventName +
                " | source=" + snapshot.Source +
                " | spo2=" + (snapshot.SpO2Percent.HasValue
                    ? snapshot.SpO2Percent.Value.ToString("F1") : "--") +
                " | demo=" + snapshot.SpO2IsDemonstration +
                " | legacyRules=" + snapshot.EligibleForLegacySpo2Rules +
                " | contato=" + snapshot.OpticalContactConfirmed +
                " | G1=" + snapshot.BasicSignalQualityApproved +
                " | age=" + snapshot.AgeSeconds.ToString("F1") + "s" +
                " | perfilLimiar=" + (UsePitaco24TestOxygenationThresholds(snapshot)
                    ? "Pitaco24Test" : "LegadoConfig") +
                " | limiarRegular=" + MinimumRegularOxygenationFor(snapshot) +
                " | limiarNormal=" + MinimumNormalOxygenationFor(snapshot) +
                " | contadorRegular=" + countSPORegular + "/" + limitSPORegular +
                " | contadorPerigo=" + countSPODanger + "/" + limitSPODanger +
                " | painel1Executado=" + SecurityPanelOneExecuted +
                " | painel2Executado=" + SecurityPanelTwoExecuted +
                " | intervaloAvaliacao=" + timeBetweenSamplesOxi.ToString("F1") + "s" +
                " | tempoJogo=" + Time.time.ToString("F1") + "s");
        }

        private void OnEnable()
        {
            // Variáveis para taxa de amostragem, que é o número de leituras usadas no jogo por minuto do Oxímetro (Tratamento de Sinais)
            SignalSamplesRateOxi = SignalTreatmentDb.LoadSignalParameters("O");
            timeBetweenSamplesOxi = 60 / SignalSamplesRateOxi;  // 60 segundos / amostragem desejada = tempo de intervalo entre cada amostra do sinal
            
            // Variáveis para alterar a cor do Blue quando abaixo da porcentagem mínima de uso da cinta
            Dolphin = GameObject.FindWithTag("Dolphin");
            minExtBelt = ParametersDb.parameters.MinimumExtensionBelt;
            maxExpBeltPacient = Pacient.Loaded.CapacitiesCinta.ExpPeakFlow;
            maxInsBeltPacient = Pacient.Loaded.CapacitiesCinta.InsPeakFlow;

            SecurityPanelOneExecuted = false;
            SecurityPanelTwoExecuted = false;
            lastSpo2EligibleForTest = null;
            nextNoTargetsDiagnosticAt = 0f;
        }

        void AdaptationGrid(float signalPitaco, float signalMano,
            float signalCinta, OximetrySnapshot oximetry)
        {
            if (_spawner == null){
                _spawner = FindObjectOfType<Spawner>();
            }

            // if (_spawner.SpawnedObjects.Count < 1) // Se não tiver objetos, os testes não são feitos
            //     return;

            //* AvaliationNode Oxímeter - DeepDDA: Monitoramento dos biosinais e feedback (Reward)
            // Integration-test branch: the original saturation alerts, pauses
            // and rewards are also exercised with eligible PITACO 2.4 DEMO data.
            // DEMO is uncalibrated: do not use these alerts for patient safety.
            bool eligible = oximetry.EligibleForLegacySpo2Rules &&
                oximetry.SpO2Percent.HasValue;
            if (!lastSpo2EligibleForTest.HasValue ||
                lastSpo2EligibleForTest.Value != eligible)
            {
                TraceSpo2AdaptationTest(oximetry,
                    eligible ? "ESTADO_ELEGIVEL" : "ESTADO_INELEGIVEL");
                lastSpo2EligibleForTest = eligible;
            }
            if (!eligible)
            {
                countSPORegular = 0;
                countSPODanger = 0;
            }
            else if (Time.time > currenttimeOxi + timeBetweenSamplesOxi)
            {
                float signalOxiSPO = (float)oximetry.SpO2Percent.Value;
                int minimumRegular = MinimumRegularOxygenationFor(oximetry);
                int minimumNormal = MinimumNormalOxygenationFor(oximetry);
                if (signalOxiSPO >= minimumRegular && signalOxiSPO < minimumNormal) // Faixa regular de teste PITACO 2.4: 89 <= SpO2 < 95    
                {
                    if (_spawner.SpawnedObjects.Count < 1) // Se não tiver objetos, os testes não seguem
                    {
                        TraceNoTargets(oximetry, "AMOSTRA_REGULAR_IGNORADA_SEM_ALVOS");
                        return;
                    }

                    countSPORegular += 1;
                    TraceSpo2AdaptationTest(oximetry, "AMOSTRA_REGULAR");
                    if (countSPORegular >= limitSPORegular && SecurityPanelOneExecuted == false) // Só dispara o "Security Panel 1" uma única vez durante o nível
                    {
                        GameObject.Find("Canvas").transform.Find("Security Panel 1").gameObject.SetActive(true);
                        if (_canvasManager == null){
                            _canvasManager = FindObjectOfType<CanvasManager>();
                        }
                        _canvasManager.PauseGametoShowAlert(); // Pausa jogo
                        AddReward(-1f);
                        SecurityPanelOneExecuted = true;
                        TraceSpo2AdaptationTest(oximetry, "PAINEL_1_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA");

                        // if (_spawner == null){
                        //     _spawner = FindObjectOfType<Spawner>();
                        // }

                        if ((StageModel.Loaded.ObjectSpeedFactor * ParametersDb.parameters.ObjectsSpeedFactor) >= 2){
                            // _spawner.speedReductionAdaptation = 1;  
                            DecreaseSpeed(); // Diminuir velocidade em um nível
                            TraceSpo2AdaptationTest(oximetry, "PAINEL_1_REDUCAO_VELOCIDADE_SOLICITADA");
                        }

                    }
                } else {
                    if (signalOxiSPO > 0 && signalOxiSPO < minimumRegular)
                    {
                        if (_spawner.SpawnedObjects.Count < 1) // Se não tiver objetos, os testes não seguem
                        {
                            TraceNoTargets(oximetry, "AMOSTRA_PERIGO_IGNORADA_SEM_ALVOS");
                            return;
                        }

                        countSPODanger += 1;
                        TraceSpo2AdaptationTest(oximetry, "AMOSTRA_PERIGO");
                        if (countSPODanger >= limitSPODanger && SecurityPanelTwoExecuted == false)  // Só dispara o "Security Panel 2" uma única vez durante o nível
                        {
                            GameObject.Find("Canvas").transform.Find("Security Panel 2").gameObject.SetActive(true);
                            if (_canvasManager == null){
                                _canvasManager = FindObjectOfType<CanvasManager>();
                            }
                            _canvasManager.PauseGametoShowAlert(); // Pausa jogo
                            AddReward(-2f);
                            SecurityPanelTwoExecuted = true;
                            TraceSpo2AdaptationTest(oximetry, "PAINEL_2_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA");
                        }
                    } else {
                        countSPORegular = 0;
                        countSPODanger = 0;
                        AddReward(0.1f);
                        TraceSpo2AdaptationTest(oximetry, "AMOSTRA_NORMAL_RECOMPENSA_SOLICITADA");

                        // if (_spawner == null){
                        //     _spawner = FindObjectOfType<Spawner>();
                        // }
                        // _spawner.speedReductionAdaptation = 1;  // Volta velocidade ao normal
                    }
                }
                currenttimeOxi = Time.time; // currenttimeOxi é atualizado para o tempo atual (Time.time é um contador de segundos que começa quando o jogo é executado)
            }


            //* AvaliationNode Extensor Belt
            if (scc.IsConnected && minExtBelt > 0)  // Se o mínimo exigido da cinta é maior qur zero
            {
                // Debug.Log($"maxExpBeltPacient: {maxExpBeltPacient}");
                // Debug.Log($"maxInsBeltPacient: {maxInsBeltPacient}");
                if ((signalCinta < (minExtBelt/100) * maxExpBeltPacient) && (signalCinta > (minExtBelt/100) * maxInsBeltPacient)) //ParametersDb.parameters.MinimumExtensionBelt
                {
                    Dolphin.GetComponent<SpriteRenderer>().color = new Color(1.0f, 0.640f, 0.0f, 1.0f); // Laranja       new Color(0.912f, 0.480f, 0.204f, 1.0f)
                } else {
                    Dolphin.GetComponent<SpriteRenderer>().color = new Color(1.0f, 1.0f, 1.0f, 1.0f); // Cor natural
                }
            }
            




            //* FlowNode
        }

        private void DecreaseSpeed()
        {
            if(_spawner != null && _spawner.SpawnedObjects != null)
            {
                foreach (var obj in _spawner.SpawnedObjects)
                {
                    var moveObject = obj?.GetComponent<MoveObject>();
                    if(moveObject != null)
                        moveObject.Speed -= 1.00f;
                }
            }
            else
            {
                Debug.LogError("DecreaseSpeed chamado, mas _spawner é nulo ou SpawnedObjects é nulo.");
            }
        }

        // DeepDDA: Método para adicionar Reward ao agente
        private void AddReward(float reward)
        {
            var agent = FindObjectOfType<DeepDDAAgent>();
            if (agent != null)
            {
                agent.AddReward(reward);
                TraceSpo2AdaptationTest(CurrentLegacyRulesOximetry,
                    "RECOMPENSA_APLICADA_" + reward.ToString("F1"));
            }
            else
            {
                TraceSpo2AdaptationTest(CurrentLegacyRulesOximetry,
                    "RECOMPENSA_NAO_APLICADA_AGENTE_AUSENTE_" + reward.ToString("F1"));
            }
        }

        // void AvaliationNode()
        // {

        //     //if()
        // }

        // void FlowNode()
        // {

        // }
    }
}