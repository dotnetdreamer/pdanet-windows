# PdaNet Browser — MCP server for AI agents

PdaNet Windows has a built‑in **Model Context Protocol (MCP) server** that lets an AI agent
drive the PdaNet browser. The agent gets a set of browser tools — open a page, see it, click,
type, read, screenshot — and uses them to do real work on the web, in a real, visible window.

It is not a separate program. It is the same `PdaNetPortable.exe`, started with `--mcp`:

```
PdaNetPortable.exe --mcp
```

The process speaks MCP over stdio (the transport every MCP client launches), opens a real
PdaNet browser window, and waits for a client to connect.

---

## Why this one is different

- **It drives the *real* PdaNet browser.** Not a throwaway headless instance — the same tabbed
  WebView2 browser a person uses, with the same sign‑ins and cookies (kept in PdaNet's own
  `data\browser` profile, apart from Edge and Chrome). Sign in once; the agent stays signed in.
- **It browses through your phone.** When PdaNet is connected, the browser's traffic follows the
  phone's connection — including any VPN on the phone. `browser_network` reports whether traffic
  is actually flowing **Through PdaNet**. An agent that browses from your phone's IP, in your
  phone's country, is a genuinely new capability.
- **You can watch it.** The window is visible by default, so you see every click and keystroke
  as the agent works. (Use `--hidden` for unattended runs.)
- **Accessibility‑style snapshot.** The agent "sees" the page as a compact tree of elements, each
  with a `[ref=eN]` handle — the same approach the best browser agents use. It acts on handles, not
  on guessed CSS selectors. The tree is derived from the DOM and descends into **open shadow roots**
  and **same‑origin iframes**; cross‑origin iframe contents are not reachable (browser security).
- **Handles can't go stale silently.** Each snapshot stamps a generation into its handles
  (`[ref=e3-12]`). A handle from an earlier snapshot is reported *stale* and refused, so the agent
  never acts on a handle that no longer means what it did when it was read.
- **Trusted input.** Clicks and keystrokes are dispatched through the Chrome DevTools Protocol, so
  they fire exactly like a person's input and work on demanding single‑page apps (React, etc.).
  Double‑click sends two real press/release cycles; in‑iframe clicks are offset to the right screen
  point.
- **Zero extra dependencies.** The server is hand‑rolled JSON‑RPC on `System.Text.Json`, so the
  portable single‑file exe stays lean. Nothing to install beyond the app and Microsoft Edge
  WebView2 (which ships with Windows 11).

---

## Requirements

- Windows 10/11 (64‑bit).
- Microsoft Edge **WebView2** runtime (built into Windows 11; the app links the free download if
  it is missing).
- `PdaNetPortable.exe` (from the Releases page, or `dotnet publish -c Release`).
- A desktop session (the browser window is a real window). For headless/unattended use pass
  `--hidden`.

Connecting PdaNet to your phone is **optional** — the browser works on whatever connection the PC
has. Connect PdaNet first if you want the agent to browse through the phone's internet/VPN.

---

## Add it to a client

### Claude Desktop

Edit `claude_desktop_config.json` (Settings → Developer → Edit Config) and add:

```json
{
  "mcpServers": {
    "pdanet-browser": {
      "command": "C:\\Tools\\PdaNet\\PdaNetPortable.exe",
      "args": ["--mcp"]
    }
  }
}
```

Restart Claude Desktop. The 20 `browser_*` tools appear in the tools menu.

### Claude Code

```
claude mcp add pdanet-browser -- "C:\Tools\PdaNet\PdaNetPortable.exe" --mcp
```

### Any MCP client

Launch `PdaNetPortable.exe --mcp` as a stdio server. It primarily implements the `initialize`
handshake (protocol `2025-06-18`, and also `2025-11-25`, `2025-03-26`, `2024-11-05`) with
`tools/list`, `tools/call` and `ping`, and additionally answers the `server/discover` RPC for
forward compatibility. It does not implement the full stateless per‑request `2026-07-28` mode.
Unknown methods return `-32601`, unknown tools `-32602`, and a non‑`"2.0"` `jsonrpc` field `-32600`.

Flags:

| Flag | Effect |
|------|--------|
| `--mcp` | Run the MCP server over stdio (visible browser window). |
| `--mcp --hidden` | Same, but keep the browser window off‑screen (unattended runs). |
| `--mcp-smoke` | Run the built‑in offline self‑test of the automation pipeline and exit (0 = pass). |
| `--mcp-smoke --show` | Same, but show the window so you can watch it. |

---

## How an agent uses it

The loop is: **open → snapshot → act → snapshot**.

1. `browser_navigate` to a URL. The reply already includes a snapshot.
2. Read the snapshot — an indented tree where each actionable element has a role, a name and a
   `[ref=eGEN-N]` handle (the `GEN` is the snapshot generation):

   ```
   Page: Hacker News
   URL: https://news.ycombinator.com/
   42 actionable element(s)

   - link "Hacker News" [ref=e1-1]
   - link "new" [ref=e1-2]
   - textbox "Search" [ref=e1-8]
   - button "Search" [ref=e1-9]
   ...
   ```
3. Act on a handle: `browser_click {ref:"e1-9"}`, `browser_type {ref:"e1-8", text:"mcp", submit:true}`.
   Most actions return a **fresh** snapshot (a new generation), so one call both acts and shows the
   result — use the handles from that newest snapshot for the next step.

Because handles carry the snapshot generation, reusing one from an earlier snapshot is reported as
stale and refused rather than acted on — so the agent never clicks a handle that has since moved.
Password fields are never revealed: the snapshot shows `(filled)`, never the value.

---

## The tools

| Tool | What it does |
|------|--------------|
| `browser_navigate` | Open a URL or search text; waits for load; returns a snapshot. `newTab` optional. |
| `browser_navigate_back` / `browser_navigate_forward` | History navigation. |
| `browser_reload` | Reload (optionally `hard` to bypass cache). |
| `browser_tabs` | `list` / `new` / `select` / `close` tabs (the active one is marked `*`). |
| `browser_snapshot` | The accessibility tree with `[ref=eN]` handles — how the agent "sees" the page. |
| `browser_read` | Readable page text as `text` or `markdown` — best for articles and results. |
| `browser_screenshot` | PNG of the viewport, or the whole page with `fullPage:true`. |
| `browser_click` | Click a handle. Supports `button` (left/right/middle) and `doubleClick`. |
| `browser_type` | Type into a handle, as real keystrokes. `clear` first, `submit` with Enter. |
| `browser_press_key` | Press one key (a character, or Enter/Tab/Escape/Arrow…/PageDown…). |
| `browser_hover` | Hover a handle (reveals menus/tooltips). |
| `browser_select_option` | Choose option(s) in a `<select>` by label or value. |
| `browser_scroll` | Scroll a direction, or a handle into view. |
| `browser_drag` | Drag one handle onto another (sliders, sortable lists, drop targets). |
| `browser_wait_for` | Wait until text appears / disappears, or a fixed time. |
| `browser_evaluate` | Run a JavaScript function body and get its JSON result. A handle is exposed as `window.__pdaEl`. |
| `browser_console` | Recent console messages and page errors/warnings. |
| `browser_network` | Recent network responses **and** the route, read from the Windows proxy PdaNet sets — so it's accurate even when the PdaNet app runs as a separate process. |
| `browser_handle_dialog` | Decide how JS dialogs (alert/confirm/prompt) are answered. |

---

## Safety & behaviour

- **JavaScript dialogs** (`alert`/`confirm`/`prompt`) can't hang the page: they are replaced with
  non‑blocking stubs that return immediately (and native dialogs are disabled as a backstop).
  `confirm`/`prompt` are **accepted** by default; switch to dismiss, or set prompt text, with
  `browser_handle_dialog`. The text of any dialog that fired is reported back on the next snapshot.
  A 90‑second per‑call timeout is a further backstop so no single tool call can wedge the server.
- **Passwords are never exposed** — a password field shows as `(filled)` in snapshots, never its
  value, and is never used as an element's name.
- **Permission requests** (geolocation, notifications, camera…) are **auto‑denied** so an
  automated run never blocks on a prompt.
- **`data:` and `javascript:` URLs are blocked from the `browser_navigate` tool**, so the agent
  can't navigate the tab into arbitrary script. (An in‑page `javascript:` link a page itself
  contains still runs when clicked, exactly as in any browser — and the agent can already run
  scripts deliberately via `browser_evaluate`.)
- **Popups become tabs** — a link that opens a new window opens a new tab the agent can see with
  `browser_tabs`.
- The agent uses your **real browser profile** (sign‑ins, cookies). Treat the MCP server like
  giving the agent your browser: only connect it to clients you trust. Clear data any time from
  the browser menu → *Clear browsing data*.
- Expected failures (no page open yet, a stale handle, a bad option) come back as normal tool
  results with `isError: true` and a plain explanation, so the model can recover — they are not
  protocol errors.

---

## How it works

```
MCP client ──stdio JSON‑RPC──► PdaNetPortable.exe --mcp
                                   │
                                   ├─ McpServer         initialize / tools.list / tools.call
                                   ├─ BrowserTools      the 20 browser_* tool schemas
                                   └─ BrowserSession ──► BrowserWindow (WebView2)
                                        • perception: injected snapshot.js → ref tree (shadow/iframe)
                                        • input:      Chrome DevTools Protocol (trusted mouse/keys)
                                        • capture:    CapturePreview + Page.captureScreenshot
                                        • route:      read from the Windows proxy PdaNet sets
```

The read loop runs on a background thread; every browser touch is marshalled onto the WPF UI
thread, so WebView2 stays happy and the window stays live. Source lives in
[`pdanet-windows/Mcp/`](../pdanet-windows/Mcp) and
[`pdanet-windows/Views/BrowserWindow.Automation.cs`](../pdanet-windows/Views/BrowserWindow.Automation.cs).

---

## Troubleshooting

- **"The browser engine (WebView2) is not available."** Install the Microsoft Edge WebView2
  runtime, then start the server again.
- **Client shows no tools.** Confirm the `command` path is correct and the exe runs:
  `PdaNetPortable.exe --mcp-smoke` should print `MCP SMOKE PASS`.
- **Nothing visible happens.** You may be running `--hidden`, or on a session with no desktop.
  Drop `--hidden` to watch the window.
- **A page says it's in the wrong country.** That is about the *connection*, not the browser —
  connect PdaNet in Phone VPN mode with a VPN on the phone. `browser_network` shows the route.
- **Logs.** The server never writes to stdout except protocol messages; human logs go to stderr
  and to `data\pdanet.log` next to the exe.
