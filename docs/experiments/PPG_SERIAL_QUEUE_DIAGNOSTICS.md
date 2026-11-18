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


## Experimento B — expiração G1 sem destruir janela contínua (branch throughput-probe)

Este incremento preserva o bloqueio de publicação do Bridge/Mixer quando o G1 expira
ou é reprovado, e **não** modifica o período G1 de 5 s nem a janela PPG de 8 s.

- `G1_EXPIRED`: mede `ageS` (Unity), `deviceAgeMs` (amostra mais recente
  observada menos último G1 no relógio do PITACO), `serialPending` e
  `bufferRetained`. Estes tempos pertencem a domínios diferentes.
- `WINDOW_SUSPEND`: invalida imediatamente a medição publicada e o candidato
  para a HUD, sem limpar amostras brutas temporariamente.
- `G1_TIMING`: compara intervalo de chegada Unity `unityArrivalGapS`,
  intervalo do firmware `deviceGapMs` e distância de número de janela.
- `G1_RECOVERY`: `REUSE_CONTIGUOUS_RAW` apenas se o próximo G1 for PASS,
  o número da janela avançar exatamente 1 e o intervalo entre timestamps G1
  estiver entre 3500 e 6500 ms. Caso contrário: `RESET_RAW`.
- Reprovação G1, perda de contato confirmada, reinício sinalizado pelo
  firmware, falha de sequência e perda de telemetria continuam invalidando
  os resultados; o fluxo de reset permanece conservador.
- `BRIDGE_TRANSITION` inclui agora `coordinatorStatus`, `hudHold`,
  `hudCandidateBpm`, `hudCandidateSpo2Demo`, `serialPending`.
  **Candidato HUD não confirma renderização dos widgets da cena.**

### Execução

1. Reinicie o Unity Editor Play Mode na branch correta e confira
   `[PITACO-QUEUE]` a cada ~10 s e `serialPending` no `[PPG-DIAG] SUMMARY`.
   Sem esses campos, o código executado **não** corresponde à branch.
2. Colete 120 s com dedo fixo; depois repita 120 s com o jogo e IA ativos.
3. Exporte o Console completo (mensagens, erros e warnings).
4. Observe se `G1_EXPIRED` continua ocorrendo; quando ocorrer, a publicação
   deve ficar indisponível e registrar `WINDOW_SUSPEND`, **não**
   `WINDOW_RESET` pelo mesmo motivo.
5. Caso o próximo G1 seja consecutivo e aprovado, procure
   `G1_RECOVERY mode=REUSE_CONTIGUOUS_RAW`; a janela não precisará ser
   reconstruída do zero quando a continuidade e o número de amostras permitirem.
6. Se o próximo G1 falhar, faltar uma janela, ocorrer perda de contato ou
   descontinuidade da sequência, o descarte deve continuar e a HUD deve
   permanecer indisponível até haver nova medição válida.
7. Compare `PITACO-QUEUE maxFrameGapMs`, `maxWaitMs`, `pending`,
   `G1_TIMING` e `BRIDGE_TRANSITION` para localizar atrasos.
8. Se `BRIDGE_TRANSITION cause=AVAILABLE` persistir mas a HUD visível
   congelar, investigar separadamente o script que renderiza os textos.
   Este experimento ainda não instrumenta o widget de cena diretamente.

Este código foi revisado estaticamente, mas não foi compilado nem executado no
Unity Editor/hardware neste ambiente. SpO2 continua DEMO não calibrada e não
é apropriada para decisões médicas ou alertas de pacientes.


 
## Experimento C — Unity-only, firmware PITACO validado e congelado

**Escopo:** somente o repositorio Unity
`PHV00/iblueit-psychophysiological-flow-pitaco2.4-port`,
branch `test/unity-ppg-queue-throughput-probe`. O firmware original
e a avaliacao G1 ja foram validados via BLE e **nao devem ser alterados
nem regravados** neste experimento.

### Contrato ja existente do firmware

A Unity consome os campos existentes nos JSON originais:
`type=sample`, `seq`, `t_ms`, `ppg.red`, `ppg.ir`,
`sqi.contact`, `sqi.reason`; e `type=g1`, `t_ms`,
`window`, `sqi.g1`, `sqi.reason`, `sqi.samples`.
**Nao ha campo epoch, extensao de firmware ou dependencia de PR externo.**

### Correcoes exclusivamente Unity

1. `Gate01Approved` e `IsGate01ApprovedForSample` verificam o
   prazo de 6500 ms pelo dominio **`sample.t_ms - g1.t_ms`**.
   O salto de `Time.unscaledTime` em frame travado nao e motivo
   suficiente para declarar G1 expirado.
2. Se nao chegarem samples por 3 s e a fila estiver vazia, a
   telemetria continua sendo invalidada (watchdog real).
   `UNITY_FRAME_STALL` concede **0,5 s de tolerancia apenas ao reset
   da janela**, na primeira verificacao apos um frame com atraso >3 s.
   Essa tolerancia NAO autoriza medicao antiga no Bridge/Mixer.
3. A fila USB/Unity passa a despachar no maximo **64 linhas por frame**
   por padrao, para repartir rajadas da retomada da thread principal;
   nao altera ordem, perda de pacotes, portas USB nem taxa da placa.
   `budgetExhaustionsTotal` pode aumentar apos um travamento curto:
   observar `pending` e `dropped` juntos antes de interpretar.
4. A telemetria detalhada `CALC stage=ESTIMATORS` deixa de imprimir
   a cada 400 ms e passa a registrar mudancas de status e batimento
   a cada 2 s, diminuindo o custo do Console e stacktrace.
5. `OximetryHudPresenter` usa **diretamente a medicao atual do
   PpgProcessingCoordinator** para PITACO 2.4, sem cache de 1 s
   do `Player`. Legado continua usando `ReadPlayerText(false)`.
   `[PPG-HUD]` traz `scene`, `object`, `frame`, `state`,
   valores candidatos e a string realmente atribuida ao `Text`.
6. `HOLD_VISUAL_ONLY` continua limitado a ate 2 s e
   1-2 amostras de contato falso consecutivas; nunca entra no Mixer.
   Sem leitura autorizada, a HUD sinaliza `G1_BLOCK`,
   `WAIT_ESTIMATE` ou `NO_CONTACT`, em vez de mostrar zeros
   ou reaproveitar uma SpO2 como se fosse atual.
7. Se o firmware indicar `sample.sqi.reason=1` enquanto um G1
   anterior estava aprovado, a Unity continua **reiniciando a janela**
   de modo conservador: o firmware pode ter iniciado nova
   sessao de aquisicao. Reprovacoes reason 4/5/9 e perda de
   contato confirmada continuam bloqueando a publicacao.

### Como validar sem tocar no firmware

```bash
git fetch origin
git switch test/unity-ppg-queue-throughput-probe
git pull --ff-only origin test/unity-ppg-queue-throughput-probe
```

1. Iniciar Play Mode e confirmar `[PITACO-QUEUE]`,
   `[PPG-DIAG]` e `[PPG-HUD]`.
2. Coletar 120 s com dedo estavel + 120 s durante jogo/IA. Preservar
   o Console inteiro, com `t`, `seq` e `devMs`.
3. Se Unity ficar ~4,75 s parada, registrar `UNITY_FRAME_STALL`
   e `maxFrameGapMs`; nao deve ocorrer `G1_EXPIRED`
   apenas por esse salto de Unity.
4. Conferir `G1_TIMING` (intervalos em dispositivo e Unity),
   `WINDOW_SUSPEND`, `G1_RECOVERY`, `G1_REVOKED`,
   `WINDOW_RESET`, `BRIDGE_TRANSITION` e `[PPG-HUD]`.
5. Para `G1_REVOKED`/reprovacoes verdadeiras, valores deixam
   de ser publicados; o texto de HUD deve indicar a causa, sem
   manter numero antigo aparentemente valido.
6. Se `[PPG-HUD] state=LIVE` mostrar numeros corretos, mas o
   widget visivel continuar congelado, verificar o Canvas/objeto
   e componentes de cena; a linha comprova atribuicao ao
   componente Text, nao a renderizacao final na tela.

**Limite da validacao atual:** revisao estatica do codigo, sem build
do Unity Editor nem teste de hardware neste ambiente. A SpO2 DEMO
permanece nao calibrada e nao e apropriada para decisoes clinicas.
