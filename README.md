# Reson

Reson para Windows, controlado pelo celular via Wi-Fi. Toca sons dentro do Valorant, Discord, TeamSpeak — qualquer app que enxergue um microfone.

> Histórico: o projeto começou com o nome interno "Soundpad" e mantém esse nome no `Soundpad.csproj` / `namespace Soundpad.*` para evitar churn de refatoração. O binário publicado, instalador, ícones e UI são todos "Reson".

## Pré-requisitos

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download) (não precisa se usar o build self-contained)
- [VoiceMeeter](https://vb-audio.com/Voicemeeter/) (Standard, Banana ou Potato)
- PC e celular na mesma Wi-Fi

## Setup VoiceMeeter

1. Instalar VoiceMeeter Banana e reiniciar Windows.
2. No painel do VoiceMeeter, em HARDWARE OUT (A1), selecionar seu fone.
3. No Windows, em **Configurações de som > Dispositivos de entrada**, marcar "VoiceMeeter Output" como microfone padrão **só** para os apps que vão receber o som (ou configurar no app: Discord → microfone = VoiceMeeter Output).
4. Manter o default de **saída de som do Windows** apontando pro seu fone (NÃO pro VoiceMeeter), pra evitar que o monitor toque duas vezes pro jogo.

## Run

```bash
dotnet run --project src/Soundpad
```

Ou build self-contained:

```bash
dotnet publish src/Soundpad -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

`Reson.exe` vai aparecer em `src/Soundpad/bin/Release/net8.0-windows/win-x64/publish/`.

## Uso

1. Rodar `Reson.exe`. Tray icon aparece.
2. Direito no tray → "Mostrar QR code".
3. Escanear no celular → abre a grade no browser.
4. (No celular) configurar **Settings → Monitor device** = seu fone (se quiser ouvir).
5. Configurar VoiceMeeter como microfone no Discord/Valorant.
6. Tocar sons!

## Configuração

`config.json` em `%LOCALAPPDATA%\Reson\` é gerado no primeiro boot. Pode editar manualmente quando o app está parado.

Na primeira execução após o rebrand Soundpad→Reson, o app migra automaticamente o conteúdo de `%LOCALAPPDATA%\Soundpad\` para `%LOCALAPPDATA%\Reson\` (preserva config + sons + logs). A pasta antiga fica intacta; pode ser apagada manualmente depois que confirmar que tudo funciona.

Campos relevantes:
- `port`: padrão 8080 (auto-fallback se ocupado).
- `audioDevice`: nome do dispositivo de saída pro game (default = VoiceMeeter detectado).
- `monitorDevice`: dispositivo opcional pra você ouvir; null = monitor desligado.
- `latencyMs`: 50 default. Subir pra 100 se houver glitches.
- `authToken`: hex de 16 bytes, embutido na URL do QR.

## Logs

`%LOCALAPPDATA%\Reson\logs\soundpad-YYYY-MM-DD-N.log` — rolling diário + por tamanho (10MB) + retenção 7 dias.
