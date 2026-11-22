# Marco 2 — Fechamento da prova de integracao PITACO 2.4 / Mixer / DeepDDA

**Data do registro:** 17/11/2026
**Status do marco:** FECHADO para a finalidade de **prova funcional experimental de integracao**.
**Repositorio Unity:** PHV00/iblueit-psychophysiological-flow-pitaco2.4-port
**Branch executada/documentada:** `test/pitaco24-spo2-adaptation-grid-logs` (base `feature/ppg-csharp-bpm-spo2-mvp`; SHA da branch antes deste registro: `935e45bbf0c62738cacae1686bdd4adb647e4b06`).
**Firmware consultado:** `PHV00/PITACO_ESP32`, branch `main`, para decodificacao do G1; a versao gravada na placa nao foi identificada independentemente.
**Natureza:** demonstracao tecnica em bancada. **Nao e validacao fisiologica, metrologica ou clinica**. O termo "fechado" NAO significa testes Unity automatizados aprovados, merge na main nem autorizacao de uso com pacientes.

## 1. Objetivo e limite do aceite

Demonstrar a cadeia `PITACO 2.4 -> USB/JSON -> PpgProcessingCoordinator -> OximetryDataBridge -> MixerManager -> MixerAdaptationGrid -> acoes de jogo`, preservando a arquitetura respiratoria do I Blue It e a inferencia existente do DeepDDA.

**Aceite documental, sustentado pelos logs fornecidos pelo usuario:**
- PITACO 2.4 identificado em COM3 exclusiva; stream JSON com crescimento regular da contagem e `descartes da fila: 0` nos registros mostrados.
- Unity recebe callbacks e amostras RED/IR (ultima amostragem detalhada: `JSON serial=3852`, `callbacks=3852`, `samples=3825`, `JSON invalido=0` e `amostraInvalida=0`). Nao ha evidencia nesses registros de perda por parser ou fila.
- `OximetryBridge->Mixer` confirma `source=Pitaco24`, `mixer=RECEBIDO` e leituras validas em periodos de contato/G1 aprovados. Ex.: `bpm=61,8` e `spo2=98,8`; `mixerReads` crescente.
- O Grid experimental reconheceu faixas 0 < SpO2 < 89 (perigo), 89 <= SpO2 < 95 (regular), >=95 (normal), com `perfilLimiar=Pitaco24Test`; limiares **de gameplay experimental**, nao limites clinicos.
- `AMOSTRA_PERIGO` chegou a `2/2`, registrou `RECOMPENSA_APLICADA_-2,0` e `PAINEL_2_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA` no tempo de jogo ~49,1 s; `AMOSTRA_REGULAR` chegou a `2/2`, registrou `RECOMPENSA_APLICADA_-1,0` e `PAINEL_1_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA` em ~84,2 s.
- `AMOSTRA_NORMAL_RECOMPENSA_SOLICITADA` e `RECOMPENSA_APLICADA_0,1` tambem ocorreram. A cadencia de avaliacao observada foi de 6,0 s.
- O agente produziu comandos observaveis de velocidade/carga; `Couldn't connect to trainer... Will perform inference instead` e compativel com uso do modelo em inferencia, nao evidencia treinamento online.

**Interpretacao restrita:** `PAINEL_*_ATIVADO` confirma que o caminho de ativacao foi executado no codigo. Sem captura do estado visual, **nao afirmar** que os paineis foram observados na tela ou que a pausa foi aferida. `RECOMPENSA_APLICADA` confirma a chamada `AddReward`, nao melhora da politica ONNX.

## 2. Arquitetura comprovada e controles

```text
Sensor optico RED/IR + pressao do PITACO 2.4
          | uma COM USB, JSON de PPG
          v
PpgProcessingCoordinator
  contato e G1 (firmware); janela C# 8 s;
  BPM por autocorrelacao; SpO2 DEMO por AC/DC
          |
          v
OximetryDataBridge
  fonte, idade, contato, G1, valores nulos
          |
          v
MixerManager / MixerAdaptationGrid
  teste de limiares e contadores -> painel / pausa / AddReward

DeepDDAAgent: politica ONNX e fluxos existentes de jogo
  observacoes e acoes originais; SpO2 nova nao e observacao nova
```

A `SpO2` integrada continua `demo=True`, calculada por um polinomio de exemplo e SEM calibracao para este sensor, montagem ou populacao. Nesta branch, a constante `OximetrySnapshotFactory.EnableDemoSpo2LegacyRulesForTesting = true` autoriza excepcionalmente os caminhos de gameplay. **Antes de qualquer demonstracao com pacientes, restaurar false e verificar bloqueio em Unity; o modo de teste nao e mecanismo assistencial.**

O oxímetro legado e seus caminhos precisam de regressao especifica; os logs atuais nao demonstram validacao cruzada com ele.

## 3. Problemas encontrados — NAO resolvidos pelo fechamento

| Camada | Evidencia dos logs | Consequencia e limite |
| --- | --- | --- |
| Contato optico | `semContato` acumulado, `lacunasContatoToleradas=4` e `perdasContatoConfirmadas=2` ao final | Houve ausencias de contato observadas e pelo menos duas perdas confirmadas; nao e falha de serial. O campo `semContato` conta samples `contact=false`, nao JSON sem `sqi`. |
| SQI G1 | Variacao entre `Passed` e `Failed`; `reason=2` e `reason=4`, com `G1_samples=125` | Reprovacoes reais por regra do firmware, inclusive com `contato=True`. Descartam a janela no coordenador C#. |
| Renovacao da janela | `buffer=38`, `163`, `205`; `Waiting for a full 8-second PPG window` | A cada reset, a reacumulacao de 8 s pode gerar intervalos prolongados de `--`; G1 avalia em ~5 s. |
| BPM | `G1=Passed` com `BPM=--` e `SpO2 DEMO` numerica | Autocorrelacao IR requer pico >=0,57 e lag 35..200 BPM; a aprovacao basica G1 nao garante estimativa BPM. |
| SpO2 DEMO | Variou de ~71,1% ate ~99,1% nos eventos mostrados; por vezes `--` com G1 Passed | A formula nao foi calibrada; rejeita reconstrucoes, razao fora [0,2;1,4] e estimativas fora [70;100]. Nao interpretar mudancas como variacao fisiologica real. |
| ML-Agents | `More observations (16) made than vector observation size (1). The observations will be truncated.` | Aviso de configuracao/ciclo de vida que necessita verificacao. Nao afirmar compatibilidade integral em todos os estados, apesar das acoes de inferencia observadas. |
| Unity | `Target que foi destruido` repetidamente e `referenced script ... missing` | Defeitos de ciclo de vida/cena independentes do ADC; necessitam investigacao. |

**Decodificacao dos reasons G1 (firmware consultado):**
- `2 = RED_LOW`: media RED < 5000 contagens ADC.
- `4 = RED_FLAT`: `max(RED)-min(RED) < 500` na janela G1, mesmo que `contact=True`.
- `0 = PASS`. O contato no firmware depende separadamente do nivel medio instantaneo RED/IR e de tres observacoes consecutivas para a mudanca de estado.
- Esses thresholds sao declarados **provisorios** na documentacao do firmware e herdados de bancada MAX30102 para a implementacao MAX30105; **nao reduzir 500 sem replay rotulado e analise da forma de onda**.

## 4. Pendencias explicitamente transferidas

1. Marco 3: estabilizar qualidade de aquisicao e estimadores com CSV bruto RED/IR + `seq` + `t_ms` + `contact` + eventos G1, metricas de amplitude, tendencia, AC/DC, correlacao, reconstrucao e referencia independente.
2. Verificar na Unity 2022.3.4f1 compilacao, EditMode (`PpgProcessingTests`, `FingerPlacementTests`, `OximetryDataBridgeTests`) e regressao PlayMode. Nenhum resultado de Test Runner foi apresentado.
3. Corrigir/verificar incompatibilidade ML-Agents 16 observacoes versus tamanho 1, referencias a objetos destruidos e scripts ausentes. Nao confundir execucao de acoes com testes de integridade aprovados.
4. Verificar visualmente paineis, pausas, comportamento de perda de contato 1/2/3, fallback legado, reinicio e desconexao.
5. Remover a autorizacao experimental de recompensa/paineis por SpO2 DEMO antes de qualquer uso nao estritamente controlado; nao misturar sessao de bancada com atendimento a paciente.
6. Comparar BPM com referencia simultanea; a SpO2 requer calibracao/validacao independentes apropriadas, nao ajustes cosméticos para atingir percentuais esperados.

## 5. Rastreabilidade

- Relatorio da branch: `docs/testing/PITACO24_SPO2_ADAPTATION_GRID_LOG_TEST.md`.
- Contrato Unity: `docs/architecture/OXIMETRY_ML_AGENTS_INTEGRATION.md`.
- Fechamento anterior: `docs/marcos/MARCO_1_5_PPG_CONTATO_TOLERANTE_UNITY.md`.
- Firmware: `PHV00/PITACO_ESP32/frimeware2.4/sqi/gate_01_integrity.ino`, `processing/pitaco_ppg_contact_sqi.ino` e `docs/firmware/PPG_VALIDATION_25HZ.md`.
- Evidencia do encerramento: trecho de log Unity compartilhado em 17/11/2026, nao anexado ao repositorio; eventos transcritos acima para auditoria.
- Estado GitHub: **documentacao feita sobre a branch de teste; nenhuma alteracao em `main`, nenhum merge e nenhuma alteracao de firmware/estimadores/algoritmos de IA**. O PR #6 continua distinto e sua validacao nao e implicitamente concluida por este documento.

**Decisao de encerramento:** Marco 2 encerrado **somente quanto ao objetivo de provar o encadeamento de software ate a regra experimental e a inferencia observada**. Qualidade fisiologica, robustez temporal, validacao Unity completa e seguranca permanecem explicitamente fora do aceite e ingressam no Marco 3.
