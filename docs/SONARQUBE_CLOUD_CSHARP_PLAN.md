# Análise estática C# do projeto Unity com SonarQube Cloud

**Status: plano de integração — NÃO há análise Sonar conectada ou executada.** Este documento registra o procedimento técnico correto para não tratar scanner genérico de JavaScript/C++ como se cobrisse código Unity/C#.

## Contexto real

- Repositório: [PITACO3.0-iblueit-psychophysiological-flow](https://github.com/PHV00/PITACO3.0-iblueit-psychophysiological-flow).
- Versão do Unity Editor declarada por `ProjectSettings/ProjectVersion.txt`: **2022.3.4f1**.
- O repositório conserva `Assets/`, `Packages/` e `ProjectSettings/`, mas **não possui `.sln` ou `.csproj` versionados na raiz**.
- A análise C# do SonarQube Cloud é realizada pelo **SonarScanner for .NET** acoplado ao processo de compilação, e não pode ser reivindicada por uma execução genérica de SonarScanner CLI sem um build C# reconhecido.

## Estratégia proposta

1. Importar o repositório como projeto no [SonarQube Cloud](https://sonarcloud.io), com autorização explícita.
2. Gerar os projetos `.csproj`/`.sln` com um Editor Unity compatível com `2022.3.4f1`; **não** inventar um `.sln` manual incompleto.
3. Selecionar o build C# adequado à solução Unity: avaliar build do Editor com exportação de projetos ou integração MSBuild, confirmando que são compilados os assemblies autorais do jogo, dos componentes PITACO e do Mixer/DeepDDA.
4. Em um job GitHub Actions com o toolchain compatível, executar:
   ```text
   dotnet sonarscanner begin /k:<SONAR_PROJECT_KEY> /o:<SONAR_ORGANIZATION> /d:sonar.token=<SONAR_TOKEN>
   dotnet build <PROJETO_COMPATIVEL> --no-incremental
   dotnet sonarscanner end /d:sonar.token=<SONAR_TOKEN>
   ```
   O exemplo é **esquemático**, não um comando já validado no repositório.
5. Conferir no resultado quantos arquivos `.cs` e quais assemblies foram efetivamente analisados. Excluir pastas de bibliotecas de terceiros e `Library/`/artefatos gerados de forma justificada, sem ocultar código autoral.
6. Gerar relatório da análise, classificar findings, publicar evidência de GitHub Actions e então configurar Quality Gate.

## Segredos / configurações

- `SONAR_TOKEN`: GitHub Actions Secret; nunca comitar token.
- `SONAR_ORGANIZATION` e `SONAR_PROJECT_KEY`: GitHub Actions Variables definidas pelo usuário após a importação.
- A compilação automática do Unity em runner pode exigir acesso a **Unity Editor compatível**, requisitos de licença e/ou configuração específica do ambiente, não disponíveis neste repositório no inventário atual.

## Alternativa enquanto o build Sonar não estiver pronto

É possível executar **CodeQL para C#** ou scanners Roslyn sobre assemblies/projetos construíveis, verificando a abrangência real. Nenhuma alternativa permite afirmar que a Unity foi analisada se os scripts autorais não estiverem entre os arquivos processados.

**Não adicionar um job Sonar com sucesso simulado** para a Unity enquanto não houver compilação C# instrumentada e auditável. Isso protegerá a qualidade da evidência exigida pelo Playbook.

[Documentação oficial SonarScanner para .NET](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/scanners/sonarscanner-for-dotnet/using)
