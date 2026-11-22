# Marco 03 — Fechamento da integração experimental PITACO 2.4 / Unity

**Data:** 22/11/2026  
**Repositório:** `PHV00/iblueit-psychophysiological-flow-pitaco2.4-port`  
**Branch de consolidação:** `test/unity-ppg-monotonic-clock-alignment`  
**Destino aprovado pelo mantenedor:** `main`  
**PR:** [#13 — Consolidação Marcos 2–3](https://github.com/PHV00/iblueit-psychophysiological-flow-pitaco2.4-port/pull/13)  
**Correspondência firmware:** `PHV00/PITACO_ESP32`, `docs/marcos/MARCO_03_FECHAMENTO_PPG_SQI_2026-11-22.md` (branch `test/ppg-g1-redflat-350` antes do merge).

## 1. Declaração de encerramento do ciclo

O mantenedor autorizou incorporar na `main` de **ambos** os repositórios o estado atual do ciclo de desenvolvimento como **Marco 03** e prosseguir com novas hipóteses em etapa separada. Esta decisão diz respeito à **integração funcional experimental e à rastreabilidade** — **não** significa que os dados PPG sejam clinicamente válidos, que a SpO₂ DEMO esteja calibrada ou que as revisões pendentes estejam concluídas.

O relatório completo fica em [CONSOLIDACAO_PITACO24_UNITY_PPG_DEEPDDA_HUD_2026-11-22.md](./CONSOLIDACAO_PITACO24_UNITY_PPG_DEEPDDA_HUD_2026-11-22.md).

A `main` antes desta consolidação já continha o Marco 0 (PR #4) e Marco 1.5 (PR #5). O escopo novo concentra-se no pós-M1.5: integração de oximetria com Mixer/DeepDDA, qualidade e diagnóstico, resiliente ingestão serial, relógio monotônico, salvaguardas de DEMO, HUD atual e 60 s.

## 2. Entregas encerradas como código experimental

| Frente | Entregas em Unity | Evidência e limites |
| --- | --- | --- |
| Integração funcional | `OximetryDataBridge` faz roteamento de fontes PITACO 2.4/legado, elegibilidade e consumo Mixer | Logs `BRIDGE_TRANSITION AVAILABLE` e `mixerRecent=True`; sem novas observações PPG no ONNX |
| Gameplay/IA | `MixerAdaptationGrid` registra contadores, painéis e recompensas, mantendo modo inferência ML-Agents | `AddReward` não demonstra treinamento; o opt-in DEMO só é permitido em Editor/Development |
| Integridade PPG | G1 firmware recebido; processamento IR/RED e suas rejeições; probe shadow só diagnóstico | reason=4 pode descartar buffer C# mesmo com contato confirmado; pendência futura |
| Temporalidade | Clock monotônico/USB worker e idade estimada; fila serial com métricas, drenagem limitada, detecção de travamentos | Verificado com log de stall ~4,70 s e 0 drops, mas cena ainda trava temporariamente |
| Cálculo BPM | Diagnóstico de autocorrelação IR e RED; comparação no mesmo fluxo 0,57/0,55; holding visual curto, sem envio ao Mixer | Publicações contam estimativas aprovadas, não validação contra referência |
| SpO₂ | Relação AC/DC RED/IR e estimativa DEMO marcada como não calibrada | Regra de gameplay experimental pode reagir a artefatos; não usar como alarme clínico |
| UI | 3 linhas de status; `BPM atual | 60s` e `O2 DEMO atual | 60s`; média móvel visual só de publicações válidas | Sem preenchimento de períodos ausentes ou alimentação da IA por médias |
| Compatibilidade | PITACO 2.0 e oxímetro legado preservados, sem recalibração automática dos 10s para PITACO 2.4 | Regressão completa das cenas e legado ainda exigida |

## 3. Evidência da última captura e interpretação

**Entrada:** log fornecido em 22/11/2026 após ajuste físico dos cabos; métricas finais em t=194,92 s:
- G1: **30 aprovações em 34 avaliações**. Três `reason=2` antes do contato, uma `reason=4` durante contato.
- BPM: **315 publicados em 338 tentativas (93,2%)**; limiar 0,55 concedeu somente duas decisões a mais que 0,57.
- Captura: `invalidJson=0`, `invalidSample=0`, `serialDropped=0`, `clockRestarts=0`, `clockSampleGaps=0` e `contactLossConfirmed=0` no resumo final.
- `[PITACO-CAL] ... SKIP_RECALIBRATE`: a correção do estágio foi observada.
- `[PPG-HUD]`: status conectado, BPM atual/média 60s e O2 DEMO atual/média 60s aparecem na string aplicada ao HUD.
- `source=Pitaco24`, `eligible=True` em períodos válidos, `mixerRecent=True`: caminho de integração ativo no ensaio com opt-in DEMO.
- Evento remanescente `RED_FLAT` em ~74,33 s: shadow `redRange=484`, `irRange=1159`, G1 firmware `reason=4`, buffer C# descartado (206 amostras).
- Travamento `UNITY_FRAME_STALL=4,70s`, backlog máximo 212, atraso máximo 4641ms, sem drops; três avisos de script desconhecido ausente.

O ajuste de cabos antecedeu melhora observada, mas a gravação **não permite identificar causalidade exclusiva**, nem qual limiar do firmware foi efetivamente gravado. O arquivo RAW original do log não deve ser adicionado ao repositório.

## 4. Contexto do firmware e delimitação da versão

O repositório `PHV00/PITACO_ESP32` possui uma branch de experimento `test/ppg-g1-redflat-350` (RED=350 e IR=500). Essa mudança **não está neste diff Unity**. A integração de ambos à `main` documenta um mesmo marco de desenvolvimento, mas **não prova** que o ESP32 físico recebeu uma compilação dessa branch. A Unity mantém seu `refMinRange=500` **apenas diagnóstico shadow**, e não deve ser usado como versão do firmware.

## 5. Pendências formalmente transferidas para a próxima etapa

1. Separação de SQI/aptidão IR para BPM e RED+IR para SpO₂: não bloquear BPM confiável automaticamente por RED_FLAT.
2. Revisão da política de reset: distinguir reprovação por amplitude e falha real de aquisição/contato sem permitir estimativas inválidas.
3. Baseline de cálculos diretos paralelo ao SQI, somente diagnóstico e sujeito a referência externa.
4. Identificação do firmware gravado, temperatura/pressão/contato óptico e suporte físico/cabos, com ensaios RAW sincronizados.
5. SpO₂ DEMO: validar calibração antes de qualquer uso fisiológico; manter proibição de regras em release e opt-in apenas para bancada.
6. Deep DDA: revisar 16 observações, referências ausentes na cena e rastrear ações/recompensas ONNX sem inferir treinamento online.
7. Resolver `UNITY_FRAME_STALL` e a origem dos 3 scripts ausentes; rodar EditMode, smoke tests, PlayMode e build/release.
8. Validar BPM com referência independente; medir erro, cobertura temporal e recuperação além da taxa de aprovação.

## 6. Situação dos PRs e políticas de merge

Os PRs incrementais **#6–#12** ficaram empilhados sobre branches intermediárias. O **PR #13** é a consolidação contra `main` e constitui a via selecionada para integrar o Marco 03. Evitar merges separados posteriores da mesma cadeia sem nova análise de diferenças (risco de duplicidade e histórico confuso).

**Status:** entrega encerrada **como Marco 03 experimental** por solicitação explícita do mantenedor; as pendências acima passam ao próximo ciclo, sem bloquear o registro histórico. Este fechamento não é declaração de aprovação clínica, de todos os testes de Unity ou de release pronto para pacientes.
