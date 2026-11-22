# PITACO 3.0 — fase 1: captura na Unity (sem alterações da IA)

Branch: **feature/pitaco3-unity-oximetry-persistence**, originada de **test/unity-ppg-monotonic-clock-alignment**.

## Escopo

Esta etapa implementa **apenas captura, DTO e preservação local**. O backend
iblueit-server-side ainda **não aceita** o identificador "Pitaco 3.0" nem
persiste as séries ópticas no MongoDB Atlas. Em DataManager.cs,
Pitaco3BackendReady permanece **false**, impedindo o POST da nova etapa.
O reenvio das pendências também é bloqueado até o rollout do backend.

Mixer, DeepDDA, regras de pausa/recompensa e estimadores estão inalterados.
Nenhuma segunda porta USB/COM foi criada.

## Fluxo da captura

1. PitacoConnectionManager distribui o protocolo serial existente.
2. PpgProcessingCoordinator publica RED/IR em OnRawSample, antes dos filtros
   de contato/G1. O novo evento é apenas observacional.
3. PpgProcessingCoordinator.OnMeasurement fornece estimativas processadas
   de BPM e SpO2 DEMO, inclusive valores individuais ausentes.
4. PlataformLogger inicia Pitaco3DataRecorder a partir do evento
   StageManager.OnStageStart e finaliza junto ao StopLogging da etapa.
   Uma reconexão que dispare StartStage novamente não reinicia a captura.
5. Quando o PITACO moderno foi identificado, PlataformLogger cria um
   FlowDataDevice com deviceName "Pitaco 3.0", contendo:
   - flowData[] — série respiratória legada;
   - bpmData[] — publicações BPM, com null quando não estimado;
   - spo2Data[] — publicações SpO2, com null quando não estimado,
     isDemonstration explícito;
   - ppgRawData[] — RED/IR recebidos, opcionalmente habilitados.
6. PlataformOverviewSendDto contém clientRunId estável por etapa.
7. DataManager salva JSON local e, com envio remoto configurado,
   deixa uma cópia pendente para a próxima fase.

Registros antigos permanecem identificados como "Pitaco". Não foi feita
migração de documentos anteriores.

## Semântica dos timestamps e qualidade

- timestamp em ppgRawData: instante UTC em que a Unity recebeu/despachou
  a mensagem, **não o instante exato de aquisição ADC**.
- timestamp em bpmData/spo2Data: publicação do estimador na Unity.
- deviceTimeMs: tempo monotônico do firmware relativo ao dispositivo.
- seq: sequência original da amostra do firmware.
- contact e sqiReason: campos ópticos informados pelo firmware;
  ficam null quando o frame não traz o objeto SQI.
- g1Approved: estado observado no momento da publicação;
  não é uma segunda aprovação da qualidade da amostra.
- Não preencher lacunas com o último valor exibido na HUD.
- SpO2 demonstrativa não é calibrada para uso clínico.

A opção captureRawPpg inicia como true para pesquisa.
maxRawSamplesPerStage inicia em 12000. Registros além desse limite são
contados em rawSamplesOmitted, não descartados silenciosamente.
A quantidade gerada deve ser medida antes do envio real, pois o MongoDB
limita documentos BSON individuais a 16 MiB.

## Onde verificar os arquivos

GameDataPaths.localDataPath:
- Data/LocalData/Pacients/<NomeSemEspacos>/PlataformsData/

Com envio remoto habilitado, cópias pendentes:
- Data/LocalData/Pacients/<pacientId>/PlataformsData/

**Atenção:** atualmente GameDataPaths.remoteDataPath e localDataPath
apontam para o mesmo diretório-base Data/LocalData. A separação existente
é feita pelos subdiretórios do paciente, por nome e ID. Revisar antes
do rollout definitivo.

Os arquivos de etapas utilizam o clientRunId no nome, impedindo
sobrescrita por duas partidas que terminem no mesmo minuto.

## Testes na Unity (ainda não executados em Editor/hardware)

1. Abrir esta branch no Unity Editor, verificar compilação sem erros.
2. Executar Tools > PITACO > Run PITACO 3.0 stage capture smoke tests.
   Este menu usa amostras sintéticas sem porta serial e verifica quatro
   séries, null em SpO2 rejeitada, compatibilidade legada e novo ID por
   etapa. Procurar [PITACO3-TEST] PASS no Console.
3. Executar etapa real com PITACO 3.0, finalizar e confirmar Borg para
   acionar LogPlaySession.
4. Inspecionar o JSON: deviceName "Pitaco 3.0", schemaVersion 1,
   clientRunId, flowData, bpmData, spo2Data, ppgRawData e rawSamplesOmitted.
5. Conferir RED/IR, seq e deviceTimeMs com o log da serial. RAW deve
   preservar amostras com contato falso.
6. Testar G1 inválido/perda de contato: sem fabricar BPM/SpO2.
7. Testar duas etapas: IDs e arquivos distintos; não misturar séries.
8. Testar PITACO legado, Mano e Cinta para regressão.
9. Confirmar que não há POST da etapa "Pitaco 3.0" antes da fase 2,
   e que o arquivo local/pendência permanece disponível.
10. Executar Tools > PITACO > Run PPG critical hardening smoke tests.

## Critérios de aceite da fase 1

- Compilação do projeto no Editor.
- Smoke tests do Editor aprovados.
- JSON local real contém os quatro arrays quando disponíveis.
- Partidas consecutivas não misturam medições.
- Null e identificação DEMO preservados.
- Mixer/DeepDDA permanecem sem alterações.
- Nenhum POST prematuro com schema incompatível.

## Próximas fases (não executadas)

**Fase 2 — Backend:** atualizar shared/FlowDataDevice.js,
shared/Validators.js, SendPlataformOverview/index.js; validar novos campos,
tratar flowData vazio, introduzir idempotência por clientRunId e
verificar tamanho dos arrays RAW. Depois de deploy/testes da API,
habilitar Pitaco3BackendReady no cliente.

**Fase 3 — MongoDB Atlas:** validar dados realmente persistidos,
tipos, vínculo por flowDataDevicesId, recuperação e reenvio seguro.
O modelo Mongoose deve ser atualizado na fase 2; a fase 3 é a
verificação da instância e possíveis índices, sem repetir o schema.
