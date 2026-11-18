# PPG / Unity — Etapa 1.1: relógios e represamento serial (bancada)

## Escopo

Somente scripts C# do I Blue It. **Firmware, INO, limiares G1 e limiar de autocorrelação não foram alterados.**

A correção distingue:
1. Tempo de aquisição `t_ms` enviado pelo PITACO.
2. Tempo monotônico de enfileiramento na thread serial até despacho na thread principal (Stopwatch).
3. Tempo `Time.unscaledTime` para ausência de telemetria por 3 s e HUD.

O G1 PASS expira **pela diferença de tempo do dispositivo para a amostra** quando supera 6.500 ms, nunca isoladamente pelo atraso da Unity. A ausência de novas amostras por 3 s continua invalidando a telemetria. Um G1 FAIL, três amostras consecutivas sem contato ou uma quebra da sequência continuam invalidando a janela. A janela não é apagada apenas porque o loop principal atrasou um G1.

Pacotes que permaneceram mais de **2.000 ms** na fila ainda podem ser avaliados pelo buffer, mas não geram medição atual nem alimentam o Mixer/HUD; o sistema aguarda pacotes frescos. O limite é **provisório de bancada**, não é um limiar de SQI.

## Preparação

- Executar exclusivamente em bancada, **sem pacientes**.
- Compilar e iniciar a Unity usando esta branch, conectar o PITACO 2.4 normalmente e usar o mesmo firmware de antes.
- Manter toda SpO2 marcada DEMO/EXPERIMENTAL: não calibrada, não clínica. Permissões para regras legadas com DEMO, se ativadas para verificar IA, devem permanecer exclusivas de bancada e ser desativadas antes de sessões com pacientes.
- Abrir Console com *Collapse* desativado para capturar eventos distintos, salvar log integral após cada ensaio.
- Não alterar os limiares 500 RED ou 0,57 autocorrelação neste passo.

## Ensaios

| Caso | Procedimento | Critério |
|---|---|---|
| A — Baseline | 3 minutos sem remover/reajustar o dedo | Verificar G1_PASS/REJECT, `G1_EXPIRED`, `FS_ANOMALY`, fila e `--`; correlacionar por t_ms |
| B — Carga do jogo | Transicionar entre cenas e jogar por 2 min mantendo o dedo | Verificar `SERIAL_QUEUE_DELAY` e `SERIAL_QUEUE_RECOVERED`; aumento de `queueAgeMs` sem expiração falsa por relógio da Unity |
| C — Contato | Uma retirada intencional, após A/B, e recolocação | Confirmar `CONTACT_LOST`, reset e nova janela de aquisição |
| D — G1 reprovado | Registrar passagem por `G1_REJECT`, se ocorrer naturalmente | Não publicar valores durante reprovação, mesmo com RED/IR na fila |
| E — Represamento | Sob carga que gere `queueAgeMs>2000` | `SERIAL_BACKLOG`; nenhum valor antigo publicado; posterior recuperação com dados atuais |

**Não é necessário forçar o caso D:** reprovações reais podem ser avaliadas quando ocorrerem. Caso E pode não ocorrer num computador sem sobrecarga; isso não é falha de teste.

## Eventos úteis

- `SUMMARY`: `queuePending`, `queueAgeMs`, `queuePeakMs`, `queuedOver2s`, `linesLastFrame`, `linesPeakFrame`, `serialDropped`, `g1Frames`, `blockedByG1`.
- `SERIAL_QUEUE_DELAY` / `SERIAL_QUEUE_RECOVERED`: transição acima/abaixo de 250 ms entre entrada na thread serial e despacho.
- `SERIAL_BACKLOG`: valor recebido há mais de 2 s; bloqueado para publicação.
- `G1_EXPIRED`: deve possuir `clock=DEVICE` e `ageDeviceMs>6500`; perda real de G1.
- `FS_ANOMALY` / `FS_RECOVERED`: cálculo de `fs>26` / retorno, com sequência e duração da janela; **não altera o processamento**.
- `G1_REJECT`: motivo do firmware e resumo RAW sombra `UNITY_RAW_SHADOW_NOT_FIRMWARE`; a faixa RED=458 é diagnóstico, não autorização para burlar G1.
- `WINDOW_RESET`: motivo e quantidade de amostras descartadas.
- `BRIDGE_TRANSITION`: motivo pelo qual o Mixer recebe ou deixa de receber leituras.
- `CALC`: `bpmReason`, `spo2Reason`, `autocorr`, `fs` e `reconstructed`.

Para filtrar log no Linux:

```bash
grep -E '\[PPG-DIAG\].*(SUMMARY|SERIAL_QUEUE|SERIAL_BACKLOG|G1_|FS_|WINDOW_RESET|CONTACT_LOST|CALC)' unity.log
```

## Critérios para fechar Etapa 1.1

1. Sem expiração de G1 provocada **somente** pelo relógio da Unity.
2. Nenhum valor da fila antiga é publicado como atual.
3. G1 FAIL, contato confirmado perdido e ausência de telemetria invalidam as leituras de acordo com a política.
4. Os logs permitem correlacionar timestamp do dispositivo e atraso da fila.
5. Se ocorrer fs de ~27,79 Hz / 228 amostras, o novo evento registra o contexto.

**Importante:** BPM rejeitado por autocorrelação e SpO2 DEMO instável continuam fora do escopo desta correção. A presença de `--` não significa necessariamente falha da Etapa 1.1.

## Referência do ensaio anterior

Anteriormente, janelas G1 5 e 6 tiveram cerca de 5,001 s de intervalo em t_ms, mas 9,61 s no log da Unity; o G1 expirou e o buffer foi zerado. Também ocorreu G1_REJECT motivo 4, com RED range observado de 458 (limiar de referência 500) e G1 subsequente aprovado com RED range observado de 511. Ambos permanecem objetos de investigação, sem mudança de limite.
