# Consolidação técnica — PITACO 2.4 no I Blue It 5.0 (22/11/2026)

**Repositório:** `PHV00/iblueit-psychophysiological-flow-pitaco2.4-port`  
**Branch de consolidação:** `test/unity-ppg-monotonic-clock-alignment`  
**Base de referência para o PR:** `main`, commit `3e2f7faaad37499effe8b4663229a07b07e15e3e` (Marco 1.5 já incorporado)  
**Natureza:** desenvolvimento e validação **experimental em bancada**, não dispositivo clínico.  
**Escopo físico/firmware:** este conjunto de alterações do repositório Unity **não inclui código de firmware nem a regravação física do ESP32**. A versão exata gravada no dispositivo não é identificada pelo protocolo observado.

## 0. Síntese executiva e delimitação de aceite

A branch acumula incrementos posteriores ao Marco 1.5: integração source-aware da oximetria com Mixer/DeepDDA, manutenção do caminho legado, observabilidade granular da captura PPG e da comunicação USB, melhoria de estados de contato/G1 e recuperação de interface, mecanismos de frescor temporal, experimento de autocorrelação 0,57/0,55, proteções da SpO₂ DEMO e HUD de captura atual comparada a médias móveis dos últimos 60 segundos.

A prova funcional **não** demonstra exatidão fisiológica de BPM, calibração de SpO₂, segurança clínica, aprendizagem online do DeepDDA, aprovação automatizada do Test Runner ou ausência de problemas de cena. Um PR pode ser apresentado e auditado, mas não deve ser confundido com aceite para produção clínica.

## 1. Marcos cronológicos e seus limites

| Marco / etapa | Principais entregas | Limite do aceite |
| --- | --- | --- |
| Marco 0 (histórico anterior à base) | Descoberta serial PITACO 2.4, comandos, preservação do PITACO 2.0, arquitetura de integração na Unity | Já incorporado em `main` pelo PR #4; não é uma alteração nova deste diff |
| Marco 0.5 / 1 / 1.5 (histórico anterior à base) | MVP BPM por autocorrelação IR, SpO₂ DEMO RED/IR, contato óptico e G1, HUD nos minigames, tolerância breve de contato e UI de reposicionamento | Parte do baseline incorporado pelo PR #5; sujeito a gates e sem validação clínica |
| Marco 2 — fonte de oximetria e Mixer | `OximetryDataBridge` persistente; `OximetrySnapshot` com fonte, elegibilidade, idade e valores opcionais; consumo por Mixer; regras de SpO₂ segregadas do oxímetro legado | Integração funcional experimental, não adição de BPM/SpO₂ como observações ONNX |
| Marco 2 — instrumentação da adaptação | Counters, transições, painéis, pausa, chamadas `AddReward`; perfil de limiares PITACO de teste 89/95 sem modificar parâmetros legados | Chamadas de recompensa e painéis registradas não comprovam treinamento ou precisão da oximetria |
| Marco 3 — diagnóstico PPG | Logs de reason G1, contato, reset, AC/DC, autocorrelação, relação R, rejeições; `PpgRawG1Probe` read-only; relatórios de hipóteses e coleta CSV offline | As estatísticas shadow da Unity não são necessariamente a mesma janela de decisão do firmware |
| Marco 3 — fluxo temporal | Diferenciação de telemetria/contato/G1; fila USB instrumentada e drenagem limitada; suspensão sem publicação em expiração ambígua; alinhamento monotônico worker ↔ firmware e proteção contra dados velhos | Alinhamento não mede latência USB absoluta; travamentos de cena ainda aparecem |
| Marco 3 — BPM e HUD | Comparação 0,57 vs 0,55 na mesma aquisição; diagnóstico IR/RED; hold **somente visual** de até duas rejeições e 2 s; UI sincronizada com coordenador e não com cache `Player` | Retenção não autoriza publicação no Mixer; limiar 0,55 ainda requer validação contra referência |
| Marco 3 — proteção de gameplay | `DEMO` inelegível por padrão; opt-in de testes no Editor/Development; release bloqueia explicitamente o uso da DEMO nas regras de saturação | O opt-in aparece ativo no último log de bancada; deve ser desabilitado para cenários não experimentais |
| Marco 3 — correção de cena | PITACO 2.4 pula a recalibração automática após 10 s; PITACO legado conserva comportamento | Verificado em log por `SKIP_RECALIBRATE`; regressão total de cenas ainda pendente |
| HUD de acompanhamento | Layout em três linhas com `BPM atual | 60s` e `O2 DEMO atual | 60s`; histórico móvel visual, só de estimativas aprovadas, 1 registro/s por métrica | Histórico não substitui a medida atual; não alimenta IA e limpa em nova sessão/perda confirmada |

## 2. Arquitetura atual e separação das responsabilidades

```text
MAX3010x / PITACO 2.4 (firmware externo, USB)
 ├─ amostras JSON: seq, t_ms, RED, IR, contato
 ├─ G1 a cada ~5 s: PASS/FAIL, reason, samples
 └─ fluxos de pressão/respiração legados preservados
       |
       v
PitacoConnectionManager + SerialControllerPitaco24
 ├─ identifica COM exclusiva e versão sem disputar portas
 ├─ leitura em worker, filas e limites de orçamento por frame
 └─ instante monotônico do recebimento da linha USB
       |
       v
PpgProcessingCoordinator
 ├─ confirma contato e controla G1 no tempo do dispositivo
 ├─ registra probe RAW somente diagnóstico
 ├─ acumula ~8 s de PPG, detecta descontinuidade
 ├─ BpmEstimator (IR; diagnóstico RED separado)
 ├─ Spo2Estimator (RED/IR, fórmula DEMO não calibrada)
 ├─ protege contra frames antigos e regula HOLD apenas visual
 └─ mantém médias móveis de 60 s EXCLUSIVAMENTE para o HUD
       |                                     |
       | OnMeasurement                      | HudMeasurement / GetHudTrend60Seconds
       v                                     v
OximetryDataBridge                   OximetryHudPresenter
 ├─ Source (Pitaco24/Legacy)          ├─ estado e captura ATUAL
 ├─ contato, G1, idade temporal        ├─ BPM atual | média até 60 s
 ├─ calibração/elegibilidade           └─ O2 DEMO atual | média até 60 s
 └─ não reutiliza HOLD visual
       |
       v
MixerManager + MixerAdaptationGrid
 ├─ regras existentes de gameplay e oxímetro legado
 ├─ teste experimental de SpO₂ (opt-in controlado)
 └─ chamadas de recompensa/painéis condicionadas à elegibilidade
       |
       v
DeepDDAAgent (ML-Agents / ONNX existente)
 ├─ 16 observações próprias de desempenho/respiração
 ├─ mesmas ações do modelo preexistente
 └─ recebe recompensas indiretas; RED, IR, SpO₂ e BPM NÃO
    foram adicionados diretamente à entrada vetorial ONNX
```

### Invariantes críticos preservados

1. G1 é decidido pelo firmware. `UNITY_RAW_SHADOW_NOT_FIRMWARE` não tem autoridade para aceitar uma janela.
2. Contato, janela de 8 s, G1 vigente e estimadores determinam publicação em `LastMeasurement`. Falha => campo nulo, não número extrapolado.
3. `HudMeasurement` pode representar leitura retida por no máximo 2 s em casos definidos; não é uma nova publicação e não deve alimentar `Bridge`, `Mixer` ou IA.
4. Histórico de 60 s é visual, mantém contagens independentes BPM/SpO₂, não preenche lacunas; `--` na captura atual não é corrigido artificialmente pela média.
5. Fonte legada conserva o contrato independente de alertas; PITACO 2.4 não deve fazer fallback silencioso para valores legados no HUD enquanto ele está ativo.
6. `SpO2CalibratedForLegacyRules=false` para o estimador DEMO. Release build não pode habilitar a autorização experimental.
7. Uma queda de dados em USB não deve se transformar em leitura recente pelo simples fato de a Unity processá-los tardiamente.

## 3. Melhorias de código agrupadas por módulo

### 3.1 Serial, conexão e continuidade

- `Core/Serial/PitacoConnectionManager.cs`: identificação de versão/COM, fila com `PendingLines`, `PeakPendingLines`, `DroppedLines`, `PeakQueueLagMs`, orçamento e drenagem em lotes, registro do tempo de entrada pelo worker.
- `Core/Serial/SerialControllerPitaco24.cs`: despacho de JSON e diagnóstico de firmware sem ruído excessivo.
- `PpgMonotonicClockAligner.cs`: estima alinhamento de `millis()` com relógio monotônico, com lock inicial, drift limitado, diagnóstico de gap, rollover `seq` 16 bits e `millis` 32 bits, e sinalização de epoch suspeita.
- `PpgProcessingCoordinator.cs`: usa tempo de dispositivo para G1, evita falso vencimento após congelamento do Editor e evita aprovar medidas da epoch anterior.
- `StageManager.cs`: não emitir recalibração do 10º segundo para PITACO 2.4.

### 3.2 PPG, integridade e BPM/SpO₂ DEMO

- `PpgRawG1Probe.cs`: observação read-only de RED, IR, intervalo, médias/ranges, clipping e lacunas, rotulando shadow e não-firmware.
- `PpgProcessingCoordinator.cs`: estados Waiting/Passed/Failed/Stale, ações Allow/Suspend/Reset, distinção perda de contato/telemetria, logs de resets e bloqueios, invalidade de leituras antigas.
- `BpmEstimator.cs`: autocorrelação IR com limiar baseline 0,57 e experimental 0,55, com diagnósticos de candidato e autocorrelação RED sem alterar fonte autoritativa.
- `Spo2Estimator.cs`: rejeições explicitadas (razão AC/DC, reconstrução, domínio e limites); o cálculo continua demonstrativo.
- `PpgSampleBuffer`: a janela de 8 s e seus limites continuam sendo mecanismo de cálculo, não a tabela de 60 s.
- Falhas G1 específicas de RED ainda podem invalidar integralmente ambos os estimadores: isto é uma **limitação conhecida**, não uma solução concluída.

### 3.3 Bridge, Mixer e DeepDDA

- `Integration/OximetryDataBridge.cs`: fonte, disponibilidade/idade, leitura atual e consumo efetivo pelo Mixer separados de valores visuais.
- `MixerManager.cs` e `MixerAdaptationGrid.cs`: transições de elegibilidade e teste 89/95 específico do PITACO, sem sobrescrever o perfil respiratório legado. Provas experimentais de `AddReward`, contadores e lógica de painéis.
- `DeepDDAAgent.cs` e `Plataform/Target/Target.cs`: correções de observações finitas, descarte de eventos/ciclo de vida de alvos, preservação do modelo ONNX. Isto não é retreinamento nem inclusão de novos sinais PPG no vetor.
- Segurança: opt-in de SpO₂ DEMO **somente Editor/Development**, desabilitado automaticamente em release comum. Ainda se exige revisão do opt-in da cena antes de demonstrar o jogo fora do ensaio.

### 3.4 HUD e experiência de uso

- `Shared/UI/OximetryHudPresenter.cs`: fonte da captura no coordenador, estados sem contato/G1/aguardando/live, retidos rotulados com `*` e apresentação em 3 linhas para caber no `Text` existente.
- `PpgProcessingCoordinator.cs`: filas leves e independentes para médias móveis visualizadas, 1 leitura aprovada por segundo por métrica em janela de 60 segundos; reinício do histórico por perda de contato confirmada, nova sessão ou descontinuidade.
- Exemplo:
```text
PITACO 2.4: conectado
BPM atual: 57.8 | 60s: 57.0
O2 DEMO: 99.7% | 60s: 99.6%
```
- Valores acima são apenas exemplo de uma captura de bancada, sem significado clínico para O₂ DEMO.

## 4. Evidência do último ensaio, após ajuste físico dos cabos

**Fonte:** arquivo de console fornecido em 22/11/2026, `Texto colado(20261122-152016).txt` (original permanece com o autor; não comitar dados brutos no GitHub).

### 4.1 Indicadores até o último SUMMARY, t=194,92 s

| Indicador | Observado | Interpretação permitida |
| --- | --- | --- |
| G1 | 34 janelas, 30 PASS / 4 FAIL | Três FAIL por `reason=2` antes de colocar o dedo, um por `reason=4` com contato |
| BPM | 315 publicações / 338 tentativas = 93,2% | Proporção de cálculos, não % do tempo nem acurácia |
| Comparação autocorrelação | `bpm057Pass=313`, `bpm055Pass=315` | 2 decisões adicionais com 0,55 neste ensaio; não 2 medições comprovadamente corretas |
| Recebimento | `json=4315`, `samples=4281` | Contadores agregados, incluindo mensagens G1 não-amostra |
| Parser e continuidade | `invalidJson=0`, `invalidSample=0`, `serialDropped=0`, `clockSampleGaps=0`, `clockRestarts=0` | Nenhuma falha dessas categorias registrada |
| Perda de contato confirmada | `contactLossConfirmed=0` | Sem perda confirmada DURANTE aquisição útil até parada, não prova montagem perfeita |
| Fila no pico de carregamento | `serialPeakPending=212`, `serialPeakQueueLagMs=4641` | Pausa do Editor/cena, com recuperação da fila e sem descarte registrado |
| Travamento Unity | `UNITY_FRAME_STALL gapS=4,70` | Não confundir atraso de renderização com atraso de conversão ADC |
| Recalibração 10 s | `action=SKIP_RECALIBRATE` | Correção de cena observada para PITACO 2.4 |
| HUD | `rendered=PITACO 2.4: conectado / BPM atual: 57.8 / 60s: 57.0 / O2 DEMO: 99.7% / 60s: 99.6%` | Confirma string renderizada pela lógica de HUD; inspeção visual de dimensões ainda apropriada |
| DeepDDA/SpO₂ | `source=Pitaco24`, `mixerRecent=True`, `eligible=True` durante opt-in | Confirma caminho de integração, não aprendizado de rede nem exatidão clínica |

### 4.2 Eventos e interpretação

- **t=19 a 34 s:** sem dedo (RED/IR próximos a ~1150), G1 reason=2, comportamento esperado de não-publicação.
- **t=34,34 s:** contato detectado, RED/IR sobem; os estimadores aguardam a janela contínua de 8 s.
- **t=39,34 s:** primeiro G1 PASS após contato; `buffer=126`, ainda não há 8 s completos.
- **t=44,86 s:** primeiro cálculo com `BPM=92,3`, `autocorr=0,562` e `SpO2 DEMO=77,9` durante transiente; depois o sinal se estabiliza. Não interpretar o valor inicial como real.
- **t=47,72–73,11 s:** estimações repetidas aproximadamente 58–63 BPM; autocorrelações IR posteriores elevadas e SpO₂ DEMO próxima a 99,7–99,8.
- **t=74,33 s:** `G1_REJECT reason=4`, shadow `redRange=484`, `irRange=1159`, `contact=True`. O software descarta 206 amostras e impõe recomposição da janela. A métrica shadow **não prova** qual limiar está gravado no ESP32; requer identificação do firmware.
- **t=79,34 s:** G1 recupera, mas janela começa a acumular novamente.
- **t=82,38–86,14 s:** transientes e baixa autocorrelação IR, com SpO₂ DEMO candidata em ~79% e ~76%; o caminho experimental de regras do jogo ainda a considerou elegível em alguns momentos. **Risco de artefato falso** no gameplay.
- **t=87,34 s em diante:** melhora sustentada; vários G1 PASS consecutivos até a janela 34 (`t=189,33 s`), BPM em torno de 55–63 e SpO₂ DEMO em torno de 99,7–99,9 em muitos registros.
- **t~190 s:** comando `f`, firmware comunica `STOPPED`; depois de ~3 s sem PPG, Unity corretamente reporta `TELEMETRY_LOST` e limpa a captura e histórico. Isto é coerente com encerramento do ensaio, não prova desconexão física.
- Três mensagens `The referenced script (Unknown) on this Behaviour is missing!` aparecem na troca de cena. Nenhum `NullReferenceException` nem aviso de truncamento 16→1 apareceu neste arquivo, mas isso não valida todas as cenas/configurações.

### 4.3 Causalidade e alcance

O usuário relatou **ajuste dos cabos** imediatamente antes desta captura, e o período estável melhorou. Isso é **compatível** com influência da montagem/conexão no sinal, mas não comprova isoladamente a causa: posição do dedo, pressão, perfusão, luz ambiente e estado da sessão também variam entre ensaios. Não atribuir precisão clínica à SpO₂ porque a DEMO ficou perto de 100%.

A única reprovação RED_FLAT com `redRange=484` (shadow) continua sendo motivo para confirmar versão/threshold real gravado, inclusive se a branch externa experimental RED=350 foi testada. Este PR do repositório Unity não incorpora aquela alteração de firmware.

## 5. Testes existentes no repositório e evidência de execução

| Arquivo / ferramenta | Cobertura prevista no código | Execução comprovada nesta consolidação |
| --- | --- | --- |
| `Assets/_Game/Scripts/Editor/Unit Tests/PpgProcessingTests.cs` | Amostras sintéticas, janela 8 s, G1, contato, resets, estimadores, contratos HUD/legacy | **Não executado nesta sessão** |
| `Assets/_Game/Scripts/Editor/Unit Tests/OximetryDataBridgeTests.cs` | Elegibilidade, fontes, números inválidos, dados velhos, opt-in DEMO, prioridade do legado | **Não executado nesta sessão** |
| `Assets/_Game/Scripts/Editor/Unit Tests/PpgMonotonicClockAlignmentTests.cs` | 12 testes: entrada USB, atrasos, freeze, warmup, wrap, epoch e frescor | **Não executado nesta sessão** |
| `Tools > PITACO > Run PPG critical hardening smoke tests` | Checagens sintéticas focadas em guardas críticos | **Não executado nesta sessão** |
| Logs Unity com hardware, 22/11/2026 | Captura ao vivo, G1, HUD, fila, Bridge e processamento | **Executado e log fornecido**, com limites acima |
| Referência externa de BPM/SpO₂ | Comparar erros e exatidão real | **Não fornecida** |

Para antes de merge: abrir no Unity, compilar scripts, rodar EditMode e smoke, validar Plataform + WaterGame + CakeGame, PITACO 2.0 legado, estados com/sem contato/G1, modo de release e checar ausência de scripts perdidos. Verificar se a string HUD realmente cabe nas caixas existentes em diferentes resoluções.

## 6. Pendências, dívida técnica e propostas deliberadamente NÃO incluídas

1. **Separar elegibilidade IR para BPM e RED+IR para SpO₂.** Hoje `reason=4` no RED revoga ambos mesmo com IR possivelmente bom. Exige critérios próprios e regressão, não apenas ignorar o G1.
2. **Política de reset por gravidade.** Diferenciar perda óptica, lacuna real e sinal limítrofe com contato, sem reutilizar amostras contaminadas ou publicar leitura reprovada.
3. **Baseline paralelo sem SQI de publicação.** Executar estimadores simples em caminho de diagnóstico, contra o mesmo sinal bruto e referência externa, sem alimentar o gameplay até validação.
4. **Identidade real do firmware carregado.** Expor versão/commit e limiares G1 em telemetria; o `refMinRange=500` da Unity é apenas shadow.
5. **Controle físico reprodutível.** Revisar fixação, cabos, pressionamento do dedo, luz, alimentação e CSV sincronizado com eventos G1.
6. **SpO₂:** fórmula atual é DEMO, sem calibração, e transientes podem produzir alertas/recompensas experimentais. Desabilitar opt-in por padrão em cenários não experimentais; nunca usar como alarme de paciente.
7. **DeepDDA:** rede existente recebe suas 16 observações e `AddReward` indireto, mas não BPM/SpO₂ como entradas. Comprovar em PlayMode observações sem truncamento, ações, instância do ONNX e ausência de scripts ausentes; não afirmar aprendizado online.
8. **Test Runner/CI:** sem aprovação desta rodada; aguardar testes em Unity e revisão.
9. **Dependência em PRs empilhados:** PRs #6 a #12 possuem partes deste conjunto. Escolher **um único caminho de merge** (consolidação para `main` OU PRs empilhados) para evitar descrições e revisões duplicadas.
10. **Registros de testes:** manter os logs brutos privados/locais; documentar só métricas agregadas e eventos necessários.

## 7. Referências internas de revisão

- `docs/architecture/OXIMETRY_ML_AGENTS_INTEGRATION.md`
- `docs/marcos/MARCO_02_INTEGRACAO_PITACO24_MIXER_DEEPDDA_FECHAMENTO.md`
- `docs/experiments/PPG_QUALIDADE_SINAL_DIAGNOSTICO_MARCO_03.md`
- `docs/experiments/PPG_DIAGNOSTICO_DECISOES_GATE_ESTIMADORES.md`
- `docs/experiments/PPG_ETAPA_1_ESTADOS_TELEMETRIA_G1.md`
- `docs/experiments/PPG_SERIAL_QUEUE_DIAGNOSTICS.md`
- `docs/experiments/PPG_BPM_THRESHOLD_HYSTERESIS_055.md`
- `docs/experiments/PPG_CRITICAL_HARDENING_VALIDATION.md`
- `docs/experiments/PPG_MONOTONIC_CLOCK_ALIGNMENT_TEST_PLAN.md`
- `docs/experiments/PPG_OPTICAL_CONTACT_FAILURE_TRACING.md`
- `docs/testing/PITACO24_SPO2_ADAPTATION_GRID_LOG_TEST.md`
- `Assets/Editor/PpgCriticalHardeningSmokeTests.cs`
- `Assets/_Game/Scripts/Editor/Unit Tests/PpgProcessingTests.cs`
- `Assets/_Game/Scripts/Editor/Unit Tests/OximetryDataBridgeTests.cs`
- `Assets/_Game/Scripts/Editor/Unit Tests/PpgMonotonicClockAlignmentTests.cs`

## 8. Estado de conclusão proposto

**Encerrar o ciclo de documentação e implementação experimental na branch, disponibilizando-o para revisão por PR.** Não dar aceite clínico, não realizar merge até testes da Unity e verificação explícita do opt-in DEMO e scripts ausentes. Futuras melhorias de SQI/recuperação/baseline simples devem ser propostas em PRs próprios, mantendo esta consolidação auditável.
