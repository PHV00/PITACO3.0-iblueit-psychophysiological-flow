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


## Experimento C — relogio do dispositivo, epoch G1 e renderizacao da HUD

**Unity:** `test/unity-ppg-queue-throughput-probe`.
**Firmware de teste (repositorio separado):** `PHV00/PITACO_ESP32`,
branch `test/ppg-g1-contact-epoch`.

### Contratos de comportamento

1. **Expiracao temporal:** os 6500 ms sao avaliados pela diferenca
   `sample.t_ms - g1.t_ms` no dominio do PITACO; `Time.unscaledTime`
   nao pode sozinho invalidar o G1 apos um frame longo da Unity.
2. **Perda real de amostras:** se a Unity deixar de receber frames por
   mais de 3 s (e nao houver pacotes aguardando despacho), deve seguir
   bloqueando, limpar a janela e registrar `TELEMETRY_LOST`.
3. **Contato/SQI:** o firmware continua confirmando/removendo contato
   apos 3 amostras; os thresholds G1 e a janela de 5 s NAO foram
   flexibilizados. Confirmacao de mudanca de contato, inicio/fim de
   coleta ou recuperacao de FIFO incrementa `sqi.epoch`.
4. **Compatibilidade:** sem campo `sqi.epoch`, a Unity preserva
   o tratamento conservador antigo para `sample.sqi.reason != 0`.
   Para validar o novo caminho, o firmware PRECISA estar gravado.
5. **Com epoch:** divergencia entre o epoch da amostra e do ultimo G1 PASS
   acarreta `G1_EPOCH_CHANGED` e `G1_REVOKED` (reset verdadeiro).
   Um `reason=1` com epoch IGUAL interrompe publicacao e registra
   `G1_REASON_WAIT`/ `WINDOW_SUSPEND`, aguardando novo G1;
   nunca publica usando a aprovacao antiga.
6. **HUD:** `[PPG-HUD]` relata o **texto visual renderizado** e seu
   estado `LIVE`, `WAIT_ESTIMATE`, `G1_BLOCK`,
   `HOLD_VISUAL_ONLY` ou `NO_CONTACT`. `HOLD_VISUAL_ONLY`
   so dura ate 2 s e ate 2 amostras `contact=false` consecutivas,
   com rotulo de ultima leitura. Bridge/Mixer NAO utilizam hold.
7. **G1 reprovado:** reason=4/5/9 e perda confirmada de contato
   continuam bloqueando valores e exigindo recuperacao real.

### Teste de bancada com Unity + firmware

- Compilar e gravar o sketch do firmware **de teste** antes de abrir
  Play Mode da Unity para medir o epoch; o firmware antigo funciona,
  mas apenas exercita o fallback conservador.
- Em bancada: 120 s com dedo fixo; repetir 120 s em gameplay e IA.
- Procurar `[PITACO-QUEUE]` (entrada/saida, `maxFrameGapMs`,
  `maxWaitMs`, `pending`), `[PPG-DIAG]` (`G1_TIMING`,
  `G1_EXPIRED`, `G1_EPOCH_CHANGED`, `G1_REASON_WAIT`,
  `G1_RECOVERY`, `G1_REJECT`, `WINDOW_RESET`,
  `WINDOW_SUSPEND`) e `[PPG-HUD]`.
- Uma pausa da Unity de 4-5 s, sem avancar o relogio do dispositivo
  mais de 6,5 s desde o G1, nao deve causar `G1_EXPIRED`.
- Testar uma oscilacao de 1-2 frames de contato: a HUD pode indicar
  `HOLD_VISUAL_ONLY`, mas nao pode autorizar Mixer e nao deve
  desencadear reset de sessao sem `epoch` alterado.
- Retirar o dedo ate confirmar perda: o epoch muda, o G1 antigo fica
  invalido e o sistema deve aguardar nova janela validada.
- Se `[PPG-HUD] state=LIVE` persistir com texto correto mas o
  usuario ainda observar HUD congelada, investigar objetos Canvas/Text
  da cena, multiplos componentes ou sobreposicao de telas.

**Nao validado em Unity/ESP32 nesta etapa**: conferir compilacao da
versao local e observar resultado antes de promover qualquer branch.
SpO2 DEMO continua nao calibrada e sem validade clinica.
