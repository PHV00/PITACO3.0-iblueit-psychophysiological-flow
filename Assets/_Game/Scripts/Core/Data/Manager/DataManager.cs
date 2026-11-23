using System.Collections.Generic;
using System.Threading.Tasks;
using Assets._Game.Scripts.Core.Api;
using Assets._Game.Scripts.Core.Api.Dto;
using Assets._Game.Scripts.Core.Configuration;
using UnityEngine;

namespace Ibit.Core.Data.Manager
{
    public sealed class DataManager
    {
        // PITACO 3.0 end-to-end TEST branch: backend schema deployed to Azure.
        // Production rollout still requires confirmation of the Atlas unique index
        // and an actual stage POST/GET; no clinical use of SpO2 DEMO.
        public const bool Pitaco3BackendReady = true;
        public const string Pitaco3DeviceName = "Pitaco 3.0";

        public static DataManager Instance { get; } = new DataManager();

        static DataManager() { }

        private DataManager() { }

        public async Task<DataManagerReponse<PacientDto, PacientSendDto>> SavePacient(PacientSendDto pacient)
        {
            var response = new DataManagerReponse<PacientDto, PacientSendDto>();
            
            //LocalDataManager.Instance.SaveLocalData("Pacient", pacient, Pacient.Loaded.Name); // BUG

            if (!ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
                return response;

            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("Pacient", pacient, Pacient.Loaded.IdApi);
                response.LocalResponse = pacient;
                return response;
            }

            response.ApiResponse = await ApiClient.Instance.SavePacient(pacient);

            return response;
        }

        public async Task<DataManagerReponse<PacientDto, PacientSendDto>> UpdatePacient(PacientSendDto pacient)
        {
            var response = new DataManagerReponse<PacientDto, PacientSendDto>();

            LocalDataManager.Instance.SaveLocalData("Pacient", pacient, Pacient.Loaded.Name);

            if (!ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
                return response;

            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("Pacient", pacient, Pacient.Loaded.IdApi);
                response.LocalResponse = pacient;
                return response;
            }

            response.ApiResponse = await ApiClient.Instance.UpdatePacient(pacient, Pacient.Loaded.IdApi);

            return response;
        }

        public async Task<DataManagerReponse<PlataformOverviewDto, PlataformOverviewSendDto>> SavePlataformOverview(PlataformOverviewSendDto plataform)
        {
            var response = new DataManagerReponse<PlataformOverviewDto, PlataformOverviewSendDto>();

            bool locallySaved = LocalDataManager.Instance.SaveLocalData(
                "PlataformOverview", plataform, Pacient.Loaded.Name);
            if (!locallySaved)
                Debug.LogError("[PITACO3-E2E] Falha ao salvar JSON local da etapa.");

            bool hasPitaco3 = plataform.FlowDataDevices != null &&
                plataform.FlowDataDevices.Exists(device =>
                    device != null && device.DeviceName == Pitaco3DeviceName);

            if (!ConfigurationManager.SendRemoteData ||
                string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
            {
                if (hasPitaco3)
                    Debug.LogWarning("[PITACO3-E2E] Envio remoto desabilitado ou GameApiToken vazio. " +
                        "Verifique gameSendRemoteData e gameApiToken. JSON local permanece salvo.");
                return response;
            }

            if (hasPitaco3 && !Pitaco3BackendReady)
            {
                bool queued = LocalDataManager.Instance.SaveRemoteData(
                    "PlataformOverview", plataform, Pacient.Loaded.IdApi);
                if (queued) response.LocalResponse = plataform;
                else Debug.LogError("[PITACO3-E2E] Falha ao preparar pendencia remota.");
                Debug.LogWarning("[PITACO3-E2E] API ainda nao liberada neste build.");
                return response;
            }

            if (hasPitaco3)
            {
                // For the E2E test, POST directly to our Azure API.
                // A separate HTTP google.com probe could fail even when Azure
                // is reachable, silently bypassing the test.
                var api = await ApiClient.Instance.SavePlataformOverview(plataform);
                if (api != null && api.Success)
                {
                    response.ApiResponse = api;
                    Debug.Log("[PITACO3-E2E] REMOTE_OK run=" + plataform.ClientRunId +
                              " | dados aceitos pela API. Conferir no Atlas.");
                }
                else
                {
                    bool queued = LocalDataManager.Instance.SaveRemoteData(
                        "PlataformOverview", plataform, Pacient.Loaded.IdApi);
                    if (queued) response.LocalResponse = plataform;
                    Debug.LogError("[PITACO3-E2E] REMOTE_FAIL run=" + plataform.ClientRunId +
                                   " | copia local mantida; pendencia=" + queued);
                }
                return response;
            }

            // The legacy devices retain the previous remote-save policy.
            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("PlataformOverview", plataform, Pacient.Loaded.IdApi);
                response.LocalResponse = plataform;
                return response;
            }

            response.ApiResponse = await ApiClient.Instance.SavePlataformOverview(plataform);
            return response;
        }

        public async Task<DataManagerReponse<CalibrationOverviewDto, CalibrationOverviewSendDto>> SaveCalibrationOverview(CalibrationOverviewSendDto calibration)
        {
            var response = new DataManagerReponse<CalibrationOverviewDto, CalibrationOverviewSendDto>();

            LocalDataManager.Instance.SaveLocalData("CalibrationOverview", calibration, Pacient.Loaded.Name);

            if (!ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
                return response;

            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("CalibrationOverview", calibration, Pacient.Loaded.IdApi);
                response.LocalResponse = calibration;
                return response;
            }

            response.ApiResponse = await ApiClient.Instance.SaveCalibrationOverview(calibration);

            return response;
        }

        public async Task<DataManagerReponse<MinigameOverviewDto, MinigameOverviewSendDto>> SaveMinigameOverview(MinigameOverviewSendDto minigame)
        {
            var response = new DataManagerReponse<MinigameOverviewDto, MinigameOverviewSendDto>();

            LocalDataManager.Instance.SaveLocalData("MinigameOverview", minigame, Pacient.Loaded.Name);

            if (!ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
                return response;

            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("MinigameOverview", minigame, Pacient.Loaded.IdApi);
                response.LocalResponse = minigame;
                return response;
            }
            response.ApiResponse = await ApiClient.Instance.SaveMinigameOverview(minigame);

            return response;
        }

        public async Task<ApiResponse<List<PacientDto>>> GetPacients()
        {
            if (!await ApiClient.Instance.HasInternetConnection() || !ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
            {
                return new ApiResponse<List<PacientDto>>
                {
                    Success = false,
                    Data = LocalDataManager.Instance.GetPacientsLocal()
                };
            }
            return await ApiClient.Instance.GetPacients();
        }

        public async Task<ApiResponse<List<StageDto>>> GetGameparameter(string IdApi)
        {
            if (!await ApiClient.Instance.HasInternetConnection() || !ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
            {
                return new ApiResponse<List<StageDto>>
                {
                    Success = false,
                    //Data = LocalDataManager.Instance.GetGameparameterLocal()
                };
            }
            return await ApiClient.Instance.GetGameparameter(IdApi);
        }

        public async Task<DataManagerReponse<StageDto, StageSendDto>> UpdateGameparameter(StageSendDto stage)
        {
            var response = new DataManagerReponse<StageDto, StageSendDto>();

            LocalDataManager.Instance.SaveLocalData("Stage", stage, Pacient.Loaded.IdApi);

            if (!ConfigurationManager.SendRemoteData || string.IsNullOrWhiteSpace(ConfigurationManager.GameApiToken))
                return response;

            var hasInternetConnection = await ApiClient.Instance.HasInternetConnection();
            if (!hasInternetConnection)
            {
                LocalDataManager.Instance.SaveRemoteData("Stage", stage, Pacient.Loaded.IdApi);
                response.LocalResponse = stage;
                return response;
            }

            response.ApiResponse = await ApiClient.Instance.UpdateGameparameter(stage, Pacient.Loaded.IdApi);

            return response;
        }

        public async Task<bool> SendRemoteData()
        {
            return await RemoteDataManager.Instance.FlushRemoteData();
        }

    }
}