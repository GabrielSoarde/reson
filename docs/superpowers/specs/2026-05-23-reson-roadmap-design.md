# Reson — Gap-Resolution Roadmap

**Data:** 2026-05-23 (rev. 2026-05-24 após review de sequenciamento)
**Status:** Approved (aguardando review)
**Tipo:** Roadmap spec (cada milestone vira seu próprio plano de implementação)

## Visão geral

Reson é um soundpad para Windows controlado pelo celular (PC servidor .NET/WPF + app Android Flutter + UI web), com mic passthrough + mixer, múltiplos boards, volume por som, e auto-update (Velopack no desktop, APK self-hosted no Android). Está em produção na v1.0.3, distribuído via GitHub Releases (github.com/GabrielSoarde/reson).

Este documento cataloga **todos os gaps conhecidos** e os organiza em milestones sequenciados. Não é uma spec de implementação detalhada — é o roadmap. **Cada milestone recebe seu próprio ciclo spec→plano→implementação.** O Milestone 1 está descrito com mais profundidade por ser o próximo.

## Princípio de priorização

**"Sólido pra distribuir" primeiro.** A ordem prioriza o que afeta diretamente quem instala, depois os diferenciais competitivos, depois polish e features de power-user.

**Definição operacional de "sólido pra distribuir"** (refinada no review): um amigo instala e (a) tem as mesmas features em qualquer superfície, (b) o áudio não distorce nem para sozinho quando o hardware muda, e (c) quando algo quebra, dá pra diagnosticar remotamente. Por isso, **confiabilidade de áudio e diagnosticabilidade são parte do M1**, não dívida adiável — adiá-las por quatro versões contradiz o próprio lema. Escolhido pelo dono do produto em 2026-05-23.

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
- **T7a** Token de auth na URL do WS (aparece em logs/histórico) — **segurança**, não robustez
- **T7b** Token único compartilhado, sem revogar device individual — **feature**
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

## Milestone 1 — Distribuição sólida (v1.1) — **t-shirt: L**

**Objetivo:** o release "sem vergonha". Paridade entre superfícies + áudio confiável + diagnosticabilidade. Tudo aqui afeta diretamente a experiência de quem instala.

**Gaps:** T1, F1, T6, T4, T9, U5, T7a.

**Por que estes (e não a infra de release):** o review apontou — corretamente — que T4 (mic para ao trocar de fone), T6 (mp3 estourado distorce) e T9 (não dá pra diagnosticar nada) são literalmente "não-sólido". Foram movidos do M4 pra cá. Em contrapartida, a infra de release (T5/T2/T3) saiu do M1 (ver track separado abaixo): são os itens de maior risco e dependência de terceiros, e não devem travar a percepção de "pronto" das features que entregam valor visível.

**Escopo:**
- **T1 — Web UI com boards + volume.** Portar pra web o que o mobile já tem (seletor de boards via `/api/boards/*`, slider de volume por som via `/api/sounds/{id}/volume`, badge de %). Arquivos: `wwwroot/{app.js, styles.css, index.html}`.
- **F1 — Normalização de volume.** Campo por som no `SoundEntry` (`NormalizeGainDb`), calculado ao adicionar (peak/LUFS-aprox sobre o PCM já no `SoundCache`), aplicado no `PlaybackEngine` junto com volume global e per-som. Toggle global + recalcular. Pure-managed, sem dep externa. Schema → v5.
- **T6 — Limiter soft-knee.** Trocar o hard-clip por soft-knee em 0.95 (configurável). Reduz distorção em material estourado. Parte da "confiabilidade de áudio" do lema.
- **T4 — Mic hot-plug default.** Honrar `OnDefaultDeviceChanged` no `DeviceChangeNotifier`: quando `MicDevice == null` e o default muda, reconstruir o pipeline de mic. Sem isso o mic do amigo para ao trocar de fone.
- **T9 — Crash reporting.** Handler de exceção não-tratada → relatório em arquivo (`%LocalAppData%\Reson\logs\crash-*.txt`) + envio opt-in (avaliar Sentry tier grátis ou endpoint simples). **Quase pré-requisito do resto do roadmap** — sem isso, todo milestone publicado é teste cego em produção. Por isso entra cedo.
- **U5 — "Pular esta versão".** Persistir versão pulada; prompt (Velopack + Android) respeita.
- **T7a — Token fora da URL.** Mover o token do query-param `?t=` pro subprotocol do WebSocket. Barato, e o vazamento em logs existe desde já. Severidade modesta em LAN confiável, mas é segurança, não dívida adiável.

**Critério de pronto (absoluto — sem "se viável"):**
- Web tem paridade total de boards/volume com o mobile.
- Sons tocam no mesmo nível percebido (normalização ativa por padrão).
- mp3 estourado não distorce audível (soft-knee).
- Trocar o dispositivo de áudio padrão do Windows com o app aberto não derruba o mic.
- Crash não-tratado gera relatório local.
- Update prompt tem "pular esta versão".
- Token não aparece mais na URL do WS.

(Note que SmartScreen/signing **não** está nos critérios do M1 — foi removido pro track de infra, justamente pra um critério de pronto não depender de aprovação de terceiros.)

**Precisa de plano próprio.**

---

## Track paralelo — Infra de release (sem número de versão de usuário) — **t-shirt: M, mas alto risco/incerteza**

Itens de infraestrutura que **não travam features de usuário** e podem aterrissar quando estiverem prontos. Separados do M1 porque têm risco e dependência de terceiros — não devem segurar a percepção de um release de usuário.

- **T5 — CI/CD via GitHub Actions.** Workflow em runner Windows que builda desktop (Velopack `vpk`) + Android e publica no push de tag `v*`. Resolve o ponto único de falha (máquina local). Risco conhecido: VB-Cable zip está gitignored — no CI, baixar de fonte confiável ou guardar como secret/artifact; keystore Android como secret.
- **T2 — Code signing.** Investigar **SignPath.io Free for OSS** (grátis pra open-source, que é o caso) ou **Azure Trusted Signing** (~US$10/mês). Assinar via `vpk pack --signParams`. Elimina SmartScreen + suaviza UAC do VB-Cable. **Depende de aprovação de terceiros** — por isso fora do critério de pronto de qualquer milestone de usuário. Quando sair, melhora a experiência retroativamente (próximos releases assinados).
- **T3 — Limpeza da migração Inno→Velopack.** README + dialog claro: usuário Inno antigo desinstala a versão Program Files após migrar. Opcional: detectar instalação Inno órfã e oferecer "Adicionar/Remover Programas".

**Cada item pode ser feito independente; T2 explicitamente sem prazo (terceiros).**

---

## Milestone 2 — QoL do dia-a-dia (v1.2) — **t-shirt: L**

**Objetivo:** o app parece completo e agradável de usar todo dia.

**Gaps:** F2, F3, U1, U2, U4, F6.

- **F2 — Editor/trim.** Trim não-destrutivo: `TrimStartMs`/`TrimEndMs` no `SoundEntry` aplicados no playback (reversível, não reescreve o arquivo). Waveform simples nas três superfícies. Schema → v6.
- **F3 — Busca + tags + favoritos.** `Tags: string[]` + `Favorite: bool` no `SoundEntry`. Busca por label/tag; ordenar por mais tocados/recentes/favoritos (dados já existem).
- **U1 — Scrubber + loop.** "Tocando agora" expõe posição/duração via WS; UI com barra + loop. `PlaybackEngine` ganha modo loop e reporta posição.
- **U2 — Onboarding.** Primeira execução: tour (parear celular, configurar Discord, adicionar som).
- **U4 — Sons órfãos.** Área "Sem posição" nas três superfícies, arrastável pra grade.
- **F6 — Ducking.** Gate de nível no mic → abaixa o som ativo enquanto fala. Configurável.

**Precisa de plano próprio.**

---

## Milestone 3 — Diferenciais (v1.3) — **t-shirt: XL (vários sub-planos)**

**Objetivo:** features que nenhum concorrente tem, explorando a arquitetura PC+celular.

**Gaps:** D1, F8, D2, D3.

- **D1 — Modo festa multi-celular.** **Escopo decidido no review: v1 é trigger-only.** Vários celulares pareiam e todos *disparam* sons no mesmo board; o nome/cor do participante e o feedback de "quem tocou" são a feature. Disparar é POST `/api/play` — **não** esbarra na edição concorrente. **Edição colaborativa** (vários editando o mesmo board ao vivo) fica fora do D1 e **depende do T8 (M4)** — se for desejada, é um incremento posterior, não parte do modo festa inicial. Esta dependência D1-colaborativo → T8 está declarada aqui de propósito, não pra ser descoberta na implementação.
- **F8 — TTS.** `POST /api/tts` → sintetiza (WinRT neural grátis/offline; fallback SAPI5) → toca pelo mixer. Celular: texto + voz/velocidade + frases salvas. Engine confirmada no plano.
- **D2 — Clipar últimos N segundos.** Buffer circular de loopback WASAPI gravando os últimos ~30s; botão "clipar" → salva → abre o trim (F2) → vira som. **Depende de F2 (M2).**
- **D3 — Importar de YouTube/link.** URL → baixar áudio (yt-dlp bundled) → trim → som. Documentar uso pessoal/ToS.

**Cada diferencial pode ser um plano separado.**

---

## Milestone 4 — Robustez avançada & manutenção (v1.4) — **t-shirt: M**

**Objetivo:** confiabilidade e manutenibilidade que não couberam no M1 (que pegou a robustez de distribuição mínima).

**Gaps:** T8, T7b, U6.

- **T8 — Edição concorrente.** Versionar o config (revision/etag); escrita stale → 409 + reload no cliente. **Pré-requisito da edição colaborativa do modo festa** (ver D1).
- **T7b — Tokens revogáveis.** Múltiplos tokens nomeados + revogar individual no WPF (a parte de segurança "token fora da URL" já foi no M1 como T7a).
- **U6 — Manutenção.** Refatorar `MainWindow.cs` (extrair componentes); widget tests no Flutter.

**Precisa de plano próprio.**

---

## Milestone 5 — Power-user (v1.5, opcional) — **t-shirt: M (F5 pode ser cortado)**

**Objetivo:** features de usuário avançado, menor prioridade.

**Gaps:** F4, F5, F7.

- **F4 — Hotkeys globais de teclado no PC.** Win32 `RegisterHotKey` → disparar sons sem alt-tab nem celular. **t-shirt: S.**
- **F5 — Overlay in-game.** Overlay transparente sobre o jogo. **Alto risco/complexidade — avaliar viabilidade; pode ser cortado.** **t-shirt: XL se viável.**
- **F7 — Multi-saída com volume por dispositivo.** Tocar em N dispositivos com níveis independentes. **t-shirt: M.**

**Precisa de plano próprio.**

---

## Concerns transversais

- **Versionamento de schema (encadeável).** Verificado no código (`SoundLibrary.Load`): a migração estrutural roda pra qualquer `schemaVersion < 4` e o stamp de versão avança até o atual, então um usuário que pular versões (ex: v4 direto pra v6 porque ignorou updates) é migrado corretamente em sequência. **Regra pra futuros milestones:** campos puramente aditivos (novo campo com default) funcionam pelo deserializer + stamp. Mas qualquer migração que exija **transformação de dados** (não só um default) precisa de um step explícito e **idempotente** vN→vN+1 que rode independente de quantas versões foram puladas. Migrações nunca podem assumir "a anterior já rodou neste boot" sem checar a versão de origem. Na prática, com o auto-update funcionando (Velopack + APK), pular versões é raro — mas as migrações encadeiam de qualquer forma.
- **Paridade tri-plataforma.** Toda feature de UI: backend primeiro (endpoint + DTO), depois WPF + web + mobile. A web é a que mais atrasa — não esquecer (T1 existe justamente por isso).
- **Auto-update como rede de segurança.** Com Velopack + APK updater, cada milestone chega aos usuários sem fricção, permitindo releases incrementais frequentes.
- **Crash reporting como base (T9 no M1).** Antes de publicar M2+, ter diagnosticabilidade. É o que torna releases incrementais seguros em vez de testes cegos.
- **Testes.** Manter a disciplina do backend (.NET, 237+ hoje). UI mobile/web fraca — M4 (U6) endereça parcialmente.

## Não-objetivos (explícitos)

- **iOS** — Android-only continua. iOS exigiria Mac + reescrita de partes nativas.
- **Voz clonada / cloud TTS premium** — quebra o posicionamento grátis/local/privado. TTS fica em engine local (M3).
- **Loja (Play Store / MS Store)** — distribuição via GitHub Releases + auto-update já resolve.
- **Monetização / contas / cloud sync** — fora de escopo; Reson é local-first.
- **Reescrever o namespace interno `Soundpad.*`** — permanece interno; só a marca é Reson.

## Sequência recomendada

M1 (distribuição sólida) → M2 (QoL) → M3 (diferenciais) → M4 (robustez avançada) → M5 (opcional). O **track de infra de release** (T5/T2/T3) roda em paralelo, sem travar nenhum milestone de usuário; T2 (signing) sem prazo por depender de terceiros. Dependências cross-milestone declaradas: D2→F2, D1-colaborativo→T8. Dentro de cada milestone: backend antes das UIs, e a web não pode ficar pra trás.
