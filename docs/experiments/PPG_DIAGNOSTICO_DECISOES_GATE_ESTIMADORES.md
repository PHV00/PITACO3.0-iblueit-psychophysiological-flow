# Marco 3 — investigação de qualidade PPG EXCLUSIVAMENTE na Unity

**Repositório editado:** `PHV00/iblueit-psychophysiological-flow-pitaco2.4-port`
**Branch de diagnóstico:** `test/ppg-quality-decision-trace`, derivada de `test/pitaco24-spo2-adaptation-grid-logs`.

**Regra desta etapa:** não alterar firmware, INO, hardware, protocolo JSON, biblioteca BLE, configuração do MAX30105 ou Python do PITACO. Usar o **firmware original** que já está instalado e funciona. O trabalho anterior de diagnóstico no repositório `PITACO_ESP32` foi desfeito na sua branch experimental; esta investigação só exige mudanças no C# da Unity.

## Pergunta experimental

Por que a HUD da Unity passa de BPM/SpO2 DEMO para `--`, ainda que o dedo não tenha sido retirado e a coleta Python/BLE tenha mostrado comportamento mais estável?

As hipóteses devem ser separadas por estágio, sem supor antecipadamente que o firmware ou o limiar G1 é defeituoso:

1. Uma mensagem `type=g1` recebida com `sqi.g1=false` bloqueia o cálculo na Unity — é a decisão **oficial** do firmware.
2. Amostras `type=sample` recebidas com `sqi.contact=false` causam quarentena do dado; três consecutivas reiniciam janela e estado de G1.
3. Perda de sequência, idade do G1, taxa de amostragem efetiva ou janela incompleta impedem o processamento de oito segundos.
4. Mesmo com janela íntegra, `BpmEstimator` pode reprovar autocorrelação ou pico; `Spo2Estimator` pode reprovar lacuna reconstruída, RMS, razão R ou faixa da fórmula DEMO.
5. A medição pode ser rejeitada no `OximetryDataBridge` por publicação inválida/antiga (>2 s) enquanto a HUD exibe seu valor independente por tolerância visual. Isto **não** prova uma falha do sensor.

## Como a Unity observa as estatísticas RED/IR sem alterar o ESP32

A Unity já recebe os pares RED/IR em cada JSON `type=sample`. A classe de diagnóstico `PpgRawG1Probe.cs` armazena até 6,5 segundos / 384 amostras desses pares **antes** dos filtros Unity. Quando chega o evento `type=g1` original, com `sqi.reason` e `sqi.samples`:

- Seleciona até a quantidade de amostras declaradas pelo firmware dentre as mais recentes entregues à Unity.
- Calcula localmente `redMin/redMax/redMean/redRange`, `irMin/irMax/irMean/irRange`, contagem de clipping, contagem de contatos falsos e descontinuidades de sequência.
- Registra `observed`, `reported`, `countCovered`, `sequenceGaps`, `spanMs` e `match`.
- Usa valores de referência `refMinRange=500` e `refMinMean=5000` **apenas no log**. Não aplica um novo G1, não reclassifica `reason` e não modifica limiar algum.
- Continua a usar `sqi.g1`, `sqi.reason`, `sqi.samples` do firmware como única autoridade para aprovar/rejeitar G1.

A marca `metrics=UNITY_RAW_SHADOW_NOT_FIRMWARE` significa que esses números são **observações da Unity**, não valores transmitidos/calculados pelo firmware. `match=CANDIDATE_ONLY` indica contagem e sequência aparentemente completas, mas **não certifica correspondência exata**: o firmware pode ter reiniciado sua janela por contato, FIFO, I2C ou temporização sem que a Unity reproduza os mesmos limites. `match=INCOMPLETE` indica falta de cobertura/continuidade. Uma falha de aquisição interna (`GAP`) é `acquisitionGap=UNOBSERVABLE` e não pode ser inferida exclusivamente do JSON entregue.

**Atenção ao exemplo 477:** um `redRange=477` local não deve ser apresentado como amplitude G1 oficial nem como prova de sinal fisiológico bom. A observação permite formular hipóteses: a medida local está 23 ADC abaixo do limiar de referência de 500 (4,6%), mas o motivo `reason` do firmware e a qualidade pulsátil real precisam ser avaliados separadamente.

## Protocolo prático no Unity Editor

1. Atualizar **apenas o repositório Unity** para `test/ppg-quality-decision-trace`. Deixar a placa e o firmware exatamente como estão.
2. Abrir `Window > General > Console`, desmarcar `Collapse` e aplicar o filtro `[PPG-DIAG]`. Os logs `[SpO2Grid-Test]` continuam disponíveis se for necessário acompanhar eventos do Mixer.
3. Realizar 90–120 s com dedo estável. Anotar, com relógio da sessão, quando houver leve alteração de pressão ou movimento; opcionalmente fazer um segundo ensaio com uma retirada deliberada para validar `CONTACT_LOST`. Evitar qualquer procedimento de dessaturação.
4. Guardar todo o Console em texto (com timestamps). Sem outro programa concorrente na mesma porta USB. Não é necessário gravar firmware, recompilar INO nem habilitar novas mensagens no PITACO.
5. Correlacionar `G1_REJECT/G1_PASS`, `CONTACT_*`, `WINDOW_RESET`, `CALC` e `BRIDGE_TRANSITION` pelos campos `devMs` (relógio do dispositivo), `seq`, `window` e `t` (relógio Unity). Os relógios são diferentes. Capturas Python feitas em outra sessão só podem ser comparadas estatisticamente, não janela a janela.

## Eventos de diagnóstico

| Evento | O que investigar |
| --- | --- |
| `G1_PASS` / `G1_REJECT` | Decisão oficial do firmware (`reason`, `samples`) e métricas observadas localmente (`metrics=UNITY_RAW_SHADOW_NOT_FIRMWARE`) |
| `CONTACT_GLITCH` | Primeiro `contact=false`; **não comprova que o dedo foi fisicamente retirado** |
| `CONTACT_LOST` | Terceira falha consecutiva e reinício do estado no Unity |
| `CONTACT_PRESENT` | Contato recuperado; quantas amostras foram temporariamente ignoradas |
| `HUD_HOLD_START` | Manutenção visual de valor anterior; não alimenta o Mixer |
| `G1_REVOKED` / `G1_EXPIRED` | PASS antigo invalidado pelo estado/idade |
| `WINDOW_RESET` / `SEQUENCE_RESET` | Motivo de descarte das amostras de oito segundos |
| `CALC stage=G1_BLOCK` | Bloqueio antes de passar ao processamento |
| `CALC stage=WINDOW_REJECT` | Janela incompleta, FIFO/seq, fs, DC ou mais de 2% de lacunas |
| `CALC stage=ESTIMATORS` | **Cada cálculo real (~400 ms)**: fs, reconstruções, DC e RMS, autocorrelação, lag, R, BPM, SpO2 DEMO e seus motivos independentes |
| `BRIDGE_TRANSITION` / `BRIDGE_HEARTBEAT` | Medição aceita/recusada no Bridge, fonte, idade, contato, G1 e leitura pelo Mixer |
| `SUMMARY` | A cada 10 s: amostras, erros de parsing, perdas da fila, contato, G1 e buffer |

Estados repetidos de espera são limitados a uma linha a cada 2 s; os cálculos reais do estimador são registrados individualmente.

### Regras C# mantidas sem alteração

- BPM por autocorrelação IR (`MinimumAutocorrelation=0.57`, faixa 35–200 BPM): motivos `INPUT_IR_AC_INSUFFICIENT`, `LAG_RANGE_INVALID`, `AUTOCORR_BELOW_0_57`, `PEAK_NOT_FOUND`, `BPM_OUT_OF_RANGE`, `OK`.
- SpO2 **DEMO não calibrada**: `RECONSTRUCTED_PPG_GAP`, `DC_NOT_POSITIVE`, `AC_RMS_TOO_LOW`, `RATIO_OUTSIDE_0_2_TO_1_4`, `SPO2_DEMO_OUTSIDE_70_TO_100`, `OK`.
- O firmware continua decidindo o G1, inclusive `reason=4` (RED_FLAT), sem nenhuma modificação.
- O `OximetryDataBridge` e a lógica de adaptação continuam consumindo a medição corrente validada, não o valor temporariamente retido da HUD.

## Logs reduzidos

- O relatório verboso antigo de contadores JSON foi substituído por `[PPG-DIAG]` com eventos.
- Foram silenciados os `[PITACO 2.4] JSON: 100/200/...`, os registros redundantes do Bridge e as narrações rotineiras de ações do DeepDDA.
- Eventos significativos de adaptação, erros de conexão, exceções, avisos de cenas e erros de componentes **continuam visíveis**. O filtro do Console isola o tema PPG sem esconder defeitos reais.

## Testes de regressão e limites de evidência

Os testes Unity `PpgProcessingTests` agora verificam:
- Compatibilidade numérica dos estimadores detalhados com suas APIs anteriores.
- Diagnósticos de reprovação por autocorrelação/AC RMS/lacunas.
- Recepção do **JSON G1 original, sem nenhum objeto novo**.
- Janela sintética local com `redRange=477` e IR=820, contagem e continuidade válidas, sem alterar aprovação G1.
- Identificação explícita de frames perdidos e seleção das últimas N amostras.
- Regras anteriores de contato, rejeição G1, buffer, HUD e Bridge.

**Validação pendente:** executar Unity EditMode Test Runner e realizar teste com hardware real. Analisar o log final antes de ajustar critérios; o G1 oficial, a janela observada localmente e os estimadores C# são etapas diferentes. Nenhum valor de SpO2 DEMO deve ser tratado como medição clínica.
