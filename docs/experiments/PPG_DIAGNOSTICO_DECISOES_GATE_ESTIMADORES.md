# Marco 3 — Rastreamento das decisões PPG (instrumentação sem ajustes de limiar)

**Branch Unity:** `test/ppg-quality-decision-trace`, derivada de `test/pitaco24-spo2-adaptation-grid-logs`.
**Branch firmware/Python correspondente:** `PHV00/PITACO_ESP32:test/ppg-g1-window-metrics`, derivada de `test/ppg-long-session-csv-logging`.
**Objetivo:** localizar onde uma janela do MAX30105 é rejeitada, desde a aquisição até a HUD e o Mixer. **Nenhum threshold, estimador, controle de contato, política de BPM/SpO2 ou recompensa foi reajustado.**

## Como usar

1. No Unity, abrir `Window > General > Console` e filtrar `[PPG-DIAG]`. Desmarcar `Collapse` para manter cada decisão em ordem. Continuar a observar também os logs `[SpO2Grid-Test]` quando forem analisados os painéis/recompensas.
2. Executar o jogo usando o firmware gravado a partir da branch de diagnóstico. **A troca de branch de código, sozinha, não atualiza o ESP32.** O firmware antigo continua funcional, mas o campo `metrics=UNAVAILABLE_OLD_FIRMWARE` indicará a ausência das métricas novas.
3. Fazer ensaio controlado de 90–120 s com contato estável, sem dessaturação provocada. Anotar quando ocorrem movimentação, variação de pressão e retirada/reposição do dedo. Não registrar dados pessoais.
4. Salvar logs completos do Console e CSV bruto do monitor Python, de preferência em execuções separadas da COM USB. Uma captura Python/BLE concorrente só é válida se não houver disputa pelo dispositivo/fluxo.
5. Combinar registros pelo `devMs`, `window` e `seq` **somente se forem da mesma sessão efetiva do firmware**. Se Unity e Python capturarem ensaios separados, comparar distribuições e causas agregadas, nunca alinhar janelas 1:1. `t` no Console é relógio Unity e não deve ser confundido com `t_ms` do ESP32.

## Eventos [PPG-DIAG] e o que demonstram

| Evento | O que contém/como interpretar |
| --- | --- |
| `G1_PASS` / `G1_REJECT` | Resultado a cada janela ~5 s; `reason`, `samples`, `devMs`, `window`, `bufferBefore`, estatísticas RED/IR recebidas |
| `CONTACT_GLITCH` | Primeira leitura instantânea sem contato; **não** é retirada física confirmada |
| `CONTACT_LOST` | Terceira falta consecutiva no critério Unity, não necessariamente retirada física intencional |
| `CONTACT_PRESENT` | Recuperação e quantas amostras de contato foram toleradas |
| `HUD_HOLD_START` | Retenção de até duas falhas **somente visual**; Mixer continua consumindo leitura validada |
| `G1_REVOKED` / `G1_EXPIRED` | PASS antigo invalidado por amostra de reset ou idade |
| `WINDOW_RESET` / `SEQUENCE_RESET` | Causa exata da perda do buffer, tamanho descartado e identificadores |
| `CALC stage=G1_BLOCK` | G1 ainda impede o processamento; não logar cada amostra, apenas transição e amostra a cada 2 s |
| `CALC stage=WINDOW_REJECT` | Motivo de rejeição da janela C# (ainda incompleta, taxa efetiva inadequada, sequências, ADC, lacunas >2%, DC) |
| `CALC stage=ESTIMATORS` | Janela aprovada: tamanho, fs, redDC/irDC, redACrms/irACrms, reconstruções, autocorrelação, lag, razão R, resultado e motivo independente de BPM e SpO2 DEMO |
| `BRIDGE_TRANSITION` / `BRIDGE_HEARTBEAT` | Fonte, contato/G1 reais, publicação idêntica, idade, elegibilidade, BPM, SpO2 DEMO e confirmação de consumo Mixer |
| `SUMMARY` | A cada 10 s: JSON/amostras recebidas, descartes reais da fila (incluindo a fila do Manager), erros de parser, contato, G1 e buffer |

**Frequência:** G1 por janela; contato/reset por evento; estimadores em **toda avaliação efetiva de ~400 ms** (para auditar cada cálculo), enquanto os estados repetidos de espera/bloqueio são limitados a uma linha a cada 2 s; Bridge ao mudar de estado ou após 10 s; resumo a cada 10 s. Os valores da HUD não são mantidos como "medição nova".

### Motivos de rejeição BPM

- `INPUT_IR_AC_INSUFFICIENT`, `LAG_RANGE_INVALID`, `AUTOCORR_BELOW_0_57`, `PEAK_NOT_FOUND`, `BPM_OUT_OF_RANGE`, `OK`.
- A autocorrelação continua com limiar 0,57 e estimativa dentro de 35–200 BPM. `lag` é amostral; quando houver falha antes do pico, vale 0.

### Motivos de rejeição SpO2 DEMO

- `INPUT_NULL`, `RECONSTRUCTED_PPG_GAP`, `DC_NOT_POSITIVE`, `AC_RMS_TOO_LOW`, `RATIO_OUTSIDE_0_2_TO_1_4`, `SPO2_DEMO_OUTSIDE_70_TO_100`, `OK`.
- A fórmula DEMO e os limites preexistentes permanecem **inalterados**. Um resultado numérico DEMO não certifica saturação nem pode orientar decisão clínica.

### Distinção fundamental: G1 x estimadores

```text
G1 REJECT reason=4 + redRange=477, minRange=500
          -> BUFFER_RESET (Unity descarta janela 8s)
          -> CALC G1_BLOCK / WINDOW_REJECT (reacumulação)
          -> CALC ESTIMATORS quando houver janela completa e G1 PASS
                  bpmReason=... autocorr=... lag=...
                  spo2Reason=... ratioR=... redACrms=...
          -> BRIDGE_TRANSITION cause=... (ou AVAILABLE)
```

**Sobre 477 ADC:** 477 é 23 contagens (4,6%) abaixo do limiar de 500. Isso prova **somente que a regra atual reprova essa faixa**, não que os valores de SpO2 DEMO são corretos, que a forma de onda possui pulso ou que a saturação foi validada. A primeira análise deve contar quantas janelas G1 seriam candidatas a PASS se `G1_MIN_RANGE` fosse, por exemplo, 450, **sem efetuar essa mudança**; exige sinais RAW e rótulos de qualidade independentes. Verificar também IR, clipping, média e GAP, para não criar falsos positivos.

## Registros reduzidos (somente branch de diagnóstico)

- Removido o relatório antigo verboso `[PPG C# diagnostico]`; substituído por eventos `[PPG-DIAG]` e `SUMMARY`.
- Desativadas mensagens `[PITACO 2.4] JSON: N` a cada 100 callbacks. Conexão, desconexão e erros permanecem.
- O Bridge deixa de despejar o mesmo `[OximetryBridge->Mixer]` a cada 5 s; apresenta causas por transição e heartbeat 10 s.
- Nas regras de gameplay, silenciados **apenas** relatórios repetitivos de recompensa normal +0,1; continuam logs de estado elegível, classificação REGULAR/PERIGO, painéis, recompensa negativa e agente ausente.
- As mensagens narrativas do DeepDDA sobre aumentar/manter/diminuir velocidade/carga foram silenciadas, **sem alterar ações do modelo**.
- **NÃO foram silenciados** problemas reais como `Target destruído` ou `Missing Script` da cena. Esses são defeitos reais e permanecem no Console, mas podem ser isolados pelo filtro `[PPG-DIAG]`.

## Critério para encerrar o diagnóstico

1. Cada janela G1 falha/passada deve ter RED/IR média, mínimo/máximo, range, clips, GAP e limiar de referência; distinguir firmware antigo por campo ausente.
2. Para cada `--`, deve haver um evento associado: bloqueio G1, buffer incompleto, autocorrelação BPM insuficiente, vetos do ratio/RMS/reconstruções da SpO2, ou Bridge sem publicação.
3. Antes de propor redução do limiar, medir taxa de rejeição, taxa de pulsos utilizáveis por janela rotulada e efeito contrafactual em falsos positivos; não usar SpO2 DEMO como rótulo de verdade.
4. Executar Unity EditMode `PpgProcessingTests` e validação em hardware. Testes adicionados cobrem compatibilidade dos estimadores e recepção opcional de métricas G1; **ainda não há resultado executado registrado neste commit**.

**Pontos de código:** `PpgProcessingCoordinator.cs`, `PpgSignalProcessor.cs`, `BpmEstimator.cs`, `Spo2Estimator.cs`, `OximetryDataBridge.cs`, `MixerAdaptationGrid.cs` e arquivos de teste Unity. O firmware/Python gera e persiste estatísticas G1 sob o objeto JSON opcional `stats`, sem alterar `sqi`.
