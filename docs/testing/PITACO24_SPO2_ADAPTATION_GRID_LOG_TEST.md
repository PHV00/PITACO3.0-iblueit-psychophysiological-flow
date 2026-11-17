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


## Execução 1 — resultado real do log recebido nesta conversa

**Veredito:** integração comprovada; teste de classificação por saturação e
painéis **inconclusivo devido a configuração**. Não criar novo marco ainda.

### Evidências de funcionamento

- `[PITACO Manager]`: Pitaco24 conectado por COM3 exclusiva; filas sem
  descartes nos relatórios disponíveis.
- Diagnóstico final enviado: `JSON serial=3393`, `callbacks=3393`,
  `samples=3369`, `JSON invalido=0`, `amostraInvalida=0`.
- `G1=Passed` e `G1_reason=0` liberaram SpO2 DEMO e, em parte do tempo,
  BPM. O Bridge confirmou `legacyRules=True` e `mixer=RECEBIDO`.
- `[SpO2Grid-Test]` registrou `ESTADO_ELEGIVEL` e
  `AMOSTRA_NORMAL_RECOMPENSA_SOLICITADA`; `AddReward(+0,1)` chamou
  `DeepDDAAgent.AddReward` (`RECOMPENSA_APLICADA_0,1`).
- Transições `ESTADO_INELEGIVEL` surgiram quando os dados ficaram ausentes,
  seja por perda de contato, reprovação G1 ou inexistência de SpO2.
- Cadência medida: `intervaloAvaliacao=6,0s`, correspondendo à
  leitura de 10 avaliações por minuto definida pela configuração do oxímetro.
- `painel1Executado=False`, `painel2Executado=False`, contadores `0/2`
  em todos os registros mostrados; **não há evidência de painel disparado**.

### Causa de configuração identificada no código

O log registra `Dispositivo(s): P`. Em
`Assets/_Game/Scripts/Core/Database/ParametersDb.cs`, a combinação
é selecionada pelos controladores legados: um PITACO conectado, sem
`SerialControllerOximetro` separado, corresponde a `P`. O sensor óptico
integrado ao PITACO 2.4 não transforma essa combinação em `PO`.

No arquivo `Config/_parametersList.csv`, seção ADAPTACAO:

| Linha | Oxigenacao Normal Minima | Oxigenacao Regular Minima |
| --- | ---: | ---: |
| `P` | **0** | **0** |
| `PO` | **95** | **89** |

No `MixerAdaptationGrid.cs`, faixa regular exige
`SpO2 >= MinimumRegularOxygenation && SpO2 < MinimumNormalOxygenation`;
perigo exige `SpO2 > 0 && SpO2 < MinimumRegularOxygenation`.
Com ambos os limites iguais a zero, todas as leituras positivas avaliadas
caem no ramo **normal**, recebendo `+0,1`: o comportamento observado
é consequência direta dos parâmetros carregados, não comprova
classificação fisiológica correta.

**Não substituir globalmente a combinação `P` por `PO`**: isso
também alteraria parâmetros de gameplay alheios à SpO2 (velocidade,
fatores de adaptação etc.). O próximo experimento precisa de um mecanismo
isolado e explícito para carregar limiares de oxigenação quando a
fonte é PITACO 2.4, preservando o restante dos parâmetros de `P`.
Para teste funcional de bancada podem ser usados os limiares de
referência já presentes na configuração `PO`: 95 e 89; não presumir
que tais valores ou a SpO2 DEMO estejam clinicamente validados.

### Pendências e cuidados

1. Instrumentar ou configurar os **limiares de SpO2 isoladamente**, então
   repetir o teste com entradas sintéticas e cenário seguro para exercitar
   `AMOSTRA_REGULAR`, `AMOSTRA_PERIGO`, `PAINEL_1_ATIVADO` e
   `PAINEL_2_ATIVADO`. Não provocar dessaturação real.
2. Validar que contadores zeram ao perder elegibilidade e que a cadência
   não aciona ações inesperadas após `G1=Failed`.
3. Documentar e corrigir, em etapa separada, os avisos repetidos
   `Tentando acessar um objeto Target que foi destruído` e os 3 avisos
   `The referenced script (Unknown) ... is missing!`.
4. `Couldn't connect to trainer ... Will perform inference instead` é
   esperado em execução com ONNX sem trainer externo.

Este relatório registra apenas evidências vistas no log recebido e no
código/configuração da branch; **não indica precisão clínica**.


## Execução 2 — correção isolada pronta para reteste

**Commit de código:** `548075ac760a6a9e7bd4ba74fd5d602253970b28`  
**Arquivo:** `Assets/_Game/Scripts/Core/Game/MixerAdaptationGrid.cs`

O `AdaptationGrid` usa os valores **mínimo regular 89** e
**mínimo normal 95** apenas quando
`OximetrySnapshot.Source == OximetrySource.Pitaco24`.
Para fontes legadas, continua lendo
`ParametersDb.parameters.MinimumRegularOxygenation` e
`MinimumNormalOxygenation`. O perfil `P` e o CSV são mantidos
inalterados: todos os demais parâmetros do gameplay são preservados.

Os registros `[SpO2Grid-Test]` agora incluem
`perfilLimiar=Pitaco24Test` ou `LegadoConfig`, e registram
**os limiares efetivamente aplicados**, não os zeros do perfil P.

Faixas da **lógica funcional**: `0 < SpO2 < 89` perigo,
`89 <= SpO2 < 95` regular, `SpO2 >= 95` normal.
Estes números vêm da linha `PO` existente no CSV e são
reaproveitados para teste, **não são cortes clinicamente validados
para o novo dispositivo**.

Para repetir:
1. Atualizar **somente** a branch `test/pitaco24-spo2-adaptation-grid-logs`.
2. Verificar `perfilLimiar=Pitaco24Test`, `limiarRegular=89`,
   `limiarNormal=95` no log.
3. Sem manipular saturação de pessoas, observar a classificação
   de leituras naturais; para cobrir os dois painéis, preferir
   reproduções sintéticas isoladas de `OximetrySnapshot`.
4. Conferir contadores `0/2 -> 1/2 -> 2/2` quando aplicável,
   painéis, pausa e recompensas. A regra atual zera os contadores
   quando a leitura deixa de ser elegível; duas leituras não
   necessariamente são consecutivas entre faixas.
5. Confirmar que nenhuma leitura com `legacyRules=False`
   chega às ações de saturação.

### Diagnóstico dos traços "--" na HUD do teste anterior

A análise de código mostra vários mecanismos **distintos**:

- Regra de contato **3 amostras falsas consecutivas**:
  1 ou 2 falhas não apagam o buffer, mas **já colocam
  `LastMeasurement` com BPM/SpO2 nulos**. O `Player` torna
  o "--" visível de imediato. Após 3, remove contato confirmado,
  limpa janela e revoga G1; 8 s de nova coleta são exigidos.
- Falha de G1 (`reason=2` ou `reason=4` vistos nos logs)
  limpa o buffer e invalida os dois estimadores mesmo com
  `contato=True`. Uma reprovação do G1 a cada 5 s pode
  prolongar a espera até uma janela contínua de 8 s.
- Um `G1=Passed` não garante SpO2: `Spo2Estimator`
  rejeita janelas com lacunas reconstruídas, razão RED/IR
  fora de [0.2,1.4] ou estimativa DEMO fora de [70,100].
  `BpmEstimator` possui validação independente; pode
  haver BPM "--" enquanto a SpO2 aparece, e vice-versa.
- O `Player` da cena consulta
  `PpgProcessingCoordinator.LastMeasurement` diretamente,
  atualiza a HUD em até 1 s e remove valores imediatamente
  na invalidação. O `Mixer` consulta o `OximetryDataBridge`,
  com validade adicional de idade máxima de 2 s.
  Portanto o caminho visual e o do Mixer não são totalmente
  equivalentes no código atual.
- O último log confirma `JSON invalido=0` e
  `amostraInvalida=0`, além de G1 `Passed/Failed`,
  um evento `perdasContatoConfirmadas=1` e
  `lacunasContatoToleradas=2`.
  Não é possível atribuir **todas** as perdas visuais a
  um único gate com o log agregado atual.

Próxima melhoria de instrumentação, **não realizada aqui**:
registrar causas granulares de invalidação/publicação em cada
janela e comparar timestamps de `Player`, `Bridge`,
G1 e detector de contato; usar isso antes de reduzir
limiares ou relaxar o G1.
