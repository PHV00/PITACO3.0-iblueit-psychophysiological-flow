# PITACO 3.0 — teste end-to-end Unity → Azure Functions → MongoDB Atlas

## Estado de teste
- Unity: branch `feature/pitaco3-unity-oximetry-persistence`.
- Backend: `PHV00/iblueit-server-side`, `main` (deploy Azure Functions aprovado no GitHub Actions).
- A flag `DataManager.Pitaco3BackendReady` foi ativada somente na branch Unity.
- Não fazer merge automático da Unity na main antes da validação em editor, hardware e Atlas.
- Usar **paciente de teste**: a finalização da fase também modifica dados do paciente e os parâmetros do jogo.
- SpO2 permanece experimental/DEMO, sem calibração clínica e sem alimentar Mixer/DeepDDA.

## Pré-requisitos
1. No Atlas, confira o índice único parcial na coleção `plataformoverviews`:
   `unique_plataform_stage_run_by_token`, chaves `{ _gameToken: 1, clientRunId: 1 }`,
   `unique: true`, filtro parcial `{ clientRunId: { $type: "string" } }`.
   O deploy do Mongoose **não comprova** que o índice existe. Não faça operações
   concorrentes de reenvio até confirmar.
2. No jogo, configure `gameSendRemoteData=true`, `gameApiToken` com token de
   teste válido e `apiEndpoint` com a URL base real da API Azure, **incluindo
   /api**, sem /plataforms. Confira a URL no serviço Azure, sem presumir host.
   O `ApiClient` usa `POST {apiEndpoint}/plataforms`.
3. Selecione um paciente com `Pacient.Loaded.IdApi` registrado no servidor.
4. Feche Serial Monitor/Arduino IDE e outros programas que monopolizem a porta.
   Ligue um único PITACO com firmware que publique RED/IR via MAX30102.
5. Tenha conexão com a internet e a API implantada na main do backend.

## Execução Unity Editor (Linux ou Windows)

No clone Unity, selecione:

```bash
git fetch origin
git switch feature/pitaco3-unity-oximetry-persistence
git pull --ff-only origin feature/pitaco3-unity-oximetry-persistence
```

No Unity Editor, primeiro confirme **Console sem erros de compilação**.
Execute `Tools > PITACO > Run PITACO 3.0 stage capture smoke tests`.
Critério: `[PITACO3-TEST] PASS`. O smoke test é offline/sintético,
não comprova conexão com hardware ou Atlas.

Depois, no Play Mode:
1. Conecte a porta COM/USB do PITACO 3.0 e confirme a identificação do
   protocolo moderno (PITACO 2.4 no `PitacoConnectionManager`).
2. Entre na fase/plataforma com paciente de teste.
3. Execute a fase por tempo suficiente para acumular amostras.
4. Finalize a fase e **confirme a escala de Borg**.
5. Aguarde finalizar a tela de salvamento e abra o Console.
6. Não use simultaneamente o oxímetro externo legado ou outros sensores
   que o backend não reconheça (há validação de `deviceName`).

### Logs esperados no Console

```text
[PITACO3-RECORD] run=<RUN_ID> | bpm=... | spo2=... | raw=... | rawOmitted=...
[PITACO3-E2E] STAGE_SNAPSHOT run=<RUN_ID> | flow=... | bpm=... | spo2=... | raw=...
[PITACO3-E2E] POST https://<HOST>/api/plataforms | run=<RUN_ID> | bytes=...
[PITACO3-E2E] HTTP 201 | API success=true | run=<RUN_ID>
[PITACO3-E2E] REMOTE_OK run=<RUN_ID> | dados aceitos pela API. Conferir no Atlas.
[PITACO3-E2E] STAGE_REMOTE_OK run=<RUN_ID>
```

O HTTP **200** também é sucesso quando `clientRunId` já existe e a
identidade da etapa é a mesma (idempotência). `HTTP 201` significa
uma nova gravação. `STAGE_REMOTE_OK` confirma o aceite da API,
**não** comprova por si só a leitura no MongoDB.

### Onde encontrar o JSON local

```text
<RaizUnity>/Data/LocalData/Pacients/<NomeDoPacienteSemEspacos>/PlataformsData/
```

Procure o arquivo mais recente `info_YYYYMMDD_HHmmss_fff_<clientRunId>.json`.
Ele deve ter `flowDataDevices` e `clientRunId`, diferente do JSON
`Stage` que contém apenas parâmetros da fase.

Se o POST falhar, uma cópia pendente é preparada em:

```text
<RaizUnity>/Data/LocalData/Pacients/<pacientId>/PlataformsData/
```

**Nunca apague o JSON local antes da comparação com o Atlas**.

## Conferência no MongoDB Atlas

No Data Explorer (ou mongosh **conectado ao banco da API**):

```javascript
const stage = db.plataformoverviews.findOne(
  { clientRunId: "<RUN_ID>" },
  { _id: 1, clientRunId: 1, devices: 1, flowDataDevicesId: 1 }
);
printjson(stage);
```

Deve existir um documento com `devices` incluindo `Pitaco 3.0`.
Depois:

```javascript
const signals = db.flowdatadevices.findOne({ _id: stage.flowDataDevicesId });
printjson(signals.flowDataDevices.map(d => ({
  deviceName: d.deviceName,
  flow: (d.flowData || []).length,
  bpm: (d.bpmData || []).length,
  spo2: (d.spo2Data || []).length,
  raw: (d.ppgRawData || []).length,
  rawSamplesOmitted: d.rawSamplesOmitted
})));
```

Confira que o documento possui RED/IR genuínos e timestamps coerentes.
`raw > 0` valida que amostras ópticas chegaram ao gravador; as séries
de estimativas podem estar vazias se os filtros não publicaram medidas.
`spo2Percent=null` é esperado para leituras rejeitadas.

Para uma execução nova, confira exatamente **um** `plataformoverview`
com `clientRunId` informado:

```javascript
db.plataformoverviews.countDocuments({ clientRunId: "<RUN_ID>" })
```

## Diagnóstico de falhas

- `[PITACO3-E2E] Envio remoto desabilitado...`: configure
  `gameSendRemoteData` e `gameApiToken`.
- Não há `STAGE_SNAPSHOT`: fase não finalizou/Borg não foi confirmado,
  ou a Unity não identificou o PITACO moderno.
- `raw=0`: conferir porta, firmware, telemetria e observador PPG;
  mesmo com POST aceito, o objetivo de coleta RED/IR não foi comprovado.
- `HTTP 403`: token inválido; conferir `gameApiToken`.
- `HTTP 400`: API rejeitou o payload. Console mostra erro resumido,
  verificar `result`, dispositivos conectados e campos das séries.
- `HTTP 413`: JSON excedeu limite de 9 MiB; reduzir duração/captura.
- `HTTP 409`: `clientRunId` reutilizado para outra etapa.
- `HTTP 500`: conferir logs da Azure Function e conexão Atlas.
- `POST NETWORK_ERROR` / `POST TIMEOUT`: conexão/URL, não equivale a
  falha do sensor. A Unity mantém JSON local e tenta salvar pendência.
- `STAGE_REMOTE_FAIL`: não considerar teste aprovado; localizar JSON.
- `STAGE_REMOTE_OK` mas sem Atlas: conferir banco/cluster da string
  `MongoDbAtlas` no ambiente da Azure.

## Critérios para aprovar E2E

1. Editor compilou e smoke tests passaram.
2. PITACO conectado gerou `raw > 0`, `red`, `ir` e `deviceTimeMs`.
3. JSON local contém `clientRunId`, `result` texto e quatro arrays.
4. API devolveu HTTP 201 ou HTTP 200 e `success=true`.
5. Atlas contém o overview e seu `flowDataDevicesId` correto.
6. Atlas contém o mesmo volume de amostras do JSON local.
7. Índice único por token e `clientRunId` foi confirmado.
8. Um segundo teste cria outro `clientRunId` e não mistura séries.

Após comprovar esses critérios, discutir merge da Unity.
