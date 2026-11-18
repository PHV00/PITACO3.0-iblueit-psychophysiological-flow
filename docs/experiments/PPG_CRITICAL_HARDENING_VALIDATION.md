# PITACO 2.4 — Hardening Unity PPG: plano e evidencias de teste

**Branch:** `test/unity-ppg-critical-hardening` (derivada de `test/unity-ppg-bpm-hysteresis-055`, PR #10)  
**Firmware:** totalmente preservado/selado. **Nao altera G1, MAX30102, USB/FIFO, limiar 0,55 nem janela PPG de 8 s.**  
**Estado:** correcoes submetidas para validacao; compilacao Unity, Editor smoke e hardware pendentes.

## 1. Diagnostico fundamentado nos logs

- O primeiro contato do ensaio anterior ocorreu em t=42,73s. G1 PASS t=47,73s, revogacao pelo firmware t=50,52s, G1 REJECT reason=5 t=55,52s, novo G1 PASS t=60,53s e primeiro BPM aceito t=74,55s: **31,82s desde o contato**, mas com falhas fisicas/de SQI intermediarias. Segunda recuperacao: t=91,09s -> 99,48s = **8,39s**, proximo do minimo de 8 segundos exigido pelo estimador.
- Limiar 0,57 vs 0,55: 38 vs 39 aprovacoes em 67 decisoes (ensaio anterior). Alterar novamente o limiar sem referencia nao e justificado.
- Falhas `G1_REVOKED sampleReason=1`, `G1_REJECT reason=5` e `CONTACT_LOST consecutive=3` devem manter invalidacao real, nunca ser encobertas por um hold.
- Em outro trecho, `UNITY_FRAME_STALL=5,07s`, `serialPeakPending=226`, `serialPeakQueueLagMs=4984`, `serialDropped=0`. A fila preservou os dados, mas leituras antigas eram publicadas com idade apenas do processamento Unity.
- O teste experimental do Mixer havia habilitado regras legadas de oxigenacao para SpO2 **DEMO nao calibrado**, incluindo classificacao de 84,5% como PERIGO: isso nao deve acontecer por padrao.
- CSV fornecido anteriormente `pitaco24_ppg_20261118_150710.csv`: 77 janelas estaveis de 8s reproduzidas offline com autocorrelacao IR >0,82, sem prova de acuracia fisiologica (nao ha referencia externa). Em perda de contato registrada, RED/IR despencam de dezenas de milhares para ~2000 contagens. Isso nao e uma falha que possa ser ignorada na Unity.

## 2. Modificacoes realmente feitas

1. **Protecao DEMO por padrao:** `OximetrySnapshotFactory.EnableDemoSpo2LegacyRulesForTesting` inicia em `false`. O componente `OximetryDataBridge` so habilita as regras DEMO se o operador marcar explicitamente `allowUncalibratedSpo2GameRulesForTesting` no Inspector. Em `OnDisable` volta para false. **Nunca habilitar em teste com paciente nem em distribuicao.** O oximentro legado independente mantem suas regras originais.
2. **Frescor realista depois de travamento:** `PitacoConnectionManager.CurrentDispatchQueueLagMs` expoe o atraso entre enqueue da linha e entrega sincronica de seu callback. `OximetryDataBridge` soma esse atraso a idade da medicao; `PpgProcessingCoordinator` impede mostrar como LIVE no HUD publicacoes cujo proprio atraso foi >2s. A fila preserva todos os dados; G1 e contato permanecem obrigatorios.
3. **Evita trabalho repetitivo na main thread:** buscas de `SerialControllerOximetro` no Bridge e `Spawner` no Mixer passam a ter retry maximo de 1/s quando os objetos nao existem. A ausencia temporaria de Spawner nao pode autorizar regras de SpO2 ou provocar dereferencia nula no ramo de adaptacao.
4. **Teste automatizavel dentro do Unity Editor:** `Assets/Editor/PpgCriticalHardeningSmokeTests.cs` adiciona o menu `Tools > PITACO > Run PPG critical hardening smoke tests` com 15 assercoes em Edit Mode, sem hardware: politica DEMO, bloqueio G1/contato, frescor incluindo atraso, legado, janela exata de 8s, BPM senoidal conhecido e descontinuidade de sequencia. O smoke sempre desliga o opt-in DEMO no `finally`.

## 3. Testes que devem ser realizados no Unity

**A — Edit Mode (obrigatorio):**
1. Abrir projeto nesta branch, aguardar compilacao sem erros.
2. Clicar `Tools > PITACO > Run PPG critical hardening smoke tests`.
3. Esperado: `[PPG-TEST] PASS: 15 critical-hardening smoke assertions (Edit Mode).` Verificar ausencia de `FAIL`.

**B — Fluxo normal via MAX30102/COM:**
1. Inspector: `allowUncalibratedSpo2GameRulesForTesting=false` (padrao).
2. Sem dedo: `contact=False`, `G1_REJECT`; nenhum BPM ou SpO2 liberado ao Mixer.
3. Com dedo estavel: primeiro `CONTACT_PRESENT`, `G1_PASS`, janela de **8s** e `BPM=numero` apenas quando `bpmReason=OK`. Capturar `bpmTrials,bpm057Pass,bpm055Pass,bpmPublished`.
4. `[SpO2Grid-Test]` deve registrar `legacyRules=False` para PITACO DEMO, mesmo com um `spo2Demo` numerico. Oximentro legado separado, se conectado, e excecao intencional.
5. Rejeicoes de BPM: verificar `BPM_REJECT_STREAK` e `BPM_HOLD_VISUAL_ONLY` apenas no HUD por ate 2 recalculos (<2s); no Bridge `bpm=--` imediatamente.

**C — Travamento de cena / fila:**
1. Abrir a cena Plataform durante transmissao normal, reproduzir carregamento/ML-Agents.
2. Registrar `UNITY_FRAME_STALL`, `[PITACO-QUEUE]`, `serialDropped` e `publishQueueLagMs`.
3. Quando `publishQueueLagMs > 2000`, a publicacao **nao** deve ser elegivel no Bridge; HUD nao deve tratar este resultado como LIVE. Em dados novos, backlog esvazia e a elegibilidade deve voltar.
4. Confirmar `serialDropped=0`, `invalidJson=0`, `sequenceGaps=0` no trecho normal. **Nao afirmar que a causa do stall foi resolvida** sem Unity Profiler: inspecionar carregamento de cena, `Academy.InitializeEnvironment`, GC, logging e componentes `Missing Script`.

**D — Contato fisico e seguranca:**
1. Retirar o dedo: tres `contact=false` consecutivos devem gerar `CONTACT_LOST`, `WINDOW_RESET` e bloquear Mixer. Nao forcar uma estimativa durante a perda.
2. Reposicionar: aguardar novo veredito G1 e janela real de 8s. Medir tempo a partir de `CONTACT_PRESENT`; criterio de objetivo para contato estavel: aproximadamente 8–10s, nao promessa de SLA.
3. Induzir `G1_REJECT`/reinicio de G1: verificar ausencia de uso do BPM anterior.
4. O opt-in DEMO nao deve ser habilitado na validacao geral. Se for necessario exercitar PAINEL 1/2 em **teste isolado**, ativar conscientemente no Inspector e desativar apos o experimento.

## 4. Criterios e limites

| Item | Esperado | Evidencia |
| --- | --- | --- |
| Edit Mode | 15/15 assercoes aprovadas | Console `[PPG-TEST]` |
| Seguranca SpO2 DEMO | Desabilitado por padrao; sinal DEMO continua visivel com rotulo | Bridge + Mixer |
| Fila serial | Zero descarta em sessao nominal; backlog pode ocorrer no carregamento | `[PITACO-QUEUE]` |
| Dados atrasados | `effectiveAgeS = timeSincePublication + packetQueueLagMs/1000` | `BRIDGE_TRANSITION ageS,publishQueueLagMs` |
| G1 e contato | Sem bypass, reset em falhas reais | `G1_REJECT/G1_REVOKED/CONTACT_LOST` |
| BPM | Estimado somente com autocorrelacao >=0,55, sem substituir por candidato rejeitado | `CALC/BRIDGE` |
| Latencia inicial | Mensurada em cada ensaio; nao muda minimo 8s | `CONTACT_PRESENT, G1_PASS, CALC` |
| Stall Unity | **Ainda precisa de perfil** e possivel conserto adicional | Unity Profiler |

## 5. O que nao foi corrigido automaticamente

A variabilidade de autocorrelacao em contato ruim, falhas fisicas de G1 e o tempo minimo de 8 segundos **nao sao bugs seguros de corrigir alterando limiares na Unity**. Uma melhora adicional exige replay com sinal bruto em janelas rejeitadas, referencia BPM independente e, idealmente, fixacao optica do dedo. A reducao dos `FindObjectOfType` diminui trabalho evitavel, mas nao prova nem elimina o stall do Unity. Valores de SpO2 DEMO **nao sao clinicamente validos**.
