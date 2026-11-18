# Etapa 1.1 — Relógios, latência e custo real do processamento PPG (Unity somente)

**Branch:** `test/unity-ppg-step1-1-clock-latency`  
**Base:** `test/unity-ppg-step1-state-continuity`  
**PITACO e firmware:** **inalterados** (nenhum `.ino`, biblioteca, baud rate, protocolo ou SQI do dispositivo foi alterado).  
**BPM, SpO₂ DEMO, limiar de autocorrelação 0,57, G1 firmware, janela de 8 segundos e adaptação de jogo:** inalterados.

## Por que esta revisão existe?

A captura de 18/11 mostrou valores `t_ms` do PITACO avançando ~5 s entre duas janelas G1, mas o `Time.unscaledTime` da Unity avançando ~9,6 s. Mais de 4 s de dados foram processados num único instante de frame Unity. O código anterior usava simultaneamente idade de dispositivo e `6,5 s` do relógio Unity para expirar o G1; isso podia invalidar uma janela verdadeira e forçar 8 s de recuperação depois de um congelamento do Editor. Medir `Time.unscaledTime` só no callback Unity não distingue **pacote recém-recebido** de **pacote antigo aguardando na fila**.

## Três domínios de relógio — não somar nem subtrair épocas distintas

| Relógio | Origem | Finalidade | Limitação |
| --- | --- | --- | --- |
| `sample.t_ms` e `g1.t_ms` | ESP32 `millis()`, transportado no JSON original | **Idade relativa do G1, ordem de aquisição, duração de janela e cadência**, com wrap uint32 | Timestamp amostral pode corresponder à leitura FIFO, **não é instante exato de conversão ADC** |
| `Stopwatch.GetTimestamp()` | Host Windows, relógio monotônico comum às threads do processo | **Idade de fila**: tempo decorrido entre `ReadLine` e execução do callback Unity; custo CPU das etapas | Não contém o horário do ADC e não mede a fila/hardware antes do `ReadLine` |
| `Time.unscaledTime` | Loop principal Unity | Tempo entre atualizações da HUD, heartbeat de recepção/telemetria e idade do snapshot do jogo | Em lote, dezenas de callbacks podem ter o **mesmo valor de frame**; não serve para medir CPU nem frescor de amostras individualmente |

**Não implementamos sincronização de relógios absolutos** ESP32↔Unity. Não é possível garantir essa sincronização com `millis()` unidirecional e sem um protocolo de troca de tempos. Usamos relógios distintos para decisões distintas, preservando o JSON do PITACO.

## O que foi efetivamente modificado

### Recepção serial (somente Unity)

- `PitacoConnectionManager.cs`: carimba cada linha imediatamente depois de `ReadLine`, antes de entrar na fila, com `Stopwatch.GetTimestamp()`. O `Packet` mantém esse `EnqueuedTicks`; a ordem, a thread e o protocolo continuam os mesmos.
- `SerialControllerPitaco24.cs`: expõe `OnJsonReceivedTimed(string json, long workerTicks)`; o evento `OnJsonReceived` original permanece, preservando consumidores legados. O coordenador PPG passa a consumir **só** o evento temporizado, sem processar cada JSON duas vezes.
- **Compatibilidade com leitura standalone:** sem `PitacoConnectionManager`, o timestamp do novo callback é um **proxy do momento de despacho**, não a chegada real no worker. Portanto, as métricas de fila só são conclusivas quando o manager persistente é o dono da COM.

### G1 e relógio de aquisição

- `PpgProcessingCoordinator.cs`: `Gate01MaxAgeMs=6500` continua o mesmo e é aplicado **apenas à diferença de `t_ms` do PITACO**. Removida a expiração independente por `Time.unscaledTime - lastGate01ArrivalAt >6,5 s`.
- **Sem novos falsos PASS:** `reason=1` após reinício do firmware ainda revoga o G1; `reason=4`/FAIL ainda impede cálculo; gap/seq/contato continuam válidos. Uma interrupção da telemetria por mais de **3 s** ainda reinicia o estado e a publicação.
- Em lotes antigos, o relógio do ESP32 pode permanecer válido, mas a **publicação é bloqueada** se a linha levou mais de **2.000 ms** entre `ReadLine` e o processamento Unity. Esse orçamento inicial corresponde à ordem de grandeza do frescor máximo do snapshot usado no Bridge; é **provisório para diagnóstico** e deve ser validado com os percentis de latência reais, não aumentado arbitrariamente.
- A entrada em fila atrasada resulta em `QUEUE_DELAYED` e resultado nulo; a recuperação em `QUEUE_RECOVERED`. Os dados RAW e candidatos continuam preservados para que, após receber uma amostra recente, a janela de 8 s possa ser avaliada sem inventar amostras e sem aguardar um novo G1 se ele ainda for válido no relógio do dispositivo.
- Os eventos `G1_EXPIRED` agora informam `deviceAgeMs` e `unityArrivalAgeS` **separadamente**. O primeiro governa a decisão; o segundo é diagnóstico.

### Medições de custo (sem logs a 25 Hz)

Em `[PPG-DIAG] event=PERF`, a cada ~10 s:

| Métrica | Significado |
| --- | --- |
| `unityMaxFrameMs` | Maior `Time.unscaledDeltaTime` no período (~10 s), útil para identificar congelamentos |
| `queuePackets`, `queueMeanMs`, `queueMaxMs` | Quantidade e idade média/máxima das mensagens até seu callback |
| `sample_count/meanMs/maxMs` | Duração real do processamento de cada callback de amostra na Unity, inclusive filtros e logs de transição |
| `g1_count/meanMs/maxMs` | Custo **Unity** de tratar o JSON G1 + calcular estatísticas observadas no monitor de diagnóstico; **não mede o G1 do ESP32** |
| `window_count/meanMs/maxMs` | Custo de `PpgSignalProcessor.TryProcess`, inclusive janelas rejeitadas |
| `bpm_count/meanMs/maxMs` | Custo de `BpmEstimator.TryEstimateDetailed` |
| `spo2_count/meanMs/maxMs` | Custo de `Spo2Estimator.TryEstimateDemoDetailed` |

Em `[PPG-DIAG] event=SUMMARY`:

| Métrica | Significado |
| --- | --- |
| `queuePending` | Pacotes `Line` ainda na fila, quando o resumo foi executado |
| `queueHighWater` | Maior profundidade observada desde o início da sessão (não é zerado a cada 10 s) |
| `queueDelayMs` e `queueDelayed` | Idade da última mensagem processada e estado do bloqueio por fila |
| `serialLastDrainMs`, `serialMaxDrainMs` | Tempo para despachar as mensagens do último frame e maior valor desde a inicialização do manager |
| `serialLastDrainPackets` | Quantidade de eventos despachados no último frame |

**Importante:** o tempo `sample_meanMs` inclui o custo dos cálculos disparados naquela amostra e seus subtempos (window, BPM, SpO2). **Não some** essas métricas como se fossem etapas não sobrepostas. Para avaliar o orçamento por frame, observe `serialMaxDrainMs`, `unityMaxFrameMs` e `queueMaxMs`.

### Redução dos logs

`CALC stage=ESTIMATORS` agora é emitido em mudança de motivo ou até uma vez a cada 2 s; as avaliações em si continuam a cada ~400 ms **no relógio `t_ms` do PITACO**, quando há janela completa. O contador `bpm_count` e `spo2_count` em `PERF` prova quantas avaliações realmente ocorreram, mesmo que elas não apareçam uma a uma no Console. Isso evita stacktraces redundantes durante freeze e reduz a distorção introduzida pelo próprio diagnóstico.

## Trilha esperada

```text
[PITACO] ESP32 sample.t_ms, seq ------- relógio RELATIVO do dispositivo
              |
SerialPort.ReadLine() ---> Stopwatch.GetTimestamp(): EnqueuedTicks
              |
Fila do manager (medir queueMeanMs, queueMaxMs, queueHighWater)
              |
Manager.Update (medir serialMaxDrainMs e serialLastDrainPackets)
              |
SerialControllerPitaco24.OnJsonReceivedTimed
              |
PpgProcessingCoordinator:
  - queueAgeMs > 2000 ?  QUEUE_DELAYED: bloquear PUBLICAÇÃO
  - sample.t_ms - g1.t_ms > 6500 ? G1_EXPIRED: invalidar
  - sample/contato/seq/reason=1 sempre obedecidos
              |
PpgSignalProcessor.TryProcess ---> window_meanMs/maxMs
BpmEstimator.TryEstimateDetailed -> bpm_meanMs/maxMs
Spo2Estimator.TryEstimateDemoDetailed -> spo2_meanMs/maxMs
              |
OximetryDataBridge: somente medição nova e fresca
```

## Testes e comparação controlada

No Unity Test Runner, executar **toda a suíte `PpgProcessingTests`** (EditMode) e depois repetir os testes de integração do Bridge e das cenas, incluindo:

- `UnityMainThreadDelayDoesNotExpireDeviceFreshG1`: força idade host antiga sem avançar o dispositivo; o G1 não pode expirar.
- `QueuedOldPacketsCannotPublishDespiteApprovedG1`: simula quatro segundos de fila; mesmo com G1 aprovado, nenhuma medição antiga é publicada. Ao receber nova amostra realmente fresca, a janela pode recuperar sem novo G1 (desde que o dispositivo continue no período autorizado).
- `QueueDelayDoesNotModifyFirmwareDeviceTimestampOrSequence`: garante que timestamps e seq originais não foram reescritos pelo novo relógio de host.
- Testes anteriores de `reason=1`, perda de contato, FAIL, GAP, G1 stale e buffer.

### Ensaio de hardware (firmware original inalterado)

1. Abrir Unity Test Runner; confirmar que a suíte está verde e não há erros de compilação.
2. Rodar **120 s** com dedo estável e filtro `[PPG-DIAG]`; depois, em sessão diferente, testar uma retirada breve. Não tentar induzir dessaturação.
3. Guardar 12–15 linhas `event=PERF` e os eventos `QUEUE_DELAYED`, `QUEUE_RECOVERED`, `G1_PASS`, `G1_EXPIRED`, `CONTACT_LOST`, `WINDOW_RESET` e `BRIDGE_TRANSITION`. Guardar também qualquer exceção e alerta da cena.
4. Usar os tempos para diagnosticar:
   - `unityMaxFrameMs` alto e `serialMaxDrainMs` baixo: congelamento em **outro** componente da cena/Editor (investigar Unity Profiler).
   - `serialMaxDrainMs` alto e `sample_meanMs`/`maxMs` alto: processamento óptico, JSON ou logs custosos.
   - `queueHighWater` e `queueMaxMs` elevados: drenagem insuficiente da fila, independente do número de drops.
   - `window/bpm/spo2_maxMs` altos: gargalo matemático identificado.
   - `G1_EXPIRED` com `deviceAgeMs > 6500`: expiração pelo critério atual; não é causada diretamente pelo relógio Unity.
5. Comparar com a **branch-base Etapa 1**. Não concluir melhora apenas pela quantidade de `--`; avaliar bloqueios corretos, erros de estado e frescor.

### Critérios para aprovar 1.1

- Nenhum G1 device-fresh expira somente porque o Unity Editor congelou.
- Pacotes com `queueDelayMs > 2000` não publicam resultados novos.
- Nenhum FAIL/contato ausente/`reason=1` é ignorado.
- Nada do firmware, parâmetros de amostragem e fórmulas foi alterado.
- Valores de `PERF` e profundidade da fila sustentam a escolha de qualquer timeout ou otimização posterior.
- Unity EditMode e ensaio físico passam sem regressões. **A validação de runtime ainda é pendente**; alterações versionadas não substituem os resultados do Editor.

**Fora do escopo:** calibrar SpO2 DEMO, validar precisão fisiológica, ajustar o limiar de autocorrelação, mudar G1/INO, sincronizar épocas absolutas do ESP32 e do PC. SpO2 DEMO continua não calibrada: não usar para decisões clínicas, pacientes ou regras terapêuticas. Se as regras de jogo DEMO estiverem habilitadas em testes, manter o ambiente estritamente de bancada.
