# Experimento C — BPM: autocorrelacao 0,57 vs 0,55, diagnostico RED/IR e histerese SOMENTE VISUAL

> Escopo: branch `test/unity-ppg-bpm-hysteresis-055`, baseada em
> `test/unity-ppg-queue-throughput-probe`. O firmware PITACO/ESP32 permanece
> **intocado**. SpO2 permanece **DEMO, nao calibrado**; nenhuma modificacao de
> limites G1 ou de seguranca/alertas clinicos.

## 1. Problema observado

Em Unity, com contato e G1 aprovados, o estimador BPM com janela deslizante
de 8 segundos e recalculo aproximadamente a cada 400 ms apresenta
`AUTOCORR_BELOW_0_57`. Exemplos reais dos logs: 0,623 (52,4 BPM),
0,558 (reprovado), 0,571 (52,4 BPM); e 0,589 (51,9 BPM), 0,566
(reprovado), 0,623 (52,4 BPM). O HUD alterna entre numero e `--`
porque o estimador realmente publica BPM nulo, mesmo quando o SpO2 DEMO
continua numerico.

**Tres recusas consecutivas nao sao tres janelas independentes**:
cada recalculo usa aproximadamente 8 s de dados, com grande sobreposicao.
Sao tres decisoes de janelas deslizantes (~1,2 s, se cada calculo ocorrer).

Problemas adicionais separados: G1 motivo 4 (RED range 405/453 abaixo
do limite 500), `G1_REVOKED` por amostra reason 1, perda real de contato
e um `UNITY_FRAME_STALL` de 5,18 s. Nao reduzir G1 para esconder falhas.

## 2. Mudancas no experimento

1. `BpmEstimator`: a API antiga continua usando 0,57. Um overload
   explicito aceita apenas 0,57 ou 0,55. O calculo do pico/lag candidato
   ocorre antes do teste de autocorrelacao; se reprovado, **bpm publicado
   continua nulo**. O BPM e lag candidatos sao apenas diagnosticos.
2. `BpmEstimator.TryEstimateRedDiagnostic`: aplica o mesmo metodo ao RED
   como **shadow**; nao vota nem alimenta Bridge/Mixer.
3. `PpgProcessingCoordinator.useExperimentalBpmThreshold055`
   (Inspector): **true por padrao nesta branch experimental**;
   false reproduz o limiar original de 0,57. A mudanca e apenas no
   estimador BPM, nao no G1. A opcao nao torna o sinal validado.
4. Contadores cumulativos em `SUMMARY`:
   `bpmTrials`, `bpm057Pass`, `bpm055Pass`, `bpmPublished`,
   `red057DiagnosticPass`, `bpmVisualHoldDecisions`. Sao
   contagens de TODOS os calculos, nao apenas CALC logados.
5. Eventos `CALC`: `bpmCandidateDiagnostic`, `lag`,
   `autocorr`, `threshold`, `baseline057`, `experiment055`,
   `redAutocorrDiagnostic`, `redBpmCandidateDiagnostic`,
   `redLagDiagnostic`, `redReasonDiagnostic`,
   `bpmRejectStreak`. Eventos `CALC` repetidos sao amostrados
   a cada 2 s para evitar custos de log na thread Unity.
6. Histerese **somente no HUD**: depois de BPM valido, primeira
   e segunda recusas consecutivas podem exibir o ultimo BPM validado,
   com o texto `BPM ultima leitura (estimador reavaliando)`.
   Terceira recusa, idade >2 s, G1 reprovado/expirado, contato perdido,
   telemetria ausente, sequencia interrompida ou janela invalida removem
   imediatamente essa exibicao. O contador zera em BPM valido.
7. O `LastMeasurement.BpmEstimate` continua nulo em toda recusa:
   `OximetryDataBridge` e `MixerManager` **nunca** recebem o
   BPM retido. `[PPG-HUD]` e `[PPG-DIAG] BRIDGE_*` distinguem
   `bpmHudHold`, `bpmHeldVisualOnly` e `bpmRejectStreak`.

## 3. Protocolo de teste (executar Unity com PITACO)

| Caso | Acao | Evidencia esperada |
| --- | --- | --- |
| A — inicial | Sem dedo e depois contato | Sem BPM antes do contato + G1 PASS + 8 s de janela; nenhum valor retido antigo |
| B — rejeicao 1 | Com BPM valido, provocar um `AUTOCORR_BELOW` leve | `LastMeasurement.BpmEstimate=null`; Bridge `bpm=--`; HUD `BPM_HOLD_VISUAL_ONLY` mostrando ultima leitura rotulada |
| C — rejeicao 2 | Segunda reprovacao consecutiva | Mesmas garantias, `bpmRejectStreak=2`; se passou 2 s desde ultimo BPM, nenhum hold |
| D — rejeicao 3 | Terceira reprovacao consecutiva | `bpmRejectStreak=3`, HUD `HR [2.4]: -- bpm`; SpO2 pode permanecer numerico se valido |
| E — recuperacao | Proximo calculo BPM `OK` | `BPM_VISUAL_RECOVERED`, rejeicoes zeradas, novo BPM live para HUD/Bridge |
| F — falha real G1 | G1 REJECT/reason=4 ou G1 REVOKED/reason=1 | `WINDOW_RESET`, sem hold visual; janela de 8 s se recompõe conforme o contrato atual |
| G — contato perdido | 3 `contact=false` consecutivos | `CONTACT_LOST`, `NO_CONTACT` no HUD, BPM antigo nao aparece |
| H — desconexao/timeout | Parar dados USB ou desconectar | `TELEMETRY_LOST`/desconexao, BPM nulo e estado inelegivel |
| I — Unity congelou | Mudanca de cena com `UNITY_FRAME_STALL` | Fila pode crescer, mas nao justificar aprovar BPM stale nem atravessar G1 reprovado |
| J — comparacao A/B | Coletar minimo 2 minutos com dedo parado e depois movimento controlado | Capturar `SUMMARY`, comparar `bpm057Pass` e `bpm055Pass` **sobre as mesmas janelas**; registrar tambem discrepancia vs referencia independente |

### Evidencia textual util

No console Unity, procurar:
`event=CALC`, `event=BPM_REJECT_STREAK`,
`event=BPM_VISUAL_RECOVERED`, `[PPG-HUD]`,
`event=BRIDGE_TRANSITION`, `event=SUMMARY`, `WINDOW_RESET`,
`CONTACT_LOST`, `G1_REJECT`, `TELEMETRY_LOST` e
`[PITACO-QUEUE]`.

Um bloco com `bpm057Pass=100 | bpm055Pass=110` significa apenas
10 decisoes adicionais liberadas pelo criterio numerico 0,55.
**Nao significa** 10 estimativas corretas. Validar periodos candidatos,
artefatos e erro em relacao a um monitor de referencia sincronizado
(ECG ou equipamento de pulso confiavel), nas mesmas condicoes.

### Criterios de aceitacao desta etapa

- Nenhum valor retido vai para `LastMeasurement.BpmEstimate`,
  `OnMeasurement`, `OximetryDataBridge` ou `MixerManager`.
- Toda retencao visual esta rotulada e limitada a duas reprovacoes e 2 s.
- Reprovacao G1, contato perdido ou perda de telemetria prevalecem sobre
  histerese. Fila serial nao deve perder mensagens.
- A taxa de aprovacao 0,55 aumenta, se houver janelas na faixa
  [0,55; 0,57), sem crescimento inaceitavel de erro vs referencia.
- Comparar RED e IR para investigar qualidade periodica. Nao trocar
  automaticamente para RED nem introduzir filtro passa-faixa nesta etapa
  sem analisar a forma de onda ou gravação RAW correspondente.

## 4. Proximas iteracoes APOS este teste

Se a divergencia RED/IR ou BPM candidato apontar contaminacao do sinal,
exportar janelas RAW contiguas de 8 s, verificar waveform, possivel
variacao de pressao/dedo e efeito da remocao de componente lenta.
Comparar filtro passa-faixa, autocorrelacao e consistencia interjanelas
em replay offline; comparar erro/coverage com a mesma referencia.
NAO considerar visualizacao estabilizada equivalente a estimacao validada.

### Referencias para fundamentacao (sem limiar universal de 0,55)

- Orphanidou et al. (2014), *Signal-quality indices for the ECG and PPG:
  derivation and applications to wireless monitoring*,
  DOI: 10.1109/JBHI.2014.2338351.
- *A Sliding Scale Signal Quality Metric of Photoplethysmography Applicable
  to Measuring Heart Rate across Clinical Contexts...*, Sensors 2023:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC10098585/
- *A Stability- and Aggregation-Based Method for Heart Rate Estimation Using
  Photoplethysmographic Signals During Physical Activity*, 2025:
  https://pmc.ncbi.nlm.nih.gov/articles/PMC12300191/
- *A Survey of Photoplethysmography and Imaging Photoplethysmography
  Quality Assessment Methods*, Applied Sciences 2022:
  https://www.mdpi.com/2076-3417/12/19/9582
