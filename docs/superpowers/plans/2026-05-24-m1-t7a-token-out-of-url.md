# M1 / T7a — Token Out of the URL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop exposing the LAN auth token as plaintext on the connection screen and in connection URLs — mask it in the desktop display, and move it off the WebSocket URL query string into the WebSocket subprotocol and the REST header.

**Architecture:** The token is a 128-bit LAN credential (`SoundConfig.AuthToken`). Today it appears (a) as visible text on the desktop connect screen, (b) in the `/?t=` bootstrap URL the QR encodes, and (c) in the `/ws?t=` WebSocket URL of every client. This plan masks the *display* (user's decision) and moves the *WS transport* to the `Sec-WebSocket-Protocol` subprotocol, while the server keeps accepting the old `?t=` query as a deprecated fallback so no client gets locked out during the desktop↔mobile auto-update skew. REST already uses the `X-Auth-Token` header on every client, so the unused `?t=` REST fallback is removed for hardening.

**Tech Stack:** .NET 8 (`Soundpad.*`, brand "Reson"), ASP.NET Core minimal API + middleware + raw WebSockets (`StateHub`), WPF (code-behind) + WinForms (`QrWindow`), vanilla JS (`wwwroot/app.js`), Flutter (`mobile/reson_app`). xUnit + `WebApplicationFactory<Program>` for server tests.

**Spec:** `docs/superpowers/specs/2026-05-23-reson-roadmap-design.md` → Milestone 1, gap **T7a** (token out of URL). T7b (revocable/rotating tokens) is a separate, later plan.

**Sequence:** T7a is independent of T6/F1 (already shipped). It can land any time in M1. Within this plan: do the display mask first (smallest, self-contained), then the server WS-subprotocol acceptance (back-compat-safe on its own), then the two clients, then the REST hardening.

---

## Current state (verified — read before implementing)

- **REST auth:** `src/Soundpad/Security/AuthTokenMiddleware.cs` requires auth for `/api/*` and `/ws`. It reads the token as `Headers["X-Auth-Token"] ?? Query["t"]` and compares with constant-time `TokensMatch`. Static files (`/`, `/app.js`, images) are served by `UseStaticFiles()` *before* the auth middleware, so they are never auth-checked.
- **WS auth:** `Program.cs` maps `app.Map("/ws", ...) → StateHub.AcceptAsync(ctx)`. The auth middleware runs *before* the WS upgrade, so the `/ws?t=<token>` query is what authenticates the handshake today. `StateHub.AcceptAsync` calls `ctx.WebSockets.AcceptWebSocketAsync()` with **no** subprotocol.
- **Web client:** `app.js:2-5` reads `?t=` from `location.search`, stores it in `localStorage['soundpad.token']`, then uses the `X-Auth-Token` header for REST (`app.js:18`) and `/ws?t=${tok}` for the WebSocket (`app.js:361`).
- **Mobile client:** `api_client.dart` sends `X-Auth-Token` header for REST; `ws_client.dart:57-64` connects to `/ws?t=$token`. The QR scanner `qr_scanner_screen.dart:_parseQr` reads the token from `uri.queryParameters['t']`. Token stored in `SharedPreferences` (`config_store.dart`).
- **Desktop display:** `ConnectionPopover.cs:187,196` builds `http://{ip}:{port}/?t={token}` and shows it as **visible text** in `_urlText` *and* encodes it in the QR. `Tray/QrWindow.cs:24` shows the same URL as a **visible label** under its QR. `WpfTrayIcon.cs:49` copies the full URL to the clipboard (not a photo surface — left unchanged).

---

## Design decisions (locked)

- **Display = mask (user's decision, 2026-05-24).** On the desktop connect screen, show the URL with the token **masked** (`http://192.168.1.101:8080/?t=60dd…1105` — first 4 + `…` + last 4 of the token). The **QR keeps the full token** (that's the scan path) and the **tray "copy" keeps the full URL** (clipboard, not a photo). Masking is applied to the *text* in `ConnectionPopover._urlText` and `QrWindow`'s label.
  - **Acknowledged residual (not solved here):** masking the *text* does not hide the *QR* — anyone who photographs the screen can still scan the QR for the full token. That's inherent to QR pairing (the QR must carry the secret, like WhatsApp Web). Eliminating the photographed-QR risk requires token rotation/expiry = **T7b**, out of scope. This plan reduces the casual "paste the URL into a chat" leak and the WS/REST URL leak; it does not make the QR safe to photograph.
- **WS transport = `Sec-WebSocket-Protocol` subprotocol.** Browsers can't set custom headers on a `WebSocket`, but they can offer subprotocols. Clients offer two: a fixed marker `reson.auth.v1` and the token value. The server validates the non-marker entry as the token and, on accept, **echoes the marker** (`reson.auth.v1`) — never the token (don't reflect the secret). The auth token is lowercase hex, which is a valid subprotocol token per RFC 6455, so no encoding is needed.
- **Back-compat for auto-update skew (critical).** The desktop *is* the server and the phone updates independently, so a new client must not break against an old server and vice-versa. Therefore: the **server accepts BOTH** the new subprotocol AND the old `?t=` query for `/ws` (query is a deprecated fallback). New clients prefer the subprotocol. An old client (query) keeps working against the new server; a new client (subprotocol) keeps working against the new server. The only unsupported combo is new-client + old-server, which the user avoids by updating the desktop (server) — but to be safe, **new clients also keep sending `?t=` on the WS URL for this one transition release** (belt-and-suspenders), with a `TODO(T7a-cleanup)` to drop it a version later. The WS URL query is not the photographed surface, so the transitional dual-send is acceptable.
- **REST `?t=` query fallback removed.** Both web and mobile use the `X-Auth-Token` header for REST; nothing uses the `?t=` query for `/api/*`. Removing it is pure hardening with zero client impact. (The `/ws` query fallback is kept per the skew rule above.)
- **`AcceptWebSocketAsync` must echo the offered marker or browsers fail the handshake.** If the client offers subprotocols and the server accepts without selecting one, Chrome/Firefox treat the handshake as failed. So `StateHub.AcceptAsync` must conditionally echo `reson.auth.v1` when (and only when) the client offered it.

### OPEN DECISION (resolve during plan review) — bootstrap `?t=` → `#t=`

The biggest *URL* surface is the **bootstrap** `http://ip:port/?t=token` — it's what the QR encodes and what lands in the browser address bar and **history**. Moving it from the query (`?t=`) to the URL **fragment** (`#t=`) keeps the token out of the server request line and out of `Referer`, because fragments are never sent to the server; `app.js` reads `location.hash`, stores it, and strips it via `history.replaceState`. This is **Task 6 below**, and it is **RECOMMENDED but optional** because it changes the QR contract (the WPF QR builder, `QrWindow`, the web hash-read, AND the mobile QR scanner must all change together — back-compat-safe since clients read both `#t=` and `?t=`).
- **If you want T7a to actually get the token out of the address bar/history, include Task 6.**
- **If you want to keep this plan to "mask + WS subprotocol" (your original framing), skip Task 6** — the token then still appears in the bootstrap URL/history (but masked on the connect screen and absent from the WS URL).
Decide before execution; the controller will include or drop Task 6 accordingly.

---

## File structure

- `src/Soundpad/Security/TokenMask.cs` — **new.** Pure helper: `Mask(string token)` and `MaskUrl(string url)`. Unit-tested.
- `src/Soundpad/Wpf/ConnectionPopover.cs` — **modify** `RefreshQr`: QR gets the full URL, `_urlText` gets the masked URL.
- `src/Soundpad/Tray/QrWindow.cs` — **modify**: label shows the masked URL; QR keeps the full URL.
- `src/Soundpad/Security/AuthTokenMiddleware.cs` — **modify**: `/ws` accepts token via subprotocol (or query fallback); `/api/*` accepts header only.
- `src/Soundpad/Api/StateHub.cs` — **modify** `AcceptAsync`: echo `reson.auth.v1` when offered.
- `src/Soundpad/wwwroot/app.js` — **modify** `connectWs`: pass the token as a subprotocol (+ transitional query).
- `mobile/reson_app/lib/api/ws_client.dart` — **modify**: pass the token as a subprotocol (+ transitional query).
- Tests: `tests/Soundpad.Tests/Security/TokenMaskTests.cs` (new), `tests/Soundpad.Tests/Security/WsSubprotocolAuthTests.cs` (new), and updates to `tests/Soundpad.Tests/Security/AuthTokenTests.cs` (REST query removal).
- **(Task 6, optional)** `ConnectionPopover.cs`, `QrWindow.cs`, `WpfTrayIcon.cs` (build `#t=`), `app.js` (read `location.hash`), `mobile/.../qr_scanner_screen.dart` (parse `uri.fragment`).

---

## Task 1: Mask the token in the desktop display

**Files:**
- Create: `src/Soundpad/Security/TokenMask.cs`
- Test: `tests/Soundpad.Tests/Security/TokenMaskTests.cs` (new)
- Modify: `src/Soundpad/Wpf/ConnectionPopover.cs`, `src/Soundpad/Tray/QrWindow.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Soundpad.Tests/Security/TokenMaskTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Security;

namespace Soundpad.Tests.Security;

public class TokenMaskTests
{
    [Fact]
    public void Mask_Long_Token_Shows_Only_First_And_Last_Four()
    {
        TokenMask.Mask("60dd78bd3517d510cb4faaf470cf1105").Should().Be("60dd…1105");
    }

    [Fact]
    public void Mask_Short_Token_Is_Fully_Hidden()
    {
        // A token too short to safely reveal 4+4 is fully masked (no partial leak).
        TokenMask.Mask("abcdef").Should().Be("…");
        TokenMask.Mask("").Should().Be("…");
    }

    [Fact]
    public void MaskUrl_Masks_The_t_Query_Value_Only()
    {
        TokenMask.MaskUrl("http://192.168.1.101:8080/?t=60dd78bd3517d510cb4faaf470cf1105")
            .Should().Be("http://192.168.1.101:8080/?t=60dd…1105");
    }

    [Fact]
    public void MaskUrl_Without_Token_Is_Unchanged()
    {
        TokenMask.MaskUrl("http://192.168.1.101:8080/").Should().Be("http://192.168.1.101:8080/");
    }
}
```

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter TokenMaskTests`
Expected: FAIL — `TokenMask` doesn't exist.

- [ ] **Step 3: Implement `TokenMask`**

Create `src/Soundpad/Security/TokenMask.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Soundpad.Security;

/// <summary>
/// Renders an auth token (or a token-bearing URL) for safe on-screen display:
/// shows only the first and last 4 characters so the user can sanity-check
/// which token it is, without the full secret being readable in a screenshot.
/// The QR code and clipboard still carry the FULL token — this is display-only.
/// </summary>
public static class TokenMask
{
    /// <summary>first4…last4, or just "…" when too short to reveal safely.</summary>
    public static string Mask(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 12) return "…";
        return $"{token[..4]}…{token[^4..]}";
    }

    /// <summary>Mask the value of the <c>t</c> query parameter inside a URL string.</summary>
    public static string MaskUrl(string url)
    {
        return Regex.Replace(url, @"([?&]t=)([^&#]+)", m => m.Groups[1].Value + Mask(m.Groups[2].Value));
    }
}
```

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter TokenMaskTests`
Expected: 4 passing.

- [ ] **Step 5: Apply the mask to the desktop display**

In `src/Soundpad/Wpf/ConnectionPopover.cs`, in `RefreshQr` (~line 187), the QR keeps the full URL; only the text label is masked:

```csharp
var url = $"http://{_activeIp}:{_port}/?t={_library.Config.AuthToken}";
try
{
    _qrImage.Source = QrRenderer.RenderBitmap(url); // QR still carries the FULL token
}
catch { /* leave previous image */ }
_urlText.Text = Soundpad.Security.TokenMask.MaskUrl(url); // display masked
```

In `src/Soundpad/Tray/QrWindow.cs` (~line 24), the label is masked but the QR (`RenderPng(url)`) keeps the full URL:

```csharp
var lbl = new Label { Text = Soundpad.Security.TokenMask.MaskUrl(url), /* ...existing props... */ };
```

(Leave `WpfTrayIcon.cs:49` — the clipboard copy — using the full `url`; clipboard is not a screenshot surface.)

- [ ] **Step 6: Build + commit**

Run: `dotnet build src/Soundpad` (clean). Manual: open the connect popover — the URL text shows `…`-masked token; the QR still scans/pairs a phone.

```bash
git add src/Soundpad/Security/TokenMask.cs tests/Soundpad.Tests/Security/TokenMaskTests.cs src/Soundpad/Wpf/ConnectionPopover.cs src/Soundpad/Tray/QrWindow.cs
git commit -m "feat(security): mask auth token in desktop connect display (T7a)"
```

---

## Task 2: Server — accept the WS token via subprotocol

**Files:**
- Modify: `src/Soundpad/Security/AuthTokenMiddleware.cs`
- Modify: `src/Soundpad/Api/StateHub.cs`
- Test: `tests/Soundpad.Tests/Security/WsSubprotocolAuthTests.cs` (new)

**Context:** The middleware authenticates `/ws` before the upgrade. We add subprotocol parsing while keeping the `?t=` query fallback (skew safety). `StateHub.AcceptAsync` must echo the `reson.auth.v1` marker when offered, or browsers fail the handshake.

- [ ] **Step 1: Write the failing test**

Create `tests/Soundpad.Tests/Security/WsSubprotocolAuthTests.cs`. Uses the existing `TestingWebApplicationFactory` and its `Server.CreateWebSocketClient()`.

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Security;

public class WsSubprotocolAuthTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public WsSubprotocolAuthTests(TestingWebApplicationFactory f) { _factory = f; }

    private string Token => _factory.Services.GetRequiredService<SoundLibrary>().Config.AuthToken;

    [Fact]
    public async Task Ws_Connects_With_Token_In_Subprotocol_No_Query()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["Sec-WebSocket-Protocol"] = $"reson.auth.v1, {Token}";
        };
        // No ?t= in the URL — auth must come from the subprotocol.
        var ws = await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), default);
        ws.SubProtocol.Should().Be("reson.auth.v1"); // server echoed the marker, not the token
        ws.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task Ws_Rejects_Bad_Token_In_Subprotocol()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Sec-WebSocket-Protocol"] = "reson.auth.v1, deadbeef";
        var act = async () => await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), default);
        await act.Should().ThrowAsync<Exception>(); // 401 → handshake fails
    }

    [Fact]
    public async Task Ws_Still_Accepts_Legacy_Query_Token()
    {
        // Back-compat: an old client that puts ?t= on the WS URL must still connect.
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, $"ws?t={Token}"), default);
        ws.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }
}
```

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter WsSubprotocolAuthTests`
Expected: FAIL — `Ws_Connects_With_Token_In_Subprotocol_No_Query` fails (middleware ignores the subprotocol; with no `?t=` it returns 401), and the echo assertion fails (server selects no subprotocol).

- [ ] **Step 3: Parse the subprotocol token in the middleware**

In `src/Soundpad/Security/AuthTokenMiddleware.cs`, define the marker and extend the token lookup. Current logic reads `Headers["X-Auth-Token"] ?? Query["t"]`. Change the token resolution so that, for a WebSocket request, the token may also come from the offered subprotocols:

```csharp
public const string WsAuthMarker = "reson.auth.v1";

// ...inside Invoke, where `supplied` is computed, replace it with:
string? supplied = ctx.Request.Headers["X-Auth-Token"].FirstOrDefault();
if (supplied is null && ctx.WebSockets.IsWebSocketRequest)
{
    // Token offered as the non-marker subprotocol entry: "reson.auth.v1, <token>".
    supplied = ctx.WebSockets.WebSocketRequestedProtocols
        .FirstOrDefault(p => !string.Equals(p, WsAuthMarker, StringComparison.Ordinal));
}
supplied ??= ctx.Request.Query["t"].FirstOrDefault(); // deprecated fallback (skew safety; /ws + legacy)
```

Keep the existing `needsAuth` (still covers `/api/*` and `/ws`) and the `TokensMatch` comparison unchanged.

- [ ] **Step 4: Echo the marker on accept**

In `src/Soundpad/Api/StateHub.cs`, change `AcceptAsync` so it selects the marker subprotocol **only when the client offered it** (otherwise legacy/no-subprotocol clients would break):

```csharp
public async Task AcceptAsync(HttpContext ctx)
{
    // Echo the auth marker subprotocol when the client offered it (browsers fail
    // the handshake if they offer subprotocols and the server selects none). The
    // token itself is never echoed.
    var ws = ctx.WebSockets.WebSocketRequestedProtocols.Contains(AuthTokenMiddleware.WsAuthMarker)
        ? await ctx.WebSockets.AcceptWebSocketAsync(AuthTokenMiddleware.WsAuthMarker)
        : await ctx.WebSockets.AcceptWebSocketAsync();
    // ...rest of the method unchanged (id, sendLock, receive loop, finally)...
}
```

(Add `using Soundpad.Security;` to `StateHub.cs` if not present.)

- [ ] **Step 5: Run, verify passes**

Run: `dotnet test --filter WsSubprotocolAuthTests`
Expected: 3 passing.

- [ ] **Step 6: Run the security + full suite for regressions**

Run: `dotnet test --filter "FullyQualifiedName~Security"` then the deterministic gate `dotnet test --filter "FullyQualifiedName!~Velopack"`.
Expected: all green. (Bare `dotnet test` is flaky on the 4 pre-existing `VelopackUpdaterTests` under parallel collection ordering — judge by the Velopack-excluded gate.)

- [ ] **Step 7: Commit**

```bash
git add src/Soundpad/Security/AuthTokenMiddleware.cs src/Soundpad/Api/StateHub.cs tests/Soundpad.Tests/Security/WsSubprotocolAuthTests.cs
git commit -m "feat(security): accept WS auth token via subprotocol, echo marker (T7a)"
```

---

## Task 3: Web client — send the WS token via subprotocol

**Files:**
- Modify: `src/Soundpad/wwwroot/app.js`

- [ ] **Step 1: Change `connectWs` to offer the subprotocol**

In `src/Soundpad/wwwroot/app.js`, `connectWs` (~line 359-361), pass the marker + token as subprotocols. Keep the `?t=` query for this transition release (back-compat against an old server) with a cleanup TODO:

```javascript
  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    // Token travels in the WS subprotocol (out of the URL). The marker lets the
    // server know which offered protocol to echo; the token is the second entry.
    // TODO(T7a-cleanup): drop the ?t= query a release after all servers updated.
    const ws = new WebSocket(`${proto}://${location.host}/ws?t=${tok}`, ['reson.auth.v1', tok]);
```

(Everything else in `connectWs` — `ws.onmessage`, the echo filter, reconnect — stays the same.)

- [ ] **Step 2: Verify**

If `node` is available: `node --check src/Soundpad/wwwroot/app.js`.
Run: `dotnet build src/Soundpad` (bundles wwwroot). Manual: load the phone web UI, confirm it connects (WS open, live volume/monitor/normalize sync still works). In devtools the WS request shows `Sec-WebSocket-Protocol: reson.auth.v1, <token>` and the response selects `reson.auth.v1`.

- [ ] **Step 3: Commit**

```bash
git add src/Soundpad/wwwroot/app.js
git commit -m "feat(web): send WS auth token via subprotocol (T7a)"
```

---

## Task 4: Mobile client — send the WS token via subprotocol

**Files:**
- Modify: `mobile/reson_app/lib/api/ws_client.dart`

- [ ] **Step 1: Pass the token as a subprotocol**

In `mobile/reson_app/lib/api/ws_client.dart` (~line 57-64), add `protocols:` to the `IOWebSocketChannel.connect` call. Keep the `?t=` query for this transition release:

```dart
final uri = Uri.parse('$wsBase/ws?t=$token'); // TODO(T7a-cleanup): drop ?t= a release later
_channel = IOWebSocketChannel.connect(
  uri,
  protocols: ['reson.auth.v1', token],
  // ...existing named args (pingInterval, etc.) unchanged...
);
```

(If the existing call already passes other named args, add `protocols:` alongside them; don't drop any.)

- [ ] **Step 2: Verify compiles**

Run: `cd mobile/reson_app && flutter analyze`
Expected: 0 errors (pre-existing warnings/infos in untouched files are fine).

- [ ] **Step 3: Commit**

```bash
git add mobile/reson_app/lib/api/ws_client.dart
git commit -m "feat(mobile): send WS auth token via subprotocol (T7a)"
```

---

## Task 5: REST — drop the `?t=` query fallback (header-only hardening)

**Files:**
- Modify: `src/Soundpad/Security/AuthTokenMiddleware.cs`
- Modify: `tests/Soundpad.Tests/Security/AuthTokenTests.cs`

**Context:** No client uses `?t=` for `/api/*` (web + mobile both send `X-Auth-Token`). Removing the REST query fallback closes the door on token-in-URL for REST. The `/ws` query fallback stays (Task 2, skew safety).

- [ ] **Step 1: Write/adjust the failing test**

In `tests/Soundpad.Tests/Security/AuthTokenTests.cs`, add a test pinning that `?t=` is rejected for `/api/*` (and confirm an existing test still covers header success — add one if absent):

```csharp
[Fact]
public async Task Api_Rejects_Token_In_Query_String()
{
    var c = _factory.CreateClient();
    var lib = _factory.Services.GetRequiredService<SoundLibrary>();
    var r = await c.GetAsync($"/api/state?t={lib.Config.AuthToken}"); // query, no header
    r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
}

[Fact]
public async Task Api_Accepts_Token_In_Header()
{
    var c = _factory.CreateClient();
    var lib = _factory.Services.GetRequiredService<SoundLibrary>();
    c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
    (await c.GetAsync("/api/state")).StatusCode.Should().Be(HttpStatusCode.OK);
}
```

(If `AuthTokenTests` previously asserted `?t=` WORKS for `/api/*`, that assertion is now obsolete — update it to expect 401. Search the file for `?t=` / `Query` and fix.)

- [ ] **Step 2: Run, verify the query test fails**

Run: `dotnet test --filter AuthTokenTests`
Expected: `Api_Rejects_Token_In_Query_String` FAILS (today the query is accepted → 200, not 401).

- [ ] **Step 3: Restrict the query fallback to `/ws`**

In `AuthTokenMiddleware.cs`, gate the query fallback so it only applies to the WebSocket path (skew safety), not REST:

```csharp
supplied ??= ctx.WebSockets.IsWebSocketRequest ? ctx.Request.Query["t"].FirstOrDefault() : null;
```

(Replace the unconditional `supplied ??= ctx.Request.Query["t"]...` from Task 2 Step 3 with this `/ws`-only form. The header path and the subprotocol path are unchanged.)

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter AuthTokenTests` then `dotnet test --filter WsSubprotocolAuthTests`
Expected: all green — REST query rejected, header accepted, WS query still accepted (legacy), WS subprotocol accepted.

- [ ] **Step 5: Full regression gate + commit**

Run: `dotnet test --filter "FullyQualifiedName!~Velopack"` → green.

```bash
git add src/Soundpad/Security/AuthTokenMiddleware.cs tests/Soundpad.Tests/Security/AuthTokenTests.cs
git commit -m "feat(security): reject token in REST query string, header-only (T7a)"
```

---

## Task 6 (RECOMMENDED — include only if approved in plan review): bootstrap `?t=` → `#t=` fragment

**Why optional:** see the "OPEN DECISION" in Design decisions. This is what actually removes the token from the browser address bar / history / `Referer`. It is back-compat-safe because every reader accepts both `#t=` and `?t=`. It changes the QR contract, so all producers/consumers move together.

**Files:**
- Modify (producers → emit `#t=`): `src/Soundpad/Wpf/ConnectionPopover.cs`, `src/Soundpad/Tray/QrWindow.cs` (via the caller), `src/Soundpad/Wpf/WpfTrayIcon.cs`, and the startup print in `src/Soundpad/Program.cs`.
- Modify (web reader): `src/Soundpad/wwwroot/app.js`.
- Modify (mobile reader): `mobile/reson_app/lib/pairing/qr_scanner_screen.dart`.

- [ ] **Step 1: Web reader accepts the fragment first, then query, then storage; strips the hash**

In `app.js`, replace the bootstrap (lines 2-5):

```javascript
  // Token may arrive in the URL fragment (#t=, never sent to the server — preferred)
  // or the legacy query (?t=). Persist it, then strip it from the address bar/history.
  const hashTok = new URLSearchParams(location.hash.replace(/^#/, '')).get('t');
  const queryTok = new URLSearchParams(location.search).get('t');
  const incoming = hashTok || queryTok;
  if (incoming) {
    localStorage.setItem('soundpad.token', incoming);
    history.replaceState(null, '', location.pathname); // drop #t=/?t= from the URL
  }
  const tok = localStorage.getItem('soundpad.token') || '';
```

- [ ] **Step 2: Mobile QR parser reads the fragment too**

In `qr_scanner_screen.dart` `_parseQr` (~line 72), accept the token from the fragment as well as the query:

```dart
final frag = Uri.splitQueryString(uri.fragment);
final token = uri.queryParameters['t'] ?? uri.queryParameters['T'] ?? frag['t'] ?? frag['T'];
```

- [ ] **Step 3: Producers emit `#t=`**

Change the URL builders from `/?t={token}` to `/#t={token}` in `ConnectionPopover.cs:RefreshQr`, the `url` passed to `QrWindow`/clipboard in `WpfTrayIcon.cs`, and the startup print in `Program.cs`. The QR still encodes the full token (now in the fragment); `TokenMask.MaskUrl` already matches `[?&]t=` — **extend its regex to also match `#t=`**:

```csharp
public static string MaskUrl(string url)
    => Regex.Replace(url, @"([?&#]t=)([^&#]+)", m => m.Groups[1].Value + Mask(m.Groups[2].Value));
```

Add a `TokenMaskTests` case: `MaskUrl("http://h/#t=60dd78bd3517d510cb4faaf470cf1105")` → `"http://h/#t=60dd…1105"`.

- [ ] **Step 4: Verify end-to-end**

`dotnet build src/Soundpad` (clean); `dotnet test --filter TokenMaskTests` (green); `node --check app.js`; `cd mobile/reson_app && flutter analyze` (0 errors). Manual: scan the new QR from the phone (fragment token parsed), and open the web URL — address bar shows no token after load, WS/REST still authenticate.

- [ ] **Step 5: Commit**

```bash
git add src/Soundpad/Wpf/ConnectionPopover.cs src/Soundpad/Tray/QrWindow.cs src/Soundpad/Wpf/WpfTrayIcon.cs src/Soundpad/Program.cs src/Soundpad/Security/TokenMask.cs src/Soundpad/wwwroot/app.js mobile/reson_app/lib/pairing/qr_scanner_screen.dart tests/Soundpad.Tests/Security/TokenMaskTests.cs
git commit -m "feat(security): carry pairing token in URL fragment, not query (T7a)"
```

---

## Self-review checklist (run after implementing)

- **Spec coverage:** T7a = "token out of URL." Display masked (Task 1); WS token off the URL into the subprotocol (Tasks 2-4); REST token off the URL (Task 5); bootstrap URL off the query into the fragment (Task 6, if approved). ✓
- **No lockout:** server accepts subprotocol AND legacy `/ws?t=` query (Task 2); new clients dual-send during transition (Tasks 3-4); REST query removal affects no client (Task 5). New-client+old-server is covered by the transitional query dual-send. ✓
- **Secret never reflected:** server echoes the `reson.auth.v1` marker, never the token (Task 2 Step 4). ✓
- **Residual documented:** QR still carries the full token; photographing the QR still leaks it — true fix is T7b rotation (out of scope). ✓
- **Type/string consistency:** marker string `reson.auth.v1` identical in `AuthTokenMiddleware.WsAuthMarker`, `StateHub.AcceptAsync`, `app.js`, and `ws_client.dart`. Token mask format `first4…last4` identical across `TokenMask` and the tests.

## Notes for the implementer

- **Test the WS handshake with the real factory.** `WebApplicationFactory<Program>` + `Server.CreateWebSocketClient()` exercises the actual middleware + `StateHub` (no mocking the protocol). `ConfigureRequest` sets the `Sec-WebSocket-Protocol` header. This is also why the suite's Velopack tests pass when run in a full suite (the factory boots `Program`/`VelopackApp.Build`).
- **Browsers fail silently on subprotocol mismatch.** If you change the marker on one side only, the WS just won't open and there's no clean error — keep `reson.auth.v1` byte-identical on client and server.
- **T7b is the follow-up.** Revocation/rotation (and thus making a photographed QR expire) is a separate plan; don't add it here.
