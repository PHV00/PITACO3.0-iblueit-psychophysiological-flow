# Etapa 1 — correção mínima de telemetria, G1 e continuidade (somente Unity)

**Branch:** `test/unity-ppg-step1-state-continuity` (base `test/ppg-quality-decision-trace`).

## Escopo e limites

- **Nenhuma alteração em `PITACO_ESP32`, INO, firmware, parâmetros MAX30105, JSON, BPM ou SpO2 DEMO.**
- `BpmEstimator.MinimumAutocorrelation` continua **0.57**; `PpgSampleBuffer.WindowMs` continua **8000 ms**; `Gate01MaxAgeMs` e `Gate01MaxAgeSeconds` continuam **6500 ms / 6.5 s**.
- Não foram criados gestores, novos relógios de thread, novas portas COM ou novas máquinas de estados públicas.
- Modificados apenas o coordenador e os testes existentes. `PpgRawG1Probe` continua somente diagnóstico.

## Alterações

### 1. Telemetria ≠ contato físico

Antes, `lastSampleReceivedAt` só era atualizado quando a amostra passava pelo caminho com contato confirmado. Uma sequência de amostras `contact=false` podia, por engano, resultar em `Sem amostras PPG recentes`, embora houvesse JSON chegando.

Agora o relógio de recepção é atualizado **imediatamente ao reconhecer um frame `type=sample`**, antes dos filtros de contato. A perda de telemetria (mais de 3 s sem receber amostras) é registrada como `[PPG-DIAG] event=TELEMETRY_LOST`, uma única vez por intervalo sem dados. Esse relógio é o da **entrega à Unity no main thread**, não o instante de leitura USB nem da conversão ADC.

Isso melhora a classificação; **não elimina atrasos reais no main thread** nem mede o tempo que os dados passaram na fila USB. Somente instrumentar a thread serial permitiria atribuir o atraso à fila, o que não foi feito nesta etapa.

### 2. Três ações simples no `PpgProcessingCoordinator`

| Ação | Condição | Resultado |
| --- | --- | --- |
| `Allow` | G1 anterior aprovado e ainda dentro da validade, contato confirmado | Buffer é candidato à estimativa; pipeline BPM e SpO2 DEMO pode prosseguir |
| `Suspend` | G1 aguardando nova janela, reprovado ou expirado | Não publica; pode continuar acumulando dados **candidatos** após última invalidação |
| `Reset` | Já havia G1 PASS e o firmware informou `sample.sqi.reason != 0` com contato novamente confirmado | Revoga PASS antigo e limpa a janela comprometida **uma única vez**, passando a `Waiting` |

`reason=1` tem significado especial no firmware original: quando o contato é reconfirmado, o firmware reinicia seu acumulador G1. Portanto, não é correto simplesmente ignorá-lo após uma oscilação. O contrato do firmware continua sendo a autoridade.

**Importante:** o armazenamento de amostras que ainda aguardam aprovação é um buffer **candidato**, e não um buffer permanentemente elegível. Nenhuma estimativa é calculada com G1 `Failed`, `Waiting` ou `Stale`. O G1 pode reprovar um trecho antes de autorizar processamento subsequente.

### 3. Expiração G1 e separação de evidência

Na transição única `Passed -> Stale`, a janela de processamento é invalidada para impedir misturar o período previamente autorizado com os próximos candidatos sem decisão G1. O histórico bruto de `PpgRawG1Probe` **não é apagado**, permitindo análise.

Esse comportamento é **mais conservador** do que a versão anterior, que mantinha o buffer de oito segundos durante expiração. Ele pode aumentar o tempo de retomada após uma expiração verdadeira. Portanto, não declarar redução de `--` sem medir; se o teste de bancada indicar piora, revisar a política de expiração sem relaxar o G1.

### 4. Menor custo de log repetitivo

Para esperas G1 e janelas ainda incompletas, `ShouldTraceCalculation` evita formatar mensagens de diagnóstico em todas as ~25 amostras por segundo. O evento segue disponível quando o estado muda ou na periodicidade diagnóstica de 2 s. Os cálculos BPM/SpO2 reais continuam auditados a cada ~400 ms.

## Testes adicionados (Unity Test Runner / EditMode)

Os testes foram acrescentados à suíte `PpgProcessingTests.cs`:

1. `ContactFalseOpticalFramesRefreshTelemetryHeartbeat`: `contact=false` não implica ausência de telemetria.
2. `ReasonOneRevokesG1OnlyOnceAndAccumulatesNewCandidateSamples`: `reason=1` revoga o PASS uma vez; espera não apaga o candidato a cada amostra.
3. `StaleG1SeparatesOldWindowFromNewCandidateSamples`: expiração separa janela autorizada da candidata e preserva a observação bruta.
4. `FailedG1DiscardsPreviousWindowButRetainsFutureCandidates`: G1 reprovado limpa dados anteriores e não publica amostras novas até novo PASS.

Manter e executar todos os testes anteriores de `PpgProcessingTests`, especialmente os relacionados a contato, buffers, expiracão, JSON original e retorno após FAIL.

**Ainda não executados no Unity:** este ambiente não possui Unity Editor, Test Runner ou compilador C#. Apenas a verificação estática pelo GitHub foi possível. Não considerar a implementação pronta para merge até compilar e passar os testes no Editor e no hardware.

## Procedimento de teste

```bash
git fetch origin
git switch --track origin/test/unity-ppg-step1-state-continuity
```

Se a branch já existir localmente:

```bash
git switch test/unity-ppg-step1-state-continuity
git pull --ff-only origin test/unity-ppg-step1-state-continuity
```

No Unity: `Window > General > Test Runner > EditMode`; executar **`PpgProcessingTests` completa**, não apenas os quatro testes novos. Se aparecer erro de compilação ou qualquer regressão, não prosseguir com ensaio.

Depois:

1. Rodar o jogo com **firmware atual inalterado** e filtro Console `[PPG-DIAG]`.
2. Fazer primeiro 120 s com dedo estável, depois um segundo ensaio incluindo pequenas oscilações e uma retirada intencional. Não forçar dessaturação.
3. Observar `G1_PASS`, `G1_REJECT`, `G1_REVOKED`, `G1_EXPIRED`, `WINDOW_RESET`, `TELEMETRY_LOST`, `CALC` e `BRIDGE_TRANSITION`.
4. Comparar duração dos `--`, quantas expirações ocorreram, amostras descartadas e tempo de retomada com a branch baseline, preferencialmente fazendo replay de uma mesma gravação.
5. Verificar que **nenhuma** janela publica novo BPM/SpO2 DEMO durante falha G1/contato/telemetria e que G1 `reason=1` sempre revoga o PASS anterior.

## Critério de avanço

Etapa 1 só está validada quando: compila, toda a suíte EditMode passa, as sessões em hardware mostram estado/causa corretos, não há publicação indevida e não há aumento relevante de perda de medições devido a resets de buffer. Se a validade temporal (6,5 s) continuar causando bloqueios por latência de fila no Editor, a próxima revisão deve medir a fila serial **antes** de aumentar qualquer timeout.

**Não avançar para os limiares BPM nem o SQI fisiológico até encerrar esta etapa.**
