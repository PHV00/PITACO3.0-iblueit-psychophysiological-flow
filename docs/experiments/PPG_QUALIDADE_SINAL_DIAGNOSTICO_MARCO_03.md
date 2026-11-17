# Marco 3 — Investigacao inicial da qualidade do sinal PPG (PITACO 2.4)

**Registro:** 17/11/2026
**Estado:** diagnostico inicial de codigo e logs; **sem medicao bruta desta sessao e sem mudanca de thresholds**.
**Origem do teste Unity:** `test/pitaco24-spo2-adaptation-grid-logs`.
**Firmware de referencia consultado:** `PHV00/PITACO_ESP32`, `main`. Confirmar no dispositivo qual commit foi gravado antes de atribuir 100% dos motivos aos thresholds abaixo.
**Seguranca:** SpO2 DEMO nao calibrada. Nao usar para alarme, decisao clinica ou alterar saturacao fisiologica de participantes para forcar testes.

## 1. O que ja e observavel

- USB/JSON funcionam: contadores `JSON invalido=0` e `amostraInvalida=0`, log serial `descartes da fila=0`. O transporte nao e a explicacao principal evidenciada.
- O Gate G1 do firmware oscilou de `Passed` para `Failed`; `reason=2` e `reason=4` ocorreram.
- O G1 acompanha ~125 amostras em 5 s e opera de forma separada do pipeline C# que exige 8 s.
- O firmware consultado define `reason=2` como `RED_LOW` (media RED < 5000) e `reason=4` como `RED_FLAT` (maxRED-minRED < 500), **mesmo se o detector instantaneo de contato retornar true**.
- A documentacao do firmware `docs/firmware/PPG_VALIDATION_25HZ.md` registra um ensaio anterior (04/11/2026) de 1498 amostras, 11 janelas G1, 3 `RED_FLAT` com faixas RED de 422, 442 e 380; uma janela de range 991 foi aprovada apesar de deriva endpoint ~-986. Dados historicos, **nao sao a captura desta execucao Unity**.
- Cada `G1 Failed` chama `InvalidatePpgWindow` e limpa o buffer de 8 s em `PpgProcessingCoordinator`. Portanto um FAIL com repeticao suficiente pode impedir uma janela completa e perpetuar `--`.
- 1-2 samples `contact=false` nao limpam imediatamente o buffer, mas a retomada cria lacunas marcadas. `PpgSignalProcessor` reconstrói lacunas apenas para BPM; `Spo2Estimator` rejeita qualquer janela com `ReconstructedSamples > 0`. Assim, mesmo uma lacuna tolerada pode indisponibilizar SpO2 enquanto permanecer nos ~8 s de janela.
- `BpmEstimator` exige autocorrelacao IR de pelo menos 0,57, com faixa 35..200 BPM; G1 PASS nao assegura BPM.
- `Spo2Estimator` usa RMS AC/DC RED e IR, aceita razao em [0,2;1,4] e polinomio demonstrativo `-45,060*R^2 + 30,354*R + 94,845`, limitado [70;100]. Este calculo pode gerar numeros sem acuracia; alta variacao numerica nao comprova alteracao fisiologica.

## 2. Hipoteses ordenadas, ainda sem conclusao causal

| Prioridade | Hipotese | Evidencia esperada para confirmar/refutar |
| --- | --- | --- |
| H1 | Canal RED com nivel optico baixo ou pouca variacao na janela G1; thresholds de bancada transferidos de MAX30102 para MAX30105 | Por janela real: meanRED <5000 para reason 2; rangeRED <500 para reason 4; comparar distribuicao com ondas visualmente rotuladas |
| H2 | Acoplamento sensor-dedo instavel (pressao, posicao, vedacao de luz, movimento) | Mudanca sistematica de AC/DC, tendencia, contacto e reasons apos fixacao anatomica comparada a condicao solta |
| H3 | Politica temporal G1 de 5 s vs janela C# de 8 s amplifica falhas ocasionais | Quantificar FAIL, resets, tempo ate primeira janela completa e disponibilidade de BPM/SpO2 |
| H4 | Algoritmos BPM e SpO2 rejeitam sinais com G1 PASS, por criterios independentes ou insuficiencia de pulsatilidade | Separar: G1 PASS+janela completa+BPM ausente versus G1 PASS+janela completa+SpO2 ausente; registrar correlacao, R, reconstrucoes |
| H5 | Distorcao de RED/IR por iluminacao, LED current, sample timing, compressao de dedo ou perfusao | Comparar sinais brutos, escala, clipping, fs efetiva e referencia externa; nao concluir por percentuais DEMO |

**Observacao importante:** `G1_MIN_RANGE=500` mede **pico-a-pico bruto**, nao a periodicidade pulsatoria. Deriva e movimento podem elevar `range`; um sinal com pulso pequeno e regular pode nao passar. Alterar o threshold agora mascara o problema ao inves de medi-lo.

## 3. Protocolo controlado proposto (sem alterar codigo)

Preparacao: identificar modelo real da placa optica (MAX30105/MAX30102), firmware gravado e LEDs/configuracao; manter `100 Hz`/FIFO average `4` (~25 amostras efetivas/s), RED+IR, ADC 18 bits, como na referencia do firmware. Usar apenas bancada/voluntario informado, com referencia independente para BPM se pretendido; nada de induzir dessaturacao.

1. **Base com dedo estavel:** 90 s parado, sensor levemente fixo e sem iluminar lateralmente. Registrar video da montagem (sem identificar pessoas) e telemetria RAW.
2. **Controle de montagem:** 90 s com suporte/anteparo optico que nao comprima excessivamente a ponta do dedo. Manter todas as demais condicoes iguais.
3. **Deslocamento suave:** sequencia curta de reposicionamento voluntario e retorno, com anotacao do momento exato para testar 1/2/3 falhas, resets e recuperacao.
4. **Controle sem dedo:** curto segmento com contato ausente, sem tratá-lo como onda de referencia; verificar rejeicao G1/contato.
5. **Opcional:** repetir a base com outra posicao do dedo e/ou LED configuration em experimento separado, nunca mudando multiplas variaveis por vez.

**Coleta USB reproducivel — executar fora da Unity:** o jogo e o probe nao podem abrir COM3 simultaneamente. Fechar PlayMode/monitor serial e confirmar a porta antes de iniciar. O probe existente envia `e`, depois `r` para iniciar a sessao e `f` no final; logo o experimento e separado da execucao do jogo.

Na raiz do repositorio `PHV00/PITACO_ESP32`:

```powershell
python -m pip install pyserial
python .\arduino-pitaco-mpx5010dp-ble\pitaco_experimental\pitaco24_ppg_probe.py COM3 --seconds 90 --csv ppg_base_90s.csv
python .\arduino-pitaco-mpx5010dp-ble\pitaco_experimental\analyze_ppg_25hz.py ppg_base_90s.csv --output ppg_base_90s_report.json
```

Repetir o probe com nome distinto para cada condicao. O `pitaco24_ppg_probe.py` ja grava os eventos `type=g1` com seus reasons e `samples`, alem dos `type=sample` RED/IR. A captura do dispositivo deve ficar local; nunca realizar esta coleta concorrente com o Unity conectado a mesma COM.

Registrar CSV local (NAO commitar RAW nem dados pessoais) com `type,seq,t_ms,red,ir,bpm_valid,g1,reason,samples,contact` e logs `X,PPG_DIAG`, `X,PPG_FIFO_OVERFLOW`, `X,PPG_SW_RING_OVERRUN`, `X,PPG_I2C_FIFO_READ_ERROR` se presentes. Adicionar anotacoes manuais do tempo, montagem, movimento e relogio Unity.

No repositorio do firmware ja existe o analisador offline:
`python arduino-pitaco-mpx5010dp-ble/pitaco_experimental/analyze_ppg_25hz.py captura.csv --output relatorio.json`

A partir do CSV gerar e comparar, por janela G1 e periodo 8 s:
- media e range RED/IR; clipping; tendencia linear; diferenca entre extremos; RMS da componente AC; AC/DC ou indice de perfusao exploratorio;
- percentuais G1 PASS, RED_LOW, RED_FLAT, CONTACT_ABSENT e GAP; taxa e duracao de contato falso; vazao/`seq`/`t_ms`; frames insuficientes;
- percentual de janelas 8s completas, resets por motivo, tempo ate primeira publicacao, disponibilidade de BPM e SpO2 **separadamente**;
- valor e rejeicao de autocorrelacao IR; razao RED/IR R; fracoes de reconstrucao; tempo gasto com SpO2 vetada por lacuna;
- para BPM, erro versus referencia sincronizada e proporcao de janelas utilizaveis; para SpO2, **nao calcular acuracia** sem protocolo de calibracao e referencia adequada.

## 4. Criterios de decisao antes de alterar parametros

- Nao baixar `G1_MIN_RANGE=500` por intuicao. Primeiro comparar distribuicoes e rotular janelas por morfologia pulsatoria e referencia externa, separadas da propria saida G1 (evitar validacao circular).
- Separar os requisitos: **integridade de amostra/FIFO (G1)**, **pulsatilidade/morfologia (futuro SQI)**, **estimador BPM**, **estimador SpO2** e **politica de HUD**. Um PASS de integridade nao garante acuracia.
- Estabilizar montagem fisica, depois revisar ganho/LED e somente depois estimadores/thresholds, usando dados repetiveis.
- Nao usar `HudMeasurement` como prova de leitura nova: a retencao de ate 2 falhas e apenas visual; Mixer consome `LastMeasurement`.
- Testar e documentar cada mudanca isoladamente em branch de experimento; preservar baseline e comparar metricas ANTES/DEPOIS.

## 5. Fontes

**Codigo interno:**
- `PHV00/PITACO_ESP32/frimeware2.4/sqi/gate_01_integrity.ino` e `docs/firmware/GATE_01.md`.
- `PHV00/PITACO_ESP32/docs/firmware/PPG_VALIDATION_25HZ.md` (experimento offline anterior).
- `Assets/_Game/Scripts/Core/Oximetry/PpgProcessingCoordinator.cs`, `PpgSignalProcessor.cs`, `BpmEstimator.cs`, `Spo2Estimator.cs` da branch Unity de teste.
- `docs/marcos/MARCO_02_INTEGRACAO_PITACO24_MIXER_DEEPDDA_FECHAMENTO.md`.

**Literatura de apoio, sem substituir os dados reais:**
- Charlton et al. (2023), *The 2023 wearable photoplethysmography roadmap*, DOI: 10.1088/1361-6579/acead2 — contato sensor-pele, movimento e caracterizacao de qualidade.
- *A Survey of Photoplethysmography and Imaging Photoplethysmography Quality Assessment Methods*, Applied Sciences 12(19), 9582 (2022), https://www.mdpi.com/2076-3417/12/19/9582 — qualidade metrologica versus fisiologica.
- *The Impact of Contact Force on Signal Quality Indices in Photoplethysmography Measurements*, Applied Sciences 14(13), 5704 (2024), https://www.mdpi.com/2076-3417/14/13/5704 — efeito de pressao de contato na amplitude e morfologia.

**Conclusao nesta fase:** os logs identificam um mecanismo de instabilidade plausivel e verificavel (G1 vermelho + resets + janela 8s), mas SEM RAW da sessao nao permitem decidir entre mau acoplamento, parametrizacao optica e forma de onda fisiologicamente fraca. O proximo passo correto e captura controlada, nao relaxamento imediato do SQI.
