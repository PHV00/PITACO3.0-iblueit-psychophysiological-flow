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
  números, a fonte, a validade, a idade e a elegibilidade para as regras
  legadas de saturação.
- `MixerAdaptationGrid` continua com os mesmos limiares, pausas, painéis
  e recompensas; passou a exigir o indicador `EligibleForLegacySpo2Rules`
  em vez de depender apenas de `sco.IsConnected`.
- `DeepDDAAgent.CollectObservations` agora sempre entrega 16 observações;
  nas transições em que não há capacidade disponível, as últimas quatro
  entradas recebem zeros, sem alterar a ordem ou o ONNX.

## Fluxo

```text
PITACO 2.4 -> PitacoConnectionManager (USB única)
  -> SerialControllerPitaco24.OnJsonReceived
  -> PpgProcessingCoordinator (contato/G1/PPG 8s/BPM/SpO2 DEMO)
  -> OximetryDataBridge.GetSnapshot()
  -> MixerManager.CurrentOximetry
  -> MixerAdaptationGrid (regra legada apenas quando elegível)

Oxímetro externo -> SerialControllerOximetro
  -> PlayerControl.PositionOnSerialOximetro
  -> OximetryDataBridge.RecordLegacy
  -> MixerManager.CurrentOximetry -> MixerAdaptationGrid

DeepDDAAgent.onnx: MESMAS 16 observações -> MESMAS 3 ações
                    -> DeepDDAManager (sem troca de política)
```

## 'Final' não significa 'clinicamente validado'

O BPM/SpO2 são valores já calculados, apresentados por contrato como números
prontos para consumo, mas a `Spo2Estimator.TryEstimateDemo` permanece
**não calibrada**. O valor demonstrativo nunca pode ser usado para
acionar os painéis de segurança, a pausa por saturação ou recompensas
fisiológicas da regra legada. Não afirmamos precisão clínica. Os
resultados também não são novas entradas do ONNX atual.

Para preparar a compatibilidade futura sem mudar o Mixer, `PpgMeasurement`
ganhou o indicador `SpO2CalibratedForLegacyRules` com **default false**.
Somente uma futura implementação validada e documentada poderá marcar
esse contrato como true. Não fazer isso apenas para liberar alertas.

## Prioridade das fontes e dados ausentes

| Estado | Saída do Bridge | Regras legadas |
| --- | --- | --- |
| PITACO 2.4 conectado, G1+contato e publicação recente | BPM final e SpO2 DEMO se disponíveis | Bloqueadas (DEMO) |
| PITACO 2.4 conectado, aguardando primeira janela | Valores indisponíveis | Bloqueadas |
| PITACO 2.4 com 1-2 falhas transientes, G1 expirado, reset ou falha de contato | Valores indisponíveis | Bloqueadas |
| PITACO 2.4 com BPM, mas sem SpO2 (lacuna reconstruída) | Só BPM | Bloqueadas |
| Oxímetro legado recente e conectado (sem 2.4 ativo) | HR+SpO2 | Preservadas |
| Sem sensor, desconectado ou valor expirado | Valores indisponíveis | Bloqueadas |

Com o 2.4 conectado, sua fonte tem prioridade e não se usa silenciosamente
um valor velho do oxímetro legado. Dados ausentes são `null`, nunca um
zero entendido como BPM fisiológico. O `FusionManager` recebe zero
apenas como fallback de compatibilidade antiga; ele atualmente não usa
HR/SpO2 na lógica de fusão.

Limites temporais iniciais de engenharia: publicação 2.4 por até **2 s**;
última leitura legada por até **15 s** (cadência O;10). Comparar contra
`Time.unscaledTime` para não manter dados antigos durante pausa do jogo.

## Testes e evidências

- `OximetryDataBridgeTests`: número final, estado de contato, G1,
  desconexão, expiração, ausência de SpO2, legado e validação futura
  apenas simulada.
- Rodar EditMode na Unity 2022.3.4f1: `OximetryDataBridgeTests`,
  `PpgProcessingTests`, `FingerPlacementTests`.
- Rodar PlayMode com e sem hardware: 2.4 sozinho, legado sozinho, ambos
  conectados, retirada do dedo, perda USB e recuperação.
- Registrar `CurrentOximetry.Source`, `HasBpm`, `HasSpO2`,
  `EligibleForLegacySpo2Rules`, decisões do agente e resultados de G1.
- Confirmar que ONNX, 16 observações, ações, YAML, e política de inferência
  não foram alterados. Verificar os painéis legados em equipamento
  independente apropriado; zero painéis por SpO2 DEMO 2.4.

**Estado:** implementação realizada na branch experimental; a compilação
Unity, execução dos testes, hardware e precisão clínica ainda precisam
de verificação. Nenhuma homologação clínica é reivindicada.
