# PITACO 2.4 — teste observacional da regra de SpO2 no AdaptationGrid

**Branch isolada:** `test/pitaco24-spo2-adaptation-grid-logs`  
**Base:** `feature/ppg-csharp-bpm-spo2-mvp`  
**Status:** instrumentação preparada; execução e validação ainda pendentes.

## Escopo e hipótese

Verificar se uma leitura **SpO2 DEMO não calibrada**, quando autorizada pelo
Bridge para **teste funcional**, percorre a cadeia:

```text
PITACO 2.4 -> PpgProcessingCoordinator -> OximetryDataBridge
           -> MixerManager.CurrentLegacyRulesOximetry
           -> MixerAdaptationGrid
           -> classe / contador -> painel, pausa ou recompensa
```

Neste experimento, `EnableDemoSpo2LegacyRulesForTesting = true` já está
habilitado **na branch de origem**. Não modificar G1, estimadores, thresholds,
spawner, fluxo respiratório ou observações/ONNX do DeepDDA durante o teste.

**Não utilizar com pacientes ou como monitoramento médico:** a SpO2 do PITACO
continua `demo=True` e estimativas incorretas podem acionar painéis e pausas
falsos. Não tentar provocar dessaturação fisiológica para atingir limiares.

## Evidência de referência: Console de 17/11/2026

Extraído do log enviado na conversa (`Texto colado(20261117-023045).txt`),
que serve como **linha de base**, não como resultado da instrumentação nova:

- 25 registros `[OximetryBridge->Mixer]`, sendo **8** com
  `legacyRules=True` e **17** com `legacyRules=False`.
- No último diagnóstico: `JSON serial=2824`, `callbacks=2824`,
  `samples=2805`, `JSON invalido=0`, `amostraInvalida=0`;
  `perdasContatoConfirmadas=3`.
- O log contém **259** avisos de acesso a `Target` destruído,
  problema separado a ser tratado em outro ciclo de correção.
- O log anterior **não registra explicitamente** o evento do
  `MixerAdaptationGrid` que habilita painel ou concede recompensa;
  por isso esta instrumentação é necessária.

Exemplos reais da linha de base:

```text
[OximetryBridge->Mixer] source=Pitaco24 | bpm=-- | spo2=73,8 | demo=True | contato=True | G1=True | legacyRules=True | age=0,4s | mixer=RECEBIDO | mixerReads=2147 | mixerSource=Pitaco24 | mixerBpm=-- | mixerSpo2=73,8
[OximetryBridge->Mixer] source=Pitaco24 | bpm=58,2 | spo2=96,7 | demo=True | contato=True | G1=True | legacyRules=True | age=0,4s | mixer=RECEBIDO | mixerReads=5522 | mixerSource=Pitaco24 | mixerBpm=58,2 | mixerSpo2=96,7
```

## Instrumentação adicionada nesta branch

O `MixerAdaptationGrid.cs` registra `[SpO2Grid-Test]` automaticamente,
usando `logSpo2AdaptationTest=true`. Não são abertos novos canais seriais.

Cada registro contém: `source`, `spo2`, `demo`, `legacyRules`,
`contato`, `G1`, `age`, `limiarRegular`, `limiarNormal`,
`contadorRegular`, `contadorPerigo`, `painel1Executado`,
`painel2Executado`, `intervaloAvaliacao` e `tempoJogo`.

Eventos possíveis e sua interpretação:

| `event` | Evidência |
| --- | --- |
| `ESTADO_ELEGIVEL` | O snapshot pode passar pelas regras do Grid |
| `ESTADO_INELEGIVEL` | Ausência de autorização ou SpO2; contadores zerados |
| `AMOSTRA_REGULAR` | Contador de faixa regular incrementado |
| `AMOSTRA_PERIGO` | Contador da faixa de perigo incrementado |
| `AMOSTRA_NORMAL_RECOMPENSA_SOLICITADA` | Ramo normal avaliado e reward solicitado |
| `PAINEL_1_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA` | Painel 1 acionado e pausa/reward solicitados |
| `PAINEL_2_ATIVADO_PAUSA_RECOMPENSA_SOLICITADA` | Painel 2 acionado e pausa/reward solicitados |
| `PAINEL_1_REDUCAO_VELOCIDADE_SOLICITADA` | Condição existente de redução de velocidade satisfeita |
| `RECOMPENSA_APLICADA_*` | `DeepDDAAgent` encontrado e `AddReward` chamado |
| `RECOMPENSA_NAO_APLICADA_AGENTE_AUSENTE_*` | Agente ausente; nenhuma recompensa aplicada |
| `AMOSTRA_*_IGNORADA_SEM_ALVOS` | Grid não contou amostra, pois não havia alvos |

Os registros `IGNORADA_SEM_ALVOS` são limitados a **uma vez a cada 5 s**.
Não há registro por frame para os estados estáveis. Quando a regra permite
uma nova avaliação, o `AMOSTRA_*` aparece respeitando a cadência do jogo.

## Procedimento de validação

1. Entrar na branch de teste, `git pull --ff-only`, iniciar Unity em PlayMode.
2. Usar o PITACO 2.4 em ambiente de bancada, sem paciente. Manter contato
   óptico e observar G1. Filtrar Console por `SpO2Grid-Test`.
3. Confirmar a transição `ESTADO_INELEGIVEL -> ESTADO_ELEGIVEL` e os
   campos `source=Pitaco24`, `demo=True`, `legacyRules=True`.
4. Acompanhar `AMOSTRA_REGULAR`, `AMOSTRA_PERIGO` ou
   `AMOSTRA_NORMAL_RECOMPENSA_SOLICITADA`, **se a própria leitura de
   teste cair naturalmente nessas faixas**. Verificar os thresholds
   registrados e a contagem necessária para o evento.
5. Se houver painel, conferir visualmente painel e pausa e comparar
   `PAINEL_*_ATIVADO` e `RECOMPENSA_APLICADA_*`.
6. Retirar o dedo: confirmar `ESTADO_INELEGIVEL` e a interrupção das
   amostras válidas; não devem ocorrer novos eventos de SpO2 sem dados válidos.
7. Salvar log do Console e registrar capturas de tela do jogo. Testes de
   limiares não alcançados naturalmente devem ser feitos depois com
   entradas sintéticas isoladas, **não provocando dessaturação real**.

## Critério de fechamento

A integração fica **aprovada parcialmente** com
`ESTADO_ELEGIVEL` e `AMOSTRA_*` após a liberação do Bridge.
A avaliação completa da regra de painéis/recompensas exige observar
ou testar sinteticamente o respectivo `PAINEL_*` e `RECOMPENSA_APLICADA_*`,
além de verificar que não há ações após perda de elegibilidade.

Esta branch **não representa um novo marco** e não altera a `main`.
Só após conferir o log e as evidências do jogo deve-se planejar
o próximo marco.
