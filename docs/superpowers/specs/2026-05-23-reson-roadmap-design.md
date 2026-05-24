# Reson — Gap-Resolution Roadmap

**Data:** 2026-05-23
**Status:** Approved (aguardando review)
**Tipo:** Roadmap spec (cada milestone vira seu próprio plano de implementação)

## Visão geral

Reson é um soundpad para Windows controlado pelo celular (PC servidor .NET/WPF + app Android Flutter + UI web), com mic passthrough + mixer, múltiplos boards, volume por som, e auto-update (Velopack no desktop, APK self-hosted no Android). Está em produção na v1.0.3, distribuído via GitHub Releases (github.com/GabrielSoarde/reson).

Este documento cataloga **todos os gaps conhecidos** e os organiza em milestones sequenciados. Não é uma spec de implementação detalhada — é o roadmap. **Cada milestone recebe seu próprio ciclo spec→plano→implementação.** O Milestone 1 está descrito com mais profundidade por ser o próximo.

## Princípio de priorização

**"Sólido pra distribuir" primeiro.** A ordem prioriza o que afeta diretamente quem instala (paridade entre plataformas, qualidade de áudio, releases confiáveis, sem avisos assustadores), depois os diferenciais competitivos, depois robustez e polish. Escolhido pelo dono do produto em 2026-05-23.

## Catálogo de gaps

### Funcionalidades (vs concorrentes)
- **F1** Normalização de volume (Soundpad tem; equaliza todos os sons ao nível da voz)
- **F2** Editor/trim embutido (cortar som sem app externo)
- **F3** Busca + tags + favoritos (dados de uso já coletados: `playCount`, `lastPlayedAt`)
- **F4** Hotkeys de teclado no PC (cortado de propósito no v1; alguns usuários querem)
- **F5** Overlay in-game (Soundpad tem via Overwolf)
- **F6** Ducking (abaixar som quando o usuário fala)
- **F7** Multi-saída com volume por dispositivo (Resanance tem)
- **F8** TTS — síntese de voz a partir de texto

### Diferenciais (nenhum concorrente tem a combinação)
- **D1** Modo festa multi-celular — vários clientes controlam o mesmo board ao vivo
- **D2** Clipar os últimos N segundos do áudio do PC (WASAPI loopback) → som instantâneo
- **D3** Importar de YouTube/link + trim

### Dívida técnica / qualidade
- **T1** Web UI sem boards/volume (mobile tem paridade, web ficou pra trás)
- **T2** Sem code signing (SmartScreen warning + UAC do VB-Cable mais assustador)
- **T3** Migração Inno→Velopack confusa (instalação dupla até desinstalar a antiga)
- **T4** Mic "padrão do Windows" hot-plug não re-detecta (`OnDefaultDeviceChanged` não honrado)
- **T5** Sem CI/CD (releases 100% locais; máquina de build é ponto único de falha)
- **T6** Limiter é hard-clip (mp3 estourado distorce; soft-knee deferido)
- **T7** Token único compartilhado (sem revogar device individual; token na URL aparece em logs)
- **T8** Edição concorrente sem tratamento (dois celulares no mesmo board = último vence)
- **T9** Zero crash reporting (não dá pra diagnosticar problema remoto de amigo)

### UX / polish
- **U1** Sem scrubber "tocando agora" / loop
- **U2** Sem onboarding de primeira execução
- **U3** Sem atalhos de teclado na web/mobile
- **U4** Sons órfãos (`position: null`) não aparecem bem na UI
- **U5** Update prompt sem "pular esta versão" (pergunta todo boot)
- **U6** `MainWindow.cs` ~950 linhas (manutenção); Flutter sem widget tests

---

## Milestone 1 — Paridade & distribuição sólida (v1.1)

**Objetivo:** qualquer pessoa instala e usa em qualquer superfície (PC nativo / web / Android) com as mesmas features; releases são automáticos; sem avisos assustadores.

**Gaps:** T1, F1, T5, T2, U5, T3.

**Escopo e abordagem:**

- **T1 — Web UI com boards + volume.** Portar pra web o que o mobile já tem: seletor de boards (lista + trocar + criar/renomear/apagar via endpoints existentes), slider de volume por som, badge de % na tile. Reutiliza os endpoints `/api/boards/*` e `/api/sounds/{id}/volume` que já existem. Arquivos: `src/Soundpad/wwwroot/{app.js, styles.css, index.html}`.

- **F1 — Normalização de volume.** Ao adicionar um som, calcular o ganho de normalização (peak ou LUFS-aproximado) e armazenar um fator por som no `SoundEntry` (novo campo `NormalizeGainDb` ou similar, schema v5). Aplicar no `PlaybackEngine` junto com o volume global e o per-sound. Toggle global "normalizar" + opção de recalcular. Pure-managed (analisar o PCM já decodificado no `SoundCache`). Sem dependência externa.

- **T5 — CI/CD via GitHub Actions.** Workflow que, ao push de tag `v*`, builda desktop (Velopack via `vpk`) + Android (`flutter build apk`) e publica o release — replicando `publish-release.ps1` na nuvem. Resolve o ponto único de falha (máquina local). Precisa: runner Windows, secrets (signing — ver T2; keystore Android), e tratamento do VB-Cable bundle (baixar no CI ou commitar o zip num release de assets privado). Desafio conhecido: o VB-Cable zip está gitignored; no CI baixar de fonte confiável ou armazenar como secret/artifact.

- **T2 — Code signing (rota grátis OSS).** Investigar **Azure Trusted Signing** (US$ ~10/mês, barato) ou **SignPath.io Free for OSS** (grátis pra projetos open-source, que é o caso). Assinar `ResonApp-win-Setup.exe` + binários via `vpk pack --signParams`. Elimina o SmartScreen e suaviza o UAC do VB-Cable. Se nenhuma rota grátis se aplicar, documentar como "comprar cert depois" e seguir sem (não-bloqueante pro resto do milestone).

- **U5 — "Pular esta versão".** Persistir em config/localStorage a versão "pulada"; o prompt de update (desktop Velopack + Android) respeita. Não pergunta de novo até existir versão mais nova que a pulada.

- **T3 — Limpeza da migração.** Documentar claramente (README + dialog no app) que usuários Inno antigos devem desinstalar a versão Program Files após migrar pro Velopack. Opcional: o app detecta uma instalação Inno órfã e oferece abrir "Adicionar/Remover Programas".

**Critério de pronto:** web com paridade total de boards/volume; sons normalizados tocam no mesmo nível; um `git push` de tag gera o release completo sem máquina local; instalador não dispara SmartScreen (se signing viável); update prompt tem "pular".

**Precisa de plano próprio.**

---

## Milestone 2 — QoL do dia-a-dia (v1.2)

**Objetivo:** o app parece completo e agradável de usar todo dia.

**Gaps:** F2, F3, U1, U2, U4, F6.

**Escopo e abordagem (resumo — detalhar no plano):**

- **F2 — Editor/trim.** Waveform simples na web/mobile + WPF; selecionar início/fim; salvar o trecho como novo arquivo (ou metadados de trim no `SoundEntry`: `TrimStartMs`/`TrimEndMs`, aplicados no playback sem reescrever o arquivo — preferível, reversível). Backend: o `SoundSampleProvider` respeita o trim.
- **F3 — Busca + tags + favoritos.** Campo `Tags: string[]` + `Favorite: bool` no `SoundEntry`. Busca filtra por label/tag. Ordenações: mais tocados (`playCount`), recentes (`lastPlayedAt`), favoritos. UI nas três superfícies.
- **U1 — Scrubber + loop.** Estado de "tocando agora" expõe posição/duração via WS; UI mostra barra de progresso e botão loop. `PlaybackEngine` ganha modo loop e reporta posição.
- **U2 — Onboarding.** Primeira execução: tour rápido (parear celular, configurar Discord, adicionar som). WPF + mobile.
- **U4 — Sons órfãos.** Área "Sem posição" na web/mobile (WPF já tem parcialmente), arrastável pra grade.
- **F6 — Ducking.** Detectar fala no mic (gate por nível) → abaixar o volume do som ativo enquanto fala. Configurável (on/off, threshold, atenuação).

**Precisa de plano próprio.**

---

## Milestone 3 — Diferenciais (v1.3)

**Objetivo:** features que nenhum concorrente tem, explorando a arquitetura PC+celular.

**Gaps:** D1, F8, D2, D3.

**Escopo e abordagem (resumo — detalhar no plano):**

- **D1 — Modo festa multi-celular.** Vários celulares pareiam no mesmo PC e todos disparam sons. A base WebSocket já suporta múltiplos clientes; falta: nome/cor por participante, feedback de "quem tocou o quê", e (opcional) permissões (host vs convidado). Baixo esforço, alto fator-uau.
- **F8 — TTS.** Endpoint `POST /api/tts` → sintetiza (WinRT neural, grátis/offline; fallback SAPI5) → toca pelo mixer. Celular: campo de texto + voz/velocidade + frases salvas. Engine decidida no plano (investigar vozes neurais disponíveis na máquina).
- **D2 — Clipar últimos N segundos.** Buffer circular de loopback WASAPI sempre gravando os últimos ~30s do que o PC reproduz; botão "clipar" → salva + abre o trim (F2) → vira som. Captura o momento engraçado da call.
- **D3 — Importar de YouTube/link.** Colar URL → baixar áudio (yt-dlp bundled ou similar) → trim → vira som. Atenção a licenciamento/ToS — documentar uso pessoal.

**Precisa de plano próprio. Cada diferencial pode até ser um plano separado.**

---

## Milestone 4 — Robustez & manutenção (v1.4)

**Objetivo:** confiabilidade e manutenibilidade.

**Gaps:** T4, T9, T8, T6, T7, U6.

**Escopo e abordagem (resumo — detalhar no plano):**

- **T4 — Mic hot-plug default.** Honrar `OnDefaultDeviceChanged` no `DeviceChangeNotifier`: quando `MicDevice == null` (padrão do sistema) e o default muda, reconstruir o pipeline de mic.
- **T9 — Crash reporting.** Logger já existe; adicionar captura de exceção não-tratada → arquivo de relatório + (opcional, opt-in) envio. Considerar Sentry (tem tier grátis) ou um endpoint simples.
- **T8 — Edição concorrente.** Versionar o config (etag/revision); rejeitar escrita stale com 409 + reload no cliente. Evita dois celulares se sobrescrevendo.
- **T6 — Limiter soft-knee.** Trocar o hard-clip por soft-knee em 0.95 (configurável). Reduz distorção em material já estourado.
- **T7 — Token por dispositivo.** Múltiplos tokens nomeados + revogar individual no WPF. Mover o token do query-param pra subprotocol no WS (não aparecer em logs).
- **U6 — Manutenção.** Refatorar `MainWindow.cs` (extrair componentes); adicionar widget tests no Flutter.

**Precisa de plano próprio.**

---

## Milestone 5 — Power-user (v1.5, opcional)

**Objetivo:** features de usuário avançado, menor prioridade.

**Gaps:** F4, F5, F7.

- **F4 — Hotkeys globais de teclado no PC.** Registrar hotkeys globais (Win32 `RegisterHotKey`) → disparar sons sem alt-tab nem celular.
- **F5 — Overlay in-game.** Overlay transparente sobre o jogo (mais complexo; avaliar viabilidade — possivelmente fora de escopo realista sem um framework de overlay).
- **F7 — Multi-saída com volume por device.** Tocar simultaneamente em N dispositivos com níveis independentes.

**Precisa de plano próprio. F5 pode ser cortado se inviável.**

---

## Concerns transversais

- **Versionamento de schema:** cada milestone que adiciona campos ao `SoundEntry`/`SoundConfig` incrementa `SchemaVersion` com migração (padrão já estabelecido: v1→v4). M1 (F1) → v5; M2 (F2/F3) → v6; etc.
- **Paridade tri-plataforma:** toda feature de UI precisa ser pensada pra WPF + web + mobile. Backend primeiro (endpoint + DTO), depois as três UIs. A web é a que mais atrasa — não esquecer.
- **Auto-update como rede de segurança:** com Velopack + APK updater funcionando, cada milestone publicado chega aos usuários sem fricção. Isso permite releases incrementais frequentes.
- **Testes:** manter a disciplina de testes do backend (.NET, 237+ hoje). UI mobile/web tem cobertura fraca — M4 endereça parcialmente.

## Não-objetivos (explícitos)

- **iOS** — Android-only continua. iOS exigiria Mac + reescrita de partes nativas.
- **Voz clonada / cloud TTS premium** — quebra o posicionamento grátis/local/privado. TTS fica em engine local (M3).
- **Loja (Play Store / MS Store)** — distribuição via GitHub Releases + auto-update já resolve. Loja é decisão futura de negócio.
- **Monetização / contas / cloud sync** — fora de escopo; Reson é local-first.
- **Reescrever o namespace interno `Soundpad.*`** — permanece interno; só a marca é Reson.

## Sequência recomendada

M1 → M2 → M3 → M4 → M5. M1 e M2 são pré-requisitos de "produto sólido"; M3 é onde o Reson vira único; M4 é a base pra escalar uso; M5 é opcional. Dentro de cada milestone, backend antes das UIs, e a web não pode ficar pra trás de novo.
