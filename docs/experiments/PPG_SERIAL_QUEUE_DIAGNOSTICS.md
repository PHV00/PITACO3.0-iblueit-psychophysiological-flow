# Diagnóstico de fila serial PITACO 2.4 — Unity

Base: `test/unity-ppg-step1-state-continuity`. Este experimento NÃO modifica firmware, portas, taxas de amostragem, G1, SQI, critérios clínicos ou janelas PPG.

## Problema e hipótese

A aquisição ocorre no ESP32 e a entrega JSON à Unity passa por uma fila de linhas. O gerenciador recebe linhas num thread e despacha até `maxLinesPerFrame` (default 256) no `Update()`. Queda de FPS, trabalho na thread principal ou rajadas de frames podem produzir backlog e descontinuidades de sequência ou entrega tardia de decisões G1, mesmo sem perda física de amostras.

## Instrumentação adicionada

No `[PPG-DIAG] event=SUMMARY` (a cada 10 s):

- `serialPending`: linhas ainda pendentes na fila naquele instante.
- `serialPeakPending`: maior ocupação observada desde o início do processo.
- `serialPeakQueueLagMs`: maior intervalo entre a inserção de uma linha na fila e seu despacho pelo gerenciador (medido pelo relógio monotônico relativo `Environment.TickCount`, não pela data do ESP32).
- `serialBudgetExhaustions`: quantidade acumulada de frames em que o despacho atingiu `maxLinesPerFrame` e ainda havia linhas pendentes.
- `serialDropped`: contador já existente de linhas descartadas por limite de fila.

Os contadores de pico são cumulativos. Para comparar sessões sem confusão, reiniciar o Play Mode entre os experimentos. O atraso é medido **após** a leitura USB no thread; ele **não mede** a latência USB/firmware. `serialDropped=0` não elimina a possibilidade de perda na FIFO do MAX30105, no USB ou em outras etapas.

## Experimento controlado

1. Executar Unity EditMode Test Runner completo; esta branch não foi compilada aqui.
2. Coletar 120 s com dedo estável, sem BLE cliente concorrente e sem manipular a pressão do contato.
3. Exportar todos os `[PPG-DIAG]` (não apenas prints), com `SUMMARY`, `G1_*`, `WINDOW_RESET`, `SEQUENCE_RESET`, `CALC`, `BRIDGE_TRANSITION`.
4. Repetir teste no jogo ativo, com os mesmos parâmetros, para comparar com cena sem carga.
5. Observar `serialPending` crescendo, picos de `serialPeakQueueLagMs`, `serialBudgetExhaustions`, `serialDropped` e sua correlação temporal com `SEQUENCE_RESET` e `G1_EXPIRED`.
6. Caso as filas estejam saudáveis, investigar primeiro `G1_REJECT reason`, `CONTACT_LOST`, reinícios da janela, `fs` fora de 20–30 Hz, autocorrelação e RMS.

## Critérios de interpretação

- `serialDropped>0`: evidência direta de descarte no gerenciador Unity.
- `serialBudgetExhaustions>0`: há frames em que a fila não foi drenada; **não prova** perda, mas mede saturação momentânea.
- `serialPending` persistentemente crescente ou `serialPeakQueueLagMs` elevado: forte candidato a atraso na thread principal.
- `serialDropped=0`, `serialPending` próximo de zero e `serialPeakQueueLagMs` pequeno, mas `G1_REJECT reason=4`: investigar variação RED real e limiar G1; não alterar o limiar sem comparação de janela equivalente.
- `G1_REJECT reason=9`: lacuna de aquisição sinalizada pelo ESP32, a ser investigada independentemente da fila Unity.
- `WINDOW_RESET` por G1 ou contato: perda lógica de janela imposta pela política, mesmo com comunicação íntegra.

## Limitações e segurança

O firmware continua sendo a autoridade do G1 e a SpO2 nesta branch continua DEMO/não calibrada. O código de base possui `EnableDemoSpo2LegacyRulesForTesting=true` no `OximetryDataBridge`: é um recurso de integração experimental, não apropriado para decisões clínicas ou monitoramento de segurança de pacientes. Não usar os resultados desta branch para atendimento.

## Próxima decisão

Antes de alterar a duração de 8 s, o G1 de 5 s, a expiração de 6,5 s ou o limite de autocorrelação de 0,57, comparar a mesma aquisição e identificar a causa quantitativamente. Corrigir primeiro as perdas da cadeia de transporte se forem demonstradas.
