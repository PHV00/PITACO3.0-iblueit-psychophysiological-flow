# Integração PITACO 2.4 -> Mixer / DeepDDA: contrato de oximetria

## Objetivo

Integrar saídas numéricas do PPG ao sistema de consumo do I Blue It usando
a mesma conexão USB e o mesmo modo de inferência ML-Agents. O modelo
`DeepDDAAgent.onnx`, as **16 observações na mesma ordem** e as três ações
discretas são preservados. Não existe novo treinamento, adição de observações
ou mudança no comportamento da rede neural neste marco.

## Decisão de engenharia

- O `OximetryDataBridge` fica em `Assets/_Game/Scripts/Core/Oximetry/Integration/`.
- O `PitacoConnectionManager.Bootstrap` cria o Bridge no mesmo objeto persistente
  que já contém `SerialControllerPitaco24` e `PpgProcessingCoordinator`.
- O Bridge não abre porta USB, não processa RED/IR e não normaliza BPM.
- `PpgProcessingCoordinator.OnMeasurement` publica valores finais de BPM
  experimental e SpO2 DEMO. O Bridge usa contato/G1 e idade para impedir
  uso de números inválidos ou desatualizados.
- O `PlayerControl` informa o Bridge das leituras aceitas do oxímetro antigo
  após o parser e a cadência original `O;10`; os campos globais
  `sensorValueOxiHR` e `sensorValueOxiSPO` continuam pertencendo ao legado.
- `MixerManager.CurrentOximetry` recebe um `OximetrySnapshot` com os
  números, a fonte, a validade e a idade. Quando os dois dispositivos estão
  conectados, este snapshot principal prioriza a oximetria do PITACO 2.4.
- `MixerManager.CurrentLegacyRulesOximetry` usa
  `OximetryDataBridge.GetLegacyRulesSnapshot()`: mantém a fonte do oxímetro
  antigo para os painéis e recompensas de saturação enquanto ele estiver
  conectado e com dados recentes. Isso impede que a conexão simultânea do
  PITACO 2.4 desative inadvertidamente os mecanismos legados.
- `MixerAdaptationGrid` continua com os mesmos limiares, pausas, painéis
  e recompensas; exige `EligibleForLegacySpo2Rules` em vez de depender
  apenas de `sco.IsConnected`. Na branch experimental, a SpO2 DEMO do
  PITACO 2.4 pode habilitar esse indicador apenas para teste de gameplay.
- `DeepDDAAgent.CollectObservations` agora sempre entrega 16 observações;
  nas transições em que não há capacidade disponível, as últimas quatro
  entradas recebem zeros, sem alterar a ordem ou o ONNX.

## Fluxo

```text
PITACO 2.4 -> PitacoConnectionManager (USB única)
  -> SerialControllerPitaco24.OnJsonReceived
  -> PpgProcessingCoordinator (contato/G1/PPG 8s/BPM/SpO2 DEMO)
  -> OximetryDataBridge.GetSnapshot()
  -> MixerManager.CurrentOximetry (números finais, independente do ONNX)
  -> MixerManager.CurrentLegacyRulesOximetry
  -> MixerAdaptationGrid (regra legada apenas quando elegível)

Oxímetro externo -> SerialControllerOximetro
  -> PlayerControl.PositionOnSerialOximetro
  -> OximetryDataBridge.RecordLegacy
  -> MixerManager.CurrentOximetry -> MixerAdaptationGrid

DeepDDAAgent.onnx: MESMAS 16 observações -> MESMAS 3 ações
                    -> DeepDDAManager (sem troca de política)
```

## 'Final' não significa 'clinicamente validado'

O BPM/SpO2 são valores já calculados, mas a `Spo2Estimator.TryEstimateDemo`
permanece **não calibrada**. O resultado continua marcado como
`SpO2IsDemonstration=true`, mesmo quando as regras do jogo estiverem
habilitadas. Não afirmamos precisão clínica ou confiabilidade dos alertas.
Os resultados não são novas entradas do ONNX atual.

**Alteração provisória desta branch de teste:**
`OximetrySnapshotFactory.EnableDemoSpo2LegacyRulesForTesting = true`.
Assim, quando PITACO 2.4 estiver conectado, houver contato, G1 aprovado,
publicação recente (<= 2 s) e um valor de SpO2 numericamente válido,
`EligibleForLegacySpo2Rules=true`, independentemente da calibração.
Isso reativa no `MixerAdaptationGrid` os limiares, recompensas, pausas e
painéis legados **para testes funcionais sem pacientes**. Uma estimativa
incorreta pode disparar um alerta falso, inclusive a partir de valores
experimentais como 78,5%. Não usar para decisões médicas nem em sessões
clínicas. Para restaurar o bloqueio antes de uso com pacientes, definir
essa constante como `false` e verificar o resultado em Unity.

O indicador de calibração independente `PpgMeasurement.SpO2CalibratedForLegacyRules`
permanece `false` por padrão. Somente uma implementação realmente calibrada
e validada deverá marcá-lo `true`; o teste funcional não modifica esse fato.

## Prioridade das fontes e dados ausentes

| Estado | Saída do Bridge | Regras legadas |
| --- | --- | --- |
| PITACO 2.4 conectado, G1+contato e publicação recente | BPM final e SpO2 DEMO se disponíveis | Habilitadas para teste se SpO2 presente |
| PITACO 2.4 conectado, aguardando primeira janela | Valores indisponíveis | Bloqueadas |
| PITACO 2.4 com 1-2 falhas transientes, G1 expirado, reset ou falha de contato | Valores indisponíveis | Bloqueadas |
| PITACO 2.4 com BPM, mas sem SpO2 (lacuna reconstruída) | Só BPM | Bloqueadas |
| Oxímetro legado recente e conectado (sem 2.4 ativo) | HR+SpO2 | Preservadas |
| Ambos conectados, PITACO 2.4 DEMO e oxímetro legado recente | Snapshot principal: PITACO 2.4; snapshot das regras: legado | Preservadas usando apenas SpO2 legada |
| Sem sensor, desconectado ou valor expirado | Valores indisponíveis | Bloqueadas |

Com o 2.4 conectado, sua fonte tem prioridade no snapshot principal e não
se usa silenciosamente um valor velho do oxímetro legado. O snapshot separado
de regras utiliza o oxímetro legado somente enquanto conectado e recente.
Dados ausentes são `null`, nunca um
zero entendido como BPM fisiológico. O `FusionManager` recebe zero
apenas como fallback de compatibilidade antiga; ele atualmente não usa
HR/SpO2 na lógica de fusão.

Limites temporais iniciais de engenharia: publicação 2.4 por até **2 s**;
última leitura legada por até **15 s** (cadência O;10). Comparar contra
`Time.unscaledTime` para não manter dados antigos durante pausa do jogo.

## Evidência automática no Console do Unity

O `OximetryDataBridge` agora registra, **por padrão e a cada 5 segundos**
(configurável em `logIntervalSeconds`), a linha:

```text
[OximetryBridge->Mixer] source=Pitaco24 | bpm=59,1 | spo2=90,5 | demo=True | contato=True | G1=True | legacyRules=True | age=0,2s | mixer=RECEBIDO | mixerReads=240 | mixerSource=Pitaco24 | mixerBpm=59,1 | mixerSpo2=90,5
```

Exemplo ilustrativo, **não é um log real do hardware**. O separador decimal
varia conforme a configuração regional do Unity.

- `source`, `bpm`, `spo2`, `contato` e `G1` descrevem o snapshot
  disponível no Bridge; `--` significa valor indisponível.
- `mixer=RECEBIDO` somente quando `MixerManager` chamou
  `RecordMixerConsumption` **após** `FusionManager` e `AdaptationGrid`
  retornarem, nos últimos 2 segundos. `mixer=AGUARDANDO` significa
  que esse consumo recente não foi confirmado.
- `mixerReads` contabiliza as confirmações durante a execução e
  `mixerBpm`/`mixerSpo2` mostram o último snapshot consumido.
- Nesta branch de teste, `demo=True` e `legacyRules=True` podem aparecer
  juntos: o dado **continua não calibrado**, porém o Mixer pode acionar
  a regra legada do jogo com essa leitura. O log sozinho não prova que um
  painel de alerta foi acionado.
- Este log **não prova que o ONNX recebeu BPM/SpO2 como observações**:
  o modelo mantém as 16 observações originais e não foi retreinado.

O registro pode ser silenciado em `logDiagnostics=false` no Inspector,
e a frequência pode ser ajustada sem alterar a aquisição PPG.

## Testes e evidências

- `OximetryDataBridgeTests`: número final, estado de contato, G1,
  desconexão, expiração, ausência de SpO2, legado e validação futura
  apenas simulada.
- Rodar EditMode na Unity 2022.3.4f1: `OximetryDataBridgeTests`,
  `PpgProcessingTests`, `FingerPlacementTests`.
- Rodar PlayMode com e sem hardware: 2.4 sozinho, legado sozinho, ambos
  conectados, retirada do dedo, perda USB e recuperação.
- Registrar `CurrentOximetry.Source`, `HasBpm`, `HasSpO2`,
  `CurrentLegacyRulesOximetry.Source`,
  `EligibleForLegacySpo2Rules`, decisões do agente e resultados de G1.
- Confirmar que ONNX, 16 observações, ações, YAML, e política de inferência
  não foram alterados. Em teste sem pacientes, verificar um valor válido
  com `demo=True | legacyRules=True | mixer=RECEBIDO` e o resultado real
  dos painéis/recompensas ao cruzar limiares durante a cadência configurada.
  Repetir sem contato, G1 reprovado, SpO2 ausente e timeout: nenhuma regra
  deve consumir uma medição inválida.

**Estado:** modo de teste funcional com regras legadas habilitadas para SpO2
DEMO elegível na branch `feature/ppg-csharp-bpm-spo2-mvp`. Compilação Unity,
execução dos testes e comportamento dos painéis com hardware ainda precisam
de verificação. A SpO2 não foi calibrada ou clinicamente validada.
