# Soundpad — Design Spec

**Data:** 2026-05-22
**Status:** Approved (aguardando review)
**Autor:** AP2 QuantumSolutions

## Visão geral

Soundpad para Windows que permite tocar sons dentro do Valorant, Discord, TeamSpeak e qualquer outro app que enxergue um microfone — usando o **celular como controle remoto** (estilo Deckboard / Stream Deck). O usuário conecta o celular via navegador na mesma Wi-Fi do PC, toca em botões em uma grade tipo Stream Deck, e o som é roteado para um dispositivo de áudio virtual (VoiceMeeter) que os jogos/voice chats enxergam como microfone.

### Objetivos

- Substituir o uso do Deckboard + soundpad terceiro por uma solução própria e customizável.
- Disparo de sons via celular durante partidas (sem alt-tab).
- Roteamento limpo para VoiceMeeter, com opção de também tocar no fone do usuário (monitor pessoal).
- Gestão de sons (upload, renomear, cor, posição na grade) feita do próprio celular.

### Não-objetivos (v1)

- Suporte a Linux/macOS — só Windows.
- Hotkeys globais de teclado no PC.
- Volume por som individual (só volume global).
- Edição de áudio (trim, fade, normalize).
- Multi-usuário / contas — apenas um token compartilhado de pareamento.
- Categorias / abas / múltiplas páginas de grade.
- Empacotamento como instalador MSI — só `.exe` self-contained.
- **Polifonia (tocar mais de um som ao mesmo tempo):** v1 é estritamente monofônico — disparar um novo som corta o anterior. Decisão de produto.
- Sons curtos vs longos: sem distinção. Cache pré-decodificado vale para qualquer duração até o limite do arquivo (25 MB).

## Pré-requisitos do usuário

- Windows 10/11.
- **VoiceMeeter** instalado e configurado (Banana ou Potato). O usuário decidiu instalar.
- Celular e PC na mesma rede Wi-Fi local.

A configuração do VoiceMeeter (qual saída do PC vira "VoiceMeeter Input" e como apontar Discord/Valorant para a saída virtual como microfone) faz parte do **README**, não do código do app.

## Arquitetura

```
+-----------------------+                  +-------------------+
|      CELULAR          |  HTTP/WS Wi-Fi   |   PC (Windows)    |
|  Navegador            +----------------->|  Soundpad.exe     |
|  http://192.168.x.x:8080                 |  (ASP.NET Core)   |
+-----------------------+                  |  + NAudio         |
                                           +---------+---------+
                                                     |
                                       +-------------+--------------+
                                       |                            |
                                       v                            v
                            +----------------------+    +------------------+
                            |  VoiceMeeter Input   |    |  Fone (default)  |
                            |  (vai pro Valorant/  |    |  (so quando      |
                            |   Discord)           |    |   monitor=ON)    |
                            +----------------------+    +------------------+
```

### Componentes do `Soundpad.exe`

- **Kestrel web server** (ASP.NET Core minimal API) escutando em `0.0.0.0:<port>`. Aceita conexões da LAN.
- **PlaybackEngine** (NAudio) — API pública que posta comandos numa fila consumida pela **`AudioEngineThread`**: thread STA dedicada que é a única que cria, opera e descarta qualquer `WasapiOut` (afinidade de COM/WASAPI). Decodifica via `SoundCache` (LRU) e toca em 1 ou 2 saídas simultâneas.
- **SoundLibrary** — carrega/grava `config.json` (escrita atômica via tmp+rename). Toda operação de escrita serializada por lock dedicado.
- **StateHub** — WebSocket que empurra eventos pra todos os clientes conectados; eventos carregam `originId` pra evitar eco no cliente que originou a ação.
- **AuthTokenMiddleware** — valida `X-Auth-Token` (HTTP) e `?t=` (WebSocket) em todos os endpoints `/api/*` e `/ws`.
- **LanAdapterPicker** — detecta o IP da LAN priorizando Ethernet/Wireless com gateway default.
- **TrayIcon** (`NotifyIcon`) — menu: IP/porta atual (clicável → copia URL), "Mostrar QR code" (janela 300×300 com QR via `QRCoder`), "Abrir pasta de sons", "Trocar adapter de rede", "Regenerar token", "Sair".

### Fluxo de um disparo de som

1. Usuário toca botão no celular → `POST /api/play/{soundId}`.
2. Backend consulta `SoundLibrary` para resolver o caminho do arquivo.
3. `PlaybackEngine.Play(soundId)`:
   - Para e descarta o player atual (se houver).
   - Abre um novo `AudioFileReader` apontando para `VoiceMeeter Input`.
   - Se `monitorEnabled == true`, abre um segundo `AudioFileReader` no dispositivo padrão do sistema (fone do usuário).
   - Inicia ambos os `WasapiOut`.
4. `StateHub` emite `playing { soundId }` no WebSocket — clientes destacam o botão.
5. Backend responde `200 OK`.

## Pipeline de áudio (NAudio)

### Detecção do dispositivo VoiceMeeter

- No boot, `DeviceLocator` enumera saídas via `MMDeviceEnumerator(DataFlow.Render)` e procura por `FriendlyName` contendo "VoiceMeeter Input" (case-insensitive). Aceita variantes: "VoiceMeeter Input", "VoiceMeeter VAIO3 Input", "VoiceMeeter Aux Input".
- Se achar, salva o nome exato em `config.json` para uso futuro.
- Se não achar, exibe erro no tray + log: "VoiceMeeter não encontrado. Instale em https://vb-audio.com/Voicemeeter/." App segue rodando — o usuário pode trocar o dispositivo manualmente no `config.json` ou via API.

### Decodificação e cache

**Pré-decodificação em memória (uma vez, cache no `SoundCache`):**
- Quando um som é tocado pela primeira vez, é decodificado completamente para PCM `float32` interleaved (`AudioFileReader` → `ISampleProvider` → consumido até EOF para `float[]`) e cacheado junto com o `WaveFormat` resultante.
- Estrutura cacheada: `record CachedSound(WaveFormat Format, float[] Samples)`. Sempre **IEEE float** — `WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)`. Documentar isso explicitamente porque `RawSourceWaveStream` aceita qualquer formato e instanciar PCM16 sobre `float[]` produz ruído branco.
- LRU: 50 entradas. Entries despejadas são re-decodificadas sob demanda. Decode é barato (poucos ms por som curto).

**Por que pré-decodificar:** decodificar uma vez serve ambos os players (game + monitor), isola playback de hiccups de I/O, e simplifica a stream construction.

### Dispositivos de saída (game + monitor)

**Dispositivo de game** = `audioDevice` no config (default detectado pelo `DeviceLocator` = VoiceMeeter Input).

**Dispositivo de monitor** = campo novo `monitorDevice` no config:
- `null` (default) → **monitor desabilitado mesmo se `monitorEnabled == true`** até o usuário escolher. Front mostra warning: "Configure o dispositivo de monitor nas opções."
- String não-vazia → nome exato de um endpoint render do WASAPI.

**Por que não usar o default do sistema como fallback do monitor:** num setup VoiceMeeter típico, o default do sistema *já é* o VoiceMeeter — usar "default" como monitor faria o som ir pro VoiceMeeter duas vezes, dobrando o nível pro jogo e não tocando no fone. Forçar escolha explícita evita esse pé-na-orelha silencioso.

A tela de settings na UI (e o menu do tray) lista todos os endpoints WASAPI render disponíveis pra escolha do `monitorDevice`. Endpoints que somem (ex: fone desconectado) ficam marcados.

### Stream construction

Para cada `Play(soundId)`:
- Recupera `CachedSound` (decode se cache miss).
- Cria um `MemoryStream(samples_bytes, writable: false)` **por player** (game e monitor). Ambos referenciam o mesmo `byte[]` subjacente (zero-copy) mas têm `Position` independente. **Não compartilhar a mesma `MemoryStream` entre players** — `Position` colideria.
- Envelopa cada MemoryStream em `RawSourceWaveStream(stream, format)` (formato cacheado).
- Aplica volume via `VolumeSampleProvider` envolvendo a `RawSourceWaveStream`. **O valor de volume persiste entre Play()s; a instância de `VolumeSampleProvider` é reconstruída a cada Play sobre a nova fonte** — o que continua é o número `volume` armazenado no engine, não o objeto. `VolumeCommand` em runtime atualiza tanto o número quanto o `Volume` do provider atual (se houver player ativo).

### Engine com thread dedicada de áudio

`WasapiOut` (COM/WASAPI) tem afinidade de thread — criar numa thread do pool Kestrel e descartar no callback `PlaybackStopped` (outra thread do pool) gera `COMException` esporádico. **Solução:** o `PlaybackEngine` mantém uma **única thread dedicada de áudio** (STA) que é a *única* que toca em qualquer `WasapiOut`.

```
PlaybackEngine
  ├─ Thread "audio-engine" (STA, dedicada, loop infinito)
  │     consome BlockingCollection<AudioCommand>
  │     executa cada comando: PlayCommand, StopCommand, VolumeCommand,
  │                           DisposeCommand, PlaybackEndedCommand
  ├─ campos _gamePlayer, _monitorPlayer (acessados SÓ pela thread de áudio)
  ├─ _currentPlayToken (long, incrementado a cada Play() — ver lifecycle)
  └─ API pública: Play(id), Stop(), SetVolume(v), SetMonitorEnabled(b)
       → cada uma é uma post de comando na fila (não-bloqueante)
```

**Comandos:** todos os métodos públicos do `PlaybackEngine` chamados pela API HTTP **postam** comandos na fila e retornam imediato. A thread de áudio drena a fila em ordem FIFO. Isso elimina a contenção entre handler de fim natural e Play()/Stop() concorrentes.

### Lifecycle: fim natural do som

Esse é o cenário "mais comum e mais negligenciado". Quando o `WasapiOut` chega ao EOF do stream, dispara `PlaybackStopped` numa thread do .NET ThreadPool — não na thread de áudio. O handler **só** posta um `PlaybackEndedCommand(playToken, source)` na fila e retorna. O command é processado pela thread de áudio.

**Token de play:**
- Cada `Play(id)` recém-iniciado recebe `_currentPlayToken = ++counter`. Esse token é capturado pelos players criados nessa execução.
- `PlaybackEndedCommand` carrega o token. Se `command.Token != _currentPlayToken`, ignora — significa que o player que terminou é de um Play() antigo já substituído. Sem isso, o fim natural do som A zeraria o `nowPlaying` que já é do som B que acabou de começar.

**Cadeia de dispose:** ao tratar `PlaybackEndedCommand` válido (ou ao processar `StopCommand`/iniciar novo `PlayCommand`), dispose **em ordem** de cada player: `WasapiOut.Stop()` → `WasapiOut.Dispose()` → `RawSourceWaveStream.Dispose()` → `MemoryStream.Dispose()`. Importante porque o `WasapiOut` mantém referência ao stream subjacente; descartar fora de ordem pode tentar ler stream já fechada. Não dispor o `byte[]` cacheado no `SoundCache` — ele sobrevive até evict por LRU.

**Critério de "som terminou":**
- Monitor desabilitado → fim quando `_gamePlayer.PlaybackStopped` dispara.
- Monitor habilitado → fim quando **o player do game** termina (autoridade única). O player do monitor é desligado junto, independente de seu próprio EOF. Evita ambiguidade de "qual dos dois decide".
- Em qualquer caso, ao processar `PlaybackEndedCommand` válido: dispose ambos os players, zera campos, emite `stopped` no WS, zera `nowPlaying`.

**Não confiar em `PlaybackStopped` pra detectar erro:** o evento dispara também quando `Stop()` é chamado manualmente. Distinguir via `StoppedEventArgs.Exception != null` → loga erro mas mesmo tratamento de dispose+limpa.

### Comportamento dos comandos

- **PlayCommand(id, token):** dispose dos players atuais (se houver), constrói novos, sobe `_gamePlayer` (e `_monitorPlayer` se monitor enabled + monitorDevice válido), registra handler `PlaybackStopped` que posta `PlaybackEndedCommand(token)`, atualiza `nowPlaying = id`, emite `playing` no WS.
- **StopCommand:** dispose dos players atuais, zera campos, emite `stopped` no WS.
- **VolumeCommand(v):** atualiza `VolumeSampleProvider.Volume` em ambos os players (afeta o som atual em tempo real).
- **SetMonitorCommand(b):** atualiza estado interno. Aplica-se **ao próximo** `Play()` — não interrompe o som atual nem liga monitor no meio. Emite `monitorChanged` no WS.
- **SetMonitorDeviceCommand(name):** atualiza `monitorDevice`. Aplica-se ao próximo Play().

### Parâmetros do WasapiOut

- Modo: `AudioClientShareMode.Shared`.
- Latência: `config.latencyMs` (default **50 ms**; subir pra 100 ms se houver glitches).

### Sobre sincronia entre dispositivos

Drift entre clocks físicos é fundamental ao roteamento multi-dispositivo. Mesmo lendo do mesmo `byte[]`, dois `WasapiOut` em hardware diferente consomem em taxas marginalmente distintas (microsegundos por segundo). Imperceptível pra sons curtos típicos de soundboard. Não é problema do app.

### Volume

Volume global único em `[0, 100]` (UI) → `[0.0, 1.0]` (NAudio). Aplicado via `VolumeSampleProvider` em ambos os players (ver "Stream construction"). Mudar volume durante reprodução é processado como `VolumeCommand` e afeta o som atual em tempo real.

### Formatos suportados

- `.mp3`, `.wav`, `.flac` — nativo no `AudioFileReader`.
- `.ogg` — via `NAudio.Vorbis` (extension).

Outros formatos (.m4a, .opus) ficam fora do v1.

## API HTTP

Base URL: `http://{lan-ip}:{port}`. Token de pareamento exigido em todos os endpoints de `/api/*` e `/ws` (ver Segurança).

### Endpoints

| Método | Rota | Auth | Body | Resposta | Descrição |
|--------|------|------|------|----------|-----------|
| `GET`  | `/` | pública | — | `index.html` | Página principal |
| `GET`  | `/static/*` | pública | — | assets | JS/CSS/manifest |
| `GET`  | `/api/state` | sim | — | `StateDto` | Estado completo |
| `POST` | `/api/play/{soundId}` | sim | — | `204` ou `404` | Toca som; corta o anterior |
| `POST` | `/api/stop` | sim | — | `204` | Para tudo |
| `POST` | `/api/monitor` | sim | `{"enabled": bool}` | `204` | Liga/desliga monitor |
| `POST` | `/api/monitor/device` | sim | `{"device": string\|null}` | `204` | Define o dispositivo de monitor (nome WASAPI) |
| `POST` | `/api/volume` | sim | `{"value": 0-100}` | `204` | Ajusta volume global |
| `POST` | `/api/sounds/upload` | sim | `multipart/form-data` | `201` + `SoundEntryDto` | Upload + auto-registra |
| `PUT`  | `/api/sounds/{id}` | sim | `{"label"?, "color"?, "icon"?, "position"?}` | `SoundEntryDto` | Edita metadados; `position` ocupada → swap |
| `DELETE` | `/api/sounds/{id}` | sim | query `?deleteFile=true` | `204` | Remove do registry; arquivo opcional |
| `POST` | `/api/grid` | sim | `{"cols": int, "rows": int}` | `204` | Redimensiona a grade; sons fora dos novos limites viram `position: null` |
| `POST` | `/api/grid/layout` | sim | `{"placements": [{"id", "position"\|null}, ...]}` | `204` | Reordena em lote (drag-and-drop) — atômico |
| `WS`   | `/ws` | sim (`?t=<token>`) | — | mensagens JSON | Eventos em tempo real |

### Schemas

Dois schemas diferentes — **persistido em disco** e **DTO da API**. Não confundir.

**`StateDto`** (resposta de `/api/state`, **runtime apenas**):
```json
{
  "audioDevice": "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
  "monitorDevice": null,
  "monitorEnabled": false,
  "availableOutputDevices": [
    "Speakers (Realtek High Definition Audio)",
    "Headphones (HyperX Cloud II)",
    "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"
  ],
  "volume": 80,
  "grid": { "cols": 3, "rows": 4 },
  "sounds": [ /* SoundEntryDto[] */ ],
  "nowPlaying": null,
  "authRequired": true
}
```

`availableOutputDevices` é a lista atual enumerada do WASAPI (snapshot, atualizada a cada `GET /api/state`). Permite que a tela de settings ofereça as opções.

**`SoundEntryDto`** (runtime, inclui campos derivados):
```json
{
  "id": "raze-ult",
  "file": "raze-ult-br.mp3",
  "label": "RAZE ULT",
  "color": "#3b82f6",
  "icon": null,
  "position": { "col": 1, "row": 0 },
  "missing": false
}
```

Campos derivados em runtime (**não persistidos** em `config.json`):
- `nowPlaying` (em `StateDto`) — id do som atualmente em reprodução, ou `null`.
- `missing` (em `SoundEntryDto`) — `true` quando o arquivo referenciado por `file` não existe no disco.
- `authRequired` — informa o cliente se precisa enviar token.

Tudo que aparece em `config.json` é persistido. O que está aqui mas não está em `config.json` é estado de runtime.

### Eventos WebSocket

Mensagens JSON `{ "type": "...", "originId": "<uuid|null>", "payload": {...} }`:

- `playing` → `{ soundId }`
- `stopped` → `{}`
- `monitorChanged` → `{ enabled }`
- `volumeChanged` → `{ value }`
- `monitorDeviceChanged` → `{ device }`
- `libraryChanged` → `{}` (cliente refaz `GET /api/state`)

**`originId` evita eco no cliente que originou a ação.** Cenário: cliente A arrasta o slider de volume → `POST /api/volume` com header `X-Origin-Id: <uuid-do-cliente-A>` → backend emite `volumeChanged` com mesmo `originId` → cliente A recebe e **ignora** porque o evento veio da sua própria ação (slider já está naquele valor; aplicar de volta causaria jitter no drag). Demais clientes não filtram porque o `originId` não bate. O cliente gera seu `uuid` no load (`crypto.randomUUID()`), guarda em variável.

### Segurança e validação

**Autenticação por token de pareamento.** "LAN é confiável" não é. Casas compartilhadas, repúblicas, Wi-Fi de café — qualquer um na mesma rede pode descobrir o IP e tocar sons. Token simples resolve a 99% dos cenários e não atrapalha o usuário porque o QR code já é o vetor de pareamento.

- `authToken` é gerado no primeiro boot (16 bytes de `RandomNumberGenerator.GetBytes(16)` → hex de 32 chars). Persistido em `config.json`. Pode ser regenerado via "Regenerar token" no menu do tray.
- A URL no QR code embute o token: `http://192.168.0.42:8080/?t=<token>`. O front salva o token no `localStorage` e envia em todo request: header `X-Auth-Token: <token>` para HTTP e como query param `?t=<token>` no WebSocket (header não funciona no WS do browser).
- Endpoints exigem o token. Exceção: `GET /` e `/static/*` (servir HTML/JS/CSS) são públicos — o JS precisa carregar antes de pegar o token da URL.
- Token inválido/ausente → `401 Unauthorized` com body `{"error": "invalid_token"}`.
- O front trata 401 mostrando "Sessão expirada. Escaneie o QR code de novo."
- Comparação do token usa `CryptographicOperations.FixedTimeEquals` (constant-time) pra evitar timing attacks (overkill na prática, mas é uma linha).

**Outras camadas:**
- Servidor escuta em `0.0.0.0:<port>` (LAN). **Não deve ser exposto à internet** — documentado no README.
- CORS aberto (`*`) — só faz diferença em rede local.
- Upload: rejeita extensões fora da whitelist, tamanho máximo 25 MB, filename sanitizado conforme regras detalhadas em "Sanitização de filename".
- `soundId` validado antes de qualquer file I/O (regex `^[a-z0-9-]+$`).
- Path de arquivo construído sempre via `Path.Combine(soundsDir, filename)` e validado com `Path.GetFullPath(result).StartsWith(soundsDirFullPath)` pra defesa em profundidade contra path traversal.

### Limites de tamanho do upload

Configurar **todos os três limites** do Kestrel/MVC pra baterem com os 25 MB do código de aplicação. Sem isso, o framework rejeita antes do nosso código e devolve `BadHttpRequestException` genérica em vez do nosso 400 com mensagem útil:
- `KestrelServerOptions.Limits.MaxRequestBodySize = 26_214_400` (25 MiB).
- `FormOptions.MultipartBodyLengthLimit = 26_214_400` (registrado via `services.Configure<FormOptions>(...)`).
- Atributo `[RequestSizeLimit(26_214_400)]` no endpoint específico de upload (extra-defensivo, sobrescreve qualquer default).

Centralizar a constante: `public const long MaxUploadBytes = 26_214_400;` referenciado em todos os pontos.

## Interface do celular

### Stack

HTML + CSS + JS vanilla. Sem framework. Servido como estático pelo Kestrel a partir de `wwwroot/`.

Arquivos: `index.html`, `app.js`, `styles.css`, `manifest.json`.

### Modo "play" (default)

```
+--------------------------------------+
|  SOUNDPAD              [O] Monitor   |
|                                      |
|  +------+  +------+  +------+        |
|  | AAAA |  | RAZE |  |ANGRY |        |
|  |LUTA  |  | ULT  |  |MONKEY|        |
|  +------+  +------+  +------+        |
|                                      |
|  +------+  +------+  +------+        |
|  | SAD  |  |  +   |  |  +   |        |
|  |MEOW  |  | add  |  | add  |        |
|  +------+  +------+  +------+        |
|                                      |
|       +--------------------+         |
|       |     []  STOP       |         |
|       +--------------------+         |
|                                      |
|       Vol ----o------  70%   [G]     |
+--------------------------------------+
```

- **Topo:** título à esquerda, toggle de monitor à direita (atualiza via `POST /api/monitor`).
- **Grade:** botões responsivos (CSS Grid). 2 colunas em telas <360px, 3 em telas médias, 4+ em telas largas (configurável pela dimensão `cols` do `GridLayout`).
- **Botão de som:** mostra `label` em texto grande, fundo na `color` configurada. Quando o som está tocando, fica com borda animada (recebido via WS `playing`).
- **Paleta de cores (v1):** 8 cores fixas — `#ef4444` (vermelho), `#f97316` (laranja), `#eab308` (amarelo), `#22c55e` (verde), `#06b6d4` (ciano), `#3b82f6` (azul), `#a855f7` (roxo), `#ec4899` (rosa). Backend aceita qualquer string hex `#rrggbb`, mas o seletor no front oferece apenas essas 8. Cor default no upload: `#3b82f6` (azul).
- **Ícone:** campo `icon` reservado no schema mas **não usado no v1** — sempre `null`. Adiciono no v2 se quiser.
- **Botão "+":** ocupa célula vazia. Tap abre o file picker do celular (`<input type="file">`) → upload.
- **Tap longo (300ms) em um botão de som:** abre bottom sheet com opções: Renomear · Cor · Posição · Remover.
- **STOP:** botão grande vermelho fixado abaixo da grade.
- **Volume:** slider horizontal, debounce de 100ms antes de enviar `POST /api/volume`.
- **Engrenagem:** abre modo "editar".

### Modo "editar"

- Reordenação por drag-and-drop via **pointer events** (melhor suporte mobile que HTML5 DnD). Long-press 300ms inicia o drag.
- Cada botão ganha overlay com ícone de "editar".
- Sair do modo edit volta pra "play".

### PWA

`manifest.json` simples: nome "Soundpad", `display: "standalone"`, ícone 512x512. Usuário "Adiciona à tela inicial" no celular → app abre em tela cheia sem barra de endereço.

### WebSocket no cliente

`app.js` abre WS em `ws://{host}/ws` no load, reconecta com backoff exponencial em caso de queda (1s, 2s, 4s, 8s, max 30s). Em qualquer reconexão, refaz `GET /api/state` para sincronizar.

## Gestão de sons e persistência

### Layout em disco

```
soundpad/
├── Soundpad.exe              <- o app
├── config.json               <- estado persistente
└── sounds/                   <- arquivos de audio
    ├── aaaaaaaaaaaaaaaa-e-lutador.mp3
    ├── angy-monkey-mp3.mp3
    ├── raze-ult-br.mp3
    └── sad-meow-song.mp3
```

`config.json` e `sounds/` ficam no mesmo diretório do `.exe` — fácil de fazer backup, fácil de movimentar.

### Schema do `config.json` (persistido)

```json
{
  "schemaVersion": 1,
  "authToken": "8f2c4a1b9e7d3f60",
  "port": 8080,
  "preferredNetworkAdapter": null,
  "audioDevice": "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
  "monitorDevice": null,
  "monitorEnabled": false,
  "volume": 80,
  "latencyMs": 50,
  "grid": { "cols": 3, "rows": 4 },
  "sounds": [
    {
      "id": "raze-ult",
      "file": "raze-ult-br.mp3",
      "label": "RAZE ULT",
      "color": "#3b82f6",
      "icon": null,
      "position": { "col": 1, "row": 0 }
    }
  ]
}
```

**Campos persistidos vs runtime** — só o que está acima vai pro disco. Campos derivados (`missing`, `nowPlaying`, `authRequired`) **nunca** são gravados.

**`schemaVersion`** começa em 1. Quando o v2 mudar o schema, o app detecta `schemaVersion < ATUAL`, executa migrações idempotentes em sequência, e grava o novo `schemaVersion` no final. Se o app é mais antigo que o `schemaVersion` no disco, recusa carregar e mostra erro claro.

**`authToken`** é gerado no primeiro boot (16 bytes aleatórios → hex). Usado pra autenticar requests da LAN (ver seção Segurança).

**`port`** sobrescreve o default 8080 quando definido. Se omitido → 8080.

**`latencyMs`** ajusta o buffer do `WasapiOut`. Default 50ms.

### Inicialização

Ordem de boot:

1. **Carregar config.** Se `config.json` não existe → cria default:
   - `schemaVersion`: `1`.
   - `authToken`: gerado novo.
   - `port`: `8080`.
   - `audioDevice`: resultado do `DeviceLocator`, ou `null`.
   - `monitorEnabled`: `false`. `volume`: `80`. `latencyMs`: `50`.
   - `grid`: `{ "cols": 3, "rows": 4 }`.
   - `sounds`: scan da pasta `sounds/`, cada arquivo vira entry na próxima posição livre.
2. **Migração de schema.** Se `schemaVersion < ATUAL`, roda migrações em sequência (no v1 não há migração; placeholder pra v2+).
3. **Reparo de invariantes.** Detecta posições duplicadas (primeira entry vence, demais ficam `position: null`); detecta entries cujo `file` sumiu (marca `missing: true` em runtime, não persiste). Loga avisos.
4. **Auto-scan.** Arquivos em `sounds/` que **não estão** no config são adicionados na próxima posição livre.
5. **Bind do servidor.** Tenta `Kestrel.Bind(0.0.0.0:port)`. Se falhar com porta ocupada:
   - Tenta as próximas 10 portas (`port+1` … `port+10`).
   - Se alguma funcionar, **persiste a nova porta no config** e segue.
   - Se nenhuma funcionar, mostra erro no tray ("Nenhuma porta livre de 8080 a 8090. Edite `config.json`.") e o app fica em estado degradado (tray ativo, sem servidor).
6. **Detectar IP da LAN.** Em máquina de gamer real existem múltiplas interfaces "up" com IPv4 privado (VirtualBox, VMware, WSL, Hyper-V, Docker, Tailscale, Hamachi). "Primeira da lista" frequentemente erra. Algoritmo:
   - Enumera interfaces via `NetworkInterface.GetAllNetworkInterfaces()`.
   - Filtra: `OperationalStatus == Up`, não loopback, tem IPv4 privado.
   - **Prioriza por:**
     1. Tipo `Wireless80211` ou `Ethernet` (descarta `Tunnel`, `Virtual` quando o `NetworkInterfaceType` indica).
     2. Tem gateway default IPv4 (`GetIPProperties().GatewayAddresses` não vazio).
     3. Nome **não** contém `vEthernet`, `VirtualBox`, `VMware`, `WSL`, `Hyper-V`, `Tailscale`, `Hamachi`, `Loopback` (case-insensitive blocklist).
   - Se múltiplas qualificam, escolhe a primeira da lista ordenada.
   - **Persiste a escolha** em `config.json` como `preferredNetworkAdapter` (Guid `Id` da interface). Em boots seguintes, se aquela interface existe e está up, usa direto — sobrevive a mudanças temporárias de adapter ranking.
   - O menu do tray expõe "Trocar adapter de rede" → submenu com todas as candidatas pra override manual.
7. **Inicializar tray icon.** Em thread STA dedicada. Mostra IP:porta e QR code.
8. **Pronto.** Loga `Soundpad rodando em http://<ip>:<port>/?t=<token-redacted>`.

### Geração de ID

`id` = slug do nome do arquivo sem extensão (lowercase, espaços → `-`, remove non-alphanumeric exceto `-`). Colisão → sufixo `-2`, `-3`, etc.

### Upload

**Toda a transação de upload roda sob o `SoundLibrary._writeLock`** — sanitização, resolução de colisão, gravação em disco, registro de entry e persistência do config são uma operação atômica. Sem isso, dois uploads simultâneos do mesmo nome podem corromper-se mutuamente (TOCTOU).

Sequência dentro do lock:
1. Valida extensão (whitelist `.mp3` / `.wav` / `.ogg` / `.flac`) e tamanho (≤ 25 MB). Falha → 400.
2. Sanitiza filename (ver "Sanitização" abaixo).
3. Resolve colisão: tenta `FileMode.CreateNew` em `sounds/<filename>`. Se lança `IOException` (arquivo existe), sufixa `(2)`, `(3)`... até `(N=100)`. Acima disso, falha → 409.
4. Escreve o arquivo (stream do multipart → file stream).
5. **Gera `id` a partir do filename final já sufixado** (não do nome original) — slug de "raze ult (2).mp3" vira `raze-ult-2`, mantendo `id` e `file` coerentes. Se ainda assim houver colisão de `id` (improvável após o sufixo de arquivo), aplica sufixo `-a`, `-b`, etc.
6. Encontra próxima posição livre na grade (ver abaixo).
7. Cria `SoundEntry` no `_sounds` em memória.
8. Persiste `config.json` (escrita atômica: grava em `.tmp`, fsync, rename).
9. Emite `libraryChanged` no WS.
10. Responde `201 Created` com `SoundEntryDto`.

**Sanitização de filename (Windows-safe):**
- Remove control chars (`\x00`-`\x1F`, `\x7F`).
- Substitui qualquer caractere fora de `[A-Za-z0-9 _.()\-\[\]]` por `_`.
- Rejeita explicitamente `..` (path traversal) e separadores (`/`, `\`).
- Trima trailing dots e spaces (Windows os ignora silenciosamente, criando comportamento inesperado).
- Detecta nomes reservados (`CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, com ou sem extensão, case-insensitive) → prefixa com `_`.
- Se após tudo o nome ficar vazio ou virar só `.<ext>` → usa o `id` gerado como filename (`<id>.<ext>`).

### Posição na grade

Três operações mexem em `position`. Regras explícitas pra que coexistam sem ambiguidade:

**Invariante:** dentro de um snapshot do config, **nenhuma posição é compartilhada por duas entries**. Validado em toda escrita; configs corrompidos manualmente são repaired no boot (primeira entry vence, demais ficam `position: null` = sem posição).

**Sons sem posição** (`position: null`): aparecem na UI numa área "Sem posição" abaixo da grade — clicável pra tocar, e drag arrasta pra dentro da grade.

**Próxima posição livre** (usada no upload): varre `grid.cols × grid.rows` em **row-major** (`(0,0)`, `(1,0)`, `(2,0)`, `(0,1)`...). Primeira célula livre = resultado.

**Auto-expand da grade:** se nenhuma célula está livre durante um upload, `rows += 1` e a nova última-linha-primeira-coluna vira o destino. **Não há auto-shrink.** O usuário pode encolher manualmente via `POST /api/grid` (ver abaixo o que acontece com sons que ficam fora).

**Drag-and-drop (reorder):**
- Soltar em célula vazia → move a entry pra lá.
- Soltar em cima de outra entry → **swap** das duas posições.
- Soltar fora da grade → vira `position: null` (sem posição).
- O front sempre envia o layout inteiro via `POST /api/grid/layout`, não múltiplos `PUT`s. Evita estados intermediários inconsistentes.

**`PUT /api/sounds/{id}` com `position`:** se a posição alvo já está ocupada → swap (mesmo comportamento do drag).

**`POST /api/grid` (encolher):** sons com `position` fora da nova grade ficam `position: null` (sem posição) — não somem nem se reposicionam automaticamente. Visíveis na área "Sem posição".

## Estrutura do projeto .NET

```
soundpad/
├── Soundpad.sln
├── src/
│   └── Soundpad/                       <- projeto unico (net8.0-windows)
│       ├── Soundpad.csproj
│       ├── Program.cs                  <- bootstrap: Kestrel + tray + DI
│       ├── Audio/
│       │   ├── PlaybackEngine.cs       <- API publica (post de comandos)
│       │   ├── AudioEngineThread.cs    <- thread STA dedicada + dispatch loop
│       │   ├── AudioCommands.cs        <- Play/Stop/Volume/SetMonitor/Ended
│       │   ├── SoundCache.cs           <- LRU de PCM float decodificado
│       │   ├── DeviceLocator.cs        <- enumera WASAPI render endpoints
│       │   └── SoundLibrary.cs         <- config + persistencia
│       ├── Network/
│       │   └── LanAdapterPicker.cs     <- detecta IP da LAN com prioridades
│       ├── Security/
│       │   └── AuthTokenMiddleware.cs  <- valida X-Auth-Token / ?t=
│       ├── Api/
│       │   ├── PlaybackEndpoints.cs
│       │   ├── SoundEndpoints.cs
│       │   ├── DeviceEndpoints.cs      <- /api/monitor/device
│       │   └── StateHub.cs             <- WebSocket
│       ├── Models/
│       │   ├── SoundConfig.cs          <- schema persistido (com schemaVersion)
│       │   ├── SoundEntry.cs
│       │   ├── GridLayout.cs
│       │   └── StateDto.cs             <- schema runtime
│       ├── Tray/
│       │   └── TrayIcon.cs             <- NotifyIcon + menu
│       └── wwwroot/
│           ├── index.html
│           ├── app.js
│           ├── styles.css
│           ├── manifest.json
│           └── icon-512.png
├── sounds/                              <- ja existe (4 mp3)
├── docs/superpowers/specs/              <- este arquivo
└── tests/
    └── Soundpad.Tests/
        ├── Soundpad.Tests.csproj
        ├── PlaybackEngineTests.cs
        ├── SoundLibraryTests.cs
        └── ApiTests.cs
```

### Target e pacotes

- TFM: `net8.0-windows` (necessário pelo `NotifyIcon` em `System.Windows.Forms`).
- Pacotes NuGet:
  - `NAudio` — playback.
  - `NAudio.Vorbis` — suporte a OGG.
  - `H.NotifyIcon.WindowsForms` (ou `System.Windows.Forms` direto) — tray icon.
  - `QRCoder` — gera QR code da URL para a janela do tray.
  - Testes: `xunit`, `xunit.runner.visualstudio`, `Microsoft.AspNetCore.Mvc.Testing`, `Moq` (ou `NSubstitute`).
- ASP.NET Core: builtin (`Microsoft.AspNetCore.App` framework reference).

### DI / composição

`Program.cs` registra:
- `SoundLibrary` como singleton.
- `PlaybackEngine` como singleton.
- `DeviceLocator` como singleton.
- `StateHub` como singleton.
- Endpoints via `app.MapGroup("/api").Map...()`.

`TrayIcon` é iniciado em uma thread STA dedicada (requisito do Windows Forms) antes do `app.RunAsync()`.

### Build de release

```
dotnet publish src/Soundpad -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Gera `Soundpad.exe` (~70-90 MB) sem necessidade de instalar .NET no PC alvo.

### Dev workflow

```
dotnet run --project src/Soundpad
```

Console loga: `Soundpad rodando em http://192.168.0.42:8080`. Tray icon aparece. Usuário abre no celular.

## Testes

### PlaybackEngine / AudioEngineThread

- Mock de `IWavePlayer` factory pra não tocar áudio de verdade no CI; mock pode disparar `PlaybackStopped` programaticamente.
- Todos os testes esperam o drain da fila de comandos (helper `await engine.IdleAsync()`).
- Casos:
  - `Play(id)` sem som tocando → cria 1 player (monitor=off) ou 2 (monitor=on + monitorDevice configurado).
  - **`Play(id)` com `monitorEnabled == true` mas `monitorDevice == null` → cria só 1 player (sem warning fatal, só log).**
  - `Play(id)` com som tocando → para o anterior antes de iniciar o novo (assert ordering via mock).
  - `Stop()` → para todos os players e emite `stopped`.
  - Toggle `monitor` no meio da reprodução não afeta o som atual.
  - Mudança de volume durante reprodução atualiza o `VolumeSampleProvider` em tempo real.
  - Cache hit: segunda chamada do mesmo `id` reusa o buffer (assert: decoder não foi chamado).
  - LRU evicta o 51º som.
  - **Fim natural do som: simula `PlaybackStopped` do player do game → engine emite `stopped`, zera `nowPlaying`, dispose dos players. Botão volta ao estado normal.**
  - **Race fim-natural vs novo Play: dispara `PlaybackStopped` do play A no mesmo instante que `Play(B)` é postado → estado final reflete B tocando, não `null`. Token de play stale é ignorado.**
  - **Monitor habilitado: `PlaybackStopped` do monitor player NÃO é tratado como fim (só o game player é autoridade). Se game ainda toca, monitor termina sozinho e fica inativo até o game terminar.**
  - **Affinity de thread: todos os `Create`/`Init`/`Dispose` do `WasapiOut` ocorrem na thread "audio-engine" (assert via captura de `Thread.CurrentThread.ManagedThreadId` no mock).**
  - Two `MemoryStream` separados sobre o mesmo `byte[]` → assert que cursor de um não afeta o outro.

### SoundLibrary

- Sistema de arquivos temporário (xunit `IDisposable` por classe).
- Casos:
  - Boot sem `config.json` cria default a partir da pasta.
  - Boot com `config.json` carrega corretamente.
  - Auto-scan adiciona arquivos novos.
  - Arquivo removido marca entry como `missing` (em runtime, não persiste).
  - Upload sanitiza filename: nomes reservados (`CON.mp3` → `_CON.mp3`), control chars, trailing dots/spaces, nome vazio → fallback pra id.
  - Upload com colisão de nome usa `FileMode.CreateNew` e sufixa `(2)`, `(3)`.
  - **Upload concorrente do mesmo nome:** dois uploads simultâneos do mesmo filename geram dois arquivos distintos (`foo.mp3` e `foo (2).mp3`), nenhum sobrescrito.
  - Grade cheia expande `rows` no próximo upload.
  - `PUT` em `position` ocupada → swap (entries trocam de posição).
  - `POST /api/grid` encolhendo → sons fora viram `position: null`.
  - Repair de duplicatas no boot: primeira vence, demais ficam `position: null`.
  - Migração de schema: lê `schemaVersion: 1` sem mudança; lê `schemaVersion: 999` → recusa com erro.

### LanAdapterPicker

- Mock de `INetworkInterfaceProvider` (lista controlada de interfaces).
- Casos:
  - Setup gamer típico (Ethernet real + VirtualBox + WSL + Tailscale) → escolhe Ethernet.
  - Só uma interface real → escolhe ela.
  - Múltiplas Ethernet (cabo + Wi-Fi) → primeira com gateway default.
  - Adapter persistido em config ainda existe → usa ele direto sem reavaliar prioridade.
  - Adapter persistido sumiu → re-avalia e persiste o novo.
  - Nenhuma interface qualifica → retorna `127.0.0.1` e loga warning.

### API

- `WebApplicationFactory<Program>` para integration tests.
- Casos:
  - `GET /api/state` retorna shape correto.
  - `POST /api/play/{id}` aciona o engine.
  - `POST /api/play/nao-existe` retorna 404.
  - `POST /api/sounds/upload` com arquivo inválido (extensão errada, > 25 MB) → 400.
  - `POST /api/monitor` persiste mudança.
  - **Auth:** todos os `/api/*` sem header `X-Auth-Token` ou com token errado → 401.
  - **Auth WS:** conexão a `/ws` sem `?t=<token>` ou com token errado → fecha imediato com código 1008.
  - `GET /` e `/static/*` funcionam sem token.
  - `POST /api/grid/layout` atômico: validar que rejeita layout com posições duplicadas (400 sem aplicar nada).
  - **Upload > 25 MB rejeitado com mensagem clara** (não com `BadHttpRequestException` genérica — confirma que os 3 limites do Kestrel/FormOptions/RequestSizeLimit estão alinhados).
  - **`originId` no header propaga pro evento WS** (assert que `volumeChanged` carrega o mesmo `originId` enviado no `POST /api/volume`).
  - `POST /api/monitor/device` com nome inexistente → 400 com mensagem listando os disponíveis.
  - `POST /api/monitor/device` com `null` → desabilita monitor implicitamente, persiste.

## Logs

- `Microsoft.Extensions.Logging` com sink em arquivo em `%LOCALAPPDATA%\Soundpad\logs\soundpad-YYYY-MM-DD.log`.
- **Rolling combinado: por dia + por tamanho (10 MB por arquivo).** Quando passa de 10 MB no mesmo dia, gera `soundpad-YYYY-MM-DD-2.log`, `-3.log`, etc. Sem isso, um loop de erro (ex: dispositivo de áudio falhando 1000×/s) enche disco em horas.
- Retenção: 7 dias. Arquivos mais antigos deletados no boot.
- Nível default: `Information`. Configurável via `appsettings.json`.
- Eventos logados: boot (com versão + porta + IP), dispositivo detectado/perdido, play/stop (com `soundId` + duração efetiva), upload (com filename + tamanho), erros com stack.
- Tokens **nunca** são logados (redactados como `<token-redacted>`).

## Riscos e mitigações

| Risco | Mitigação |
|-------|-----------|
| VoiceMeeter não instalado / dispositivo não detectado | Erro claro no tray + log; app sobe em estado degradado; usuário corrige e o `DeviceLocator` re-detecta na próxima `Play()` |
| Porta 8080 ocupada | Auto-fallback pra `port+1`...`port+10`, persiste no config (ver Inicialização). Falha total → tray mostra mensagem clara |
| Latência percebida | Default 50ms shared mode. Subir pra 100ms se houver glitches (campo `latencyMs` no config) |
| Celular cai da Wi-Fi durante uso | Cliente WS reconecta com backoff exponencial (1/2/4/8/...30s) e refaz `GET /api/state` |
| Arquivos em `sounds/` deletados manualmente | Auto-detect na inicialização, marca `missing: true` em runtime, botão fica desabilitado com ícone de alerta |
| Concorrência (vários clientes apertando ao mesmo tempo) | `PlaybackEngine.Play()` e `SoundLibrary.<todas operações de escrita>` serializados por `lock` dedicado. Upload é atômico ponta-a-ponta (sanitização → CreateNew → registro → persist). Comportamento garantido: último request vence |
| Acesso não autorizado na LAN | Token de 16 bytes embutido na URL do QR; validado em todos os endpoints `/api/*` e `/ws`. Comparação constant-time |
| Loop de erro enchendo disco com logs | Rolling por tamanho (10MB/arquivo) + retenção 7 dias |
| Schema do `config.json` muda no v2 | `schemaVersion: 1` no config; app roda migrações idempotentes ao detectar versão antiga; recusa carregar config mais novo que o app |
| Posições duplicadas no config (edição manual ou bug) | Repair no boot: primeira entry vence, demais ficam `position: null`; log de aviso |
| Race entre fim natural do som e novo `Play()` | Token monotônico por play; handler de `PlaybackStopped` posta comando que valida `token == _currentPlayToken` antes de zerar estado |
| `COMException` esporádico no `WasapiOut` | Thread STA dedicada de áudio: única responsável por criar/operar/dispor qualquer `WasapiOut`. Comandos da API são posts não-bloqueantes na fila |
| `nowPlaying` mente após fim do som | Engine subscribe a `PlaybackStopped` do player do game; ao disparar (e validar token), zera `nowPlaying`, dispose dos players, emite `stopped` no WS |
| Monitor toca duas vezes no VoiceMeeter (default do sistema = VoiceMeeter) | `monitorDevice` é campo separado, default `null` (= desabilitado até escolha explícita). UI lista candidatos WASAPI |
| Detecção de IP da LAN escolhe adapter virtual (VirtualBox/WSL/Tailscale) | Prioriza Ethernet/Wireless + gateway default + blocklist de nomes; persiste escolha como `preferredNetworkAdapter`; tray permite override manual |
| Slider de volume jittery por eco do WebSocket | `originId` no header/evento; cliente ignora evento que ele mesmo originou |
| Erro genérico em upload > 25 MB | Limites do Kestrel/FormOptions/RequestSizeLimit alinhados com a constante única do app |
| `id` e `file` desencontrados após sufixo de colisão | `id` é derivado do filename **final** sufixado, não do original |

## Fora de escopo (decisões explícitas para não fazer no v1)

- Volume por som.
- Hotkeys globais no PC.
- Categorias / abas / múltiplas páginas.
- Edição de áudio.
- Multi-usuário / contas (só um token compartilhado).
- HTTPS / certificados (HTTP simples na LAN; usuário responsável por não expor à internet).
- Instalador MSI.
- Suporte a outras plataformas (Linux/macOS).
- Sincronização de configs entre múltiplos PCs.
- Polifonia (mais de um som simultâneo).
- Tema dark/light (vai ser dark e ponto).
