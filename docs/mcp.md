# PdaNet Browser: MCP server for AI agents

PdaNet Windows has a built‑in **Model Context Protocol (MCP) server**. It lets an AI agent, such as
Claude Code, Claude Desktop, Cursor, VS Code or your own agent framework, use the PdaNet browser:
open websites, read them, fill in forms, click, upload, download, manage tabs and everything else
the browser offers, in a real window the person can watch, pause and stop.

```
PdaNetPortable.exe --mcp
```

---

## What sets it apart

- **It drives your real browser, live.** While PdaNet runs, `--mcp` connects to *that* PdaNet and
  drives its browser: the same window, the same tabs, your sign‑ins, and your phone's connection when
  PdaNet is connected. An agent can read the tabs you already have open ("summarize the tab I'm on").
- **Your tabs stay yours.** An agent opens pages in tabs of its own and never replaces a page you
  opened. Before it clicks, types, goes back or runs a script in a tab you opened, the browser asks
  you, once per tab; reading your tabs needs no question. If you switch tabs while it works, it stays
  in its own tab and brings it back to the front for its next action. Closing your tabs asks you too.
  Your own alerts and file choosers stay as they are.
- **The person stays in charge.** A bar at the top of the browser says which agent is using it and
  what it is doing right now ("Clicking button "Sign in""), with **Pause** and **Stop**. Pause holds
  an agent at once, even in the middle of typing. A new agent acts only once the person presses
  **Allow** (or **Always allow**). Clearing browsing data or the history, and letting websites with a
  safety warning open, need the person's approval every time. When an agent needs a sign‑in, a CAPTCHA
  or a two‑factor code, it hands the browser over with `browser_ask_human` and waits for **Done** (with
  an optional typed reply).
- **Everything the browser offers.** Tabs (new, select, close, duplicate, reopen, move, mute, close
  others, restore last session), web apps, history, find on page, zoom, full screen, print to PDF,
  downloads, search engine, website colors, safety warnings, clearing data, the window itself, and
  the route through the phone. Each is a tool.
- **Pages cannot reach the agent's helper.** The page outline is built in an *isolated JavaScript
  world* that shares the page's DOM but not its scripts, so a page cannot see the helper, change how
  it works or forge the handles it hands out. What a page shows is still the page's own: its text,
  labels and layout are untrusted, and the server's instructions tell agents so.
- **Handles cannot hit the wrong thing.** Each element the agent can act on gets a handle like
  `[ref=e12]`. A handle names *one element* for as long as it exists, and handle numbers are unique
  across tabs. A handle from another tab, from a page the tab has left, or from a frame that has gone
  to another page is refused with an explanation, never applied to whatever happens to be there now.
- **Built for agents.** Clicks and keys are trusted input (Chrome DevTools Protocol), so React and
  other web apps behave as for a person. Clicks scroll into view and refuse, with the reason, when an
  element is disabled, hidden or covered by something like a cookie banner, also when the cover lies
  over the frame the element sits in. JavaScript dialogs are
  held until the agent answers them. File choosers are caught, so uploads work. Popups become tabs the
  agent is told about. Actions wait until the network goes quiet.
- **Light on tokens.** After an action the agent gets what changed (navigation, new tabs, dialogs,
  downloads, console errors) and, when only part of the page changed, just the outline lines that
  changed since its last look, with their parent lines for context. When most of the page changed it
  gets the whole outline.
- **Every client, every protocol version.** stdio (what MCP apps start), Streamable HTTP for agent
  frameworks, and both protocol eras: 2024‑11‑05 to 2025‑11‑25 (`initialize`) and the stateless
  2026‑07‑28 revision (`server/discover`, per‑request `_meta`). Requests run concurrently with
  cancellation, progress, logging, resources and prompts.
- **Nothing extra to install.** The server is hand‑rolled on `System.Text.Json`, inside the same
  portable `PdaNetPortable.exe`. It needs the Microsoft Edge WebView2 runtime, which Windows 11 has.
- **Checked end to end.** `PdaNetPortable.exe --mcp-smoke` runs 150 checks offline against real pages:
  protocol edge cases, every kind of tool, the person's controls (their tabs, approvals, pause in the
  middle of typing, cancelled calls), the HTTP transport's safety checks, and the pipe with a real
  `--mcp` relay. While a PdaNet runs, the 4 relay checks are skipped, since that PdaNet holds the pipe.

---

## Connect an agent

The quickest way: open PdaNet, **Settings → AI agents**, and press **Copy for Claude Code**,
**Copy for Claude Desktop** or **Copy for VS Code**. The copied text already holds the full path of
your `PdaNetPortable.exe`. By hand:

### Claude Code

```
claude mcp add pdanet-browser -- "C:\Tools\PdaNet\PdaNetPortable.exe" --mcp
```

### Claude Desktop and Cursor

Claude Desktop: Settings → Developer → Edit Config (`claude_desktop_config.json`). Cursor:
`~/.cursor/mcp.json`. Add:

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

### VS Code

`.vscode/mcp.json` (or the user `mcp.json`):

```json
{
  "servers": {
    "pdanet-browser": {
      "type": "stdio",
      "command": "C:\\Tools\\PdaNet\\PdaNetPortable.exe",
      "args": ["--mcp"]
    }
  }
}
```

### Agent frameworks over HTTP

For agents that connect to an MCP server by address (LangGraph, the OpenAI Agents SDK, your own
code), turn on **Settings → AI agents → Also take agents over HTTP** and press Save. The server
listens on this PC only, at `http://127.0.0.1:47823/mcp` (the port is a setting), and every request
needs the key from **Copy key**:

```
claude mcp add --transport http pdanet-browser http://127.0.0.1:47823/mcp --header "Authorization: Bearer <key>"
```

```json
{
  "servers": {
    "pdanet-browser": {
      "type": "http",
      "url": "http://127.0.0.1:47823/mcp",
      "headers": { "Authorization": "Bearer <key>" }
    }
  }
}
```

**New key** locks out every agent that has the old one.

---

## How `--mcp` connects

```
                 ┌───────────────────────── PdaNet running ─────────────────────────┐
MCP client ─stdio─► PdaNetPortable.exe --mcp ──named pipe (this Windows user only)──► │
                 │                                                     MCP server ──► the PdaNet browser
agent framework ─────── HTTP 127.0.0.1:47823/mcp + key (when turned on) ──────────► │   (the window you use)
                 └──────────────────────────────────────────────────────────────────┘

                 ┌──────────── PdaNet not running, or --standalone / --hidden ────────────┐
MCP client ─stdio─► PdaNetPortable.exe --mcp: MCP server ──► a browser window of its own │
                 └──────────────────────────────────────────────────────────────────────────┘
```

- **PdaNet runs:** `--mcp` is a thin relay. It passes the client's messages to the running PdaNet
  over a named pipe that only this Windows user can open. The agent drives the person's own browser.
  The first time an agent acts, PdaNet asks the person in the browser.
- **PdaNet does not run:** `--mcp` is the server itself, with a PdaNet browser window of its own
  (same sign‑ins and settings). The client that started it is the person's choice, so it is allowed
  without asking. Closing that window ends the server.

| Flag | Effect |
|------|--------|
| `--mcp` | Relay to the running PdaNet, or run a browser of its own. |
| `--mcp --standalone` | Always a browser of its own, even when PdaNet runs. |
| `--mcp --hidden` | A browser of its own, off screen, for unattended runs. It leaves the person's saved tabs and window place alone. |
| `--mcp-smoke` | The built‑in end‑to‑end check, offline (exit code 0 = pass). Add `--show` to watch it. |

---

## The person's controls

| | |
|---|---|
| **Allow a new agent** | The first call of an agent the person has not allowed shows *"Claude Code wants to use the PdaNet browser"* with **Allow**, **Always allow** and **Deny**. The call waits up to two minutes for the answer. **Always allow** remembers the agent's name (Settings → AI agents → Forget clears the list). Agents are told apart by the name their app gives and how they connect, so Allow says *which* agent may work, it is not a password: a program that runs as you could give the same name. The pipe takes programs of your own Windows user only, and HTTP needs the key. A browser that `--mcp` opens itself, when PdaNet does not run, belongs to the agent that started it and asks nothing. |
| **See what it does** | While an agent works, the bar says *"Claude Code is using this browser · Typing into textbox "Email""* and the element it is about to touch is outlined on the page. |
| **Pause** | Agents wait until **Resume**, also a call that is busy: it stops between two keys, clicks or scroll steps, and types the rest into the same field afterwards. The person has the browser meanwhile. |
| **Stop** | Every agent's calls stop at once and their connections close. An agent must be allowed again to come back, even one you always allow. Also in the tray menu: **Stop AI agents**. |
| **Approve what is yours** | Each time: acting in a tab you opened (once per tab), closing your tabs, clearing browsing data or the history, forgetting a website, and letting websites with a safety warning open. |
| **Help the agent** | `browser_ask_human` shows the agent's message with **Done**, **I can't** and, when asked for, a reply box. |
| **Turn it off** | Settings → AI agents → *Let AI agents use the browser*. Agents then get a clear "access is turned off" answer, also a `--mcp` that runs a browser of its own. |

PdaNet writes what agents do to its activity log (pages opened, clicks, typing, with the website,
never the typed text).

---

## How an agent works with it

The loop is **open → read the outline → act → read what changed**.

1. `browser_navigate` opens a page and returns its outline:

   ```
   Page: Sign in · Example
   URL: https://example.com/login
   Tab [0] of 2 · 14 elements to act on · showing 0–900 of 2,400 px

   - banner [ref=e1]
     - link "Home" (/) [ref=e2]
   - main [ref=e3]
     - heading "Sign in" [level=1]
     - form [ref=e4]
       - textbox "Email" [required] [ref=e5]
       - textbox "Password" [ref=e6]
       - checkbox "Remember me" [unchecked] [ref=e7]
       - combobox "Language" value="English" options=[English|Deutsch|Français] [ref=e8]
       - button "Sign in" [ref=e9]
     - text "Forgot your password? We can help."
   ```

2. Act on handles: `browser_fill_form` with `[{ref:"e5", value:"ada@example.com"}, {ref:"e7", value:true}]`,
   then `browser_click {ref:"e9"}`.
3. Each action answers with what it did, what changed, and the outline (or only its changed lines):

   ```
   Clicked button "Sign in" (e9).
   → Navigated to https://example.com/inbox
   → 1 new console error (see browser_console_messages).

   Page: Inbox · Example
   …
   ```

Good to know:

- **Passwords** never come back: a password field shows `[filled]`, typed passwords are reported as
  "Typed 12 characters".
- **Dialogs** (`confirm`, `prompt`, leave‑page) hold the page until answered with
  `browser_handle_dialog`, and results say when one is open. Alerts are closed by themselves and reported.
- **Uploads:** click the page's upload button; the file chooser is caught and the result says so.
  Then `browser_file_upload {paths:[…]}`. Or pass the file field's ref directly.
- **New tabs** a link opens are followed, and the result says so.
- **Frames from other websites** (payment fields, embedded maps) show as `iframe … [cross-origin] [ref=…]`.
  Use `browser_take_screenshot` and `browser_mouse` there: screenshot pixels are CSS pixels, the same
  x,y `browser_mouse` takes.
- **Big pages:** `browser_snapshot {ref:"e3"}` outlines one region; `browser_read` gives the text in parts.
- **Time limits:** every tool has one (two minutes for most, longer for waiting on downloads or the
  person). A call that runs out is stopped, not left running, so a stuck page never blocks the next call.
- **Page text is untrusted.** The server's instructions tell agents never to follow instructions found on pages.

---

## Tools

| Group | Tool | What it does |
|---|---|---|
| Navigate | `browser_navigate` | Open an address, a web app by name, or a search; waits until usable; returns the outline. `newTab` optional. |
| | `browser_navigate_back`, `browser_navigate_forward` | History of the active tab. |
| | `browser_reload` | Reload (`hard` skips the cache) or `stop` loading. |
| | `browser_tabs` | `list`, `new` (at `url`), `select`, `close`, `duplicate`, `reopen`, `close_others`, `close_right`, `move`, `mute`, `unmute`, `restore` (the tabs from last time). |
| See | `browser_snapshot` | The outline with handles; `ref` for one region; `changesOnly` for a diff. |
| | `browser_read` | Readable text as markdown or text, in parts (`offset`), of any tab (`tab`). |
| | `browser_take_screenshot` | Visible part, `fullPage`, or one element (`ref`); PNG or JPEG. |
| Act | `browser_click` | Trusted click; `button`, `doubleClick`, `modifiers`, `force`. |
| | `browser_type` | Type as real text input; `clear`, `submit`, `slowly`. |
| | `browser_fill_form` | Many fields at once: text, dropdowns, checkboxes, radios, files. |
| | `browser_press_key` | A key or chord (`Control+A`, `Shift+Tab`), `repeat`. |
| | `browser_hover` | Point at an element (menus, tooltips). |
| | `browser_select_option` | Choose in a native dropdown by label or value. |
| | `browser_scroll` | Wheel scroll up/down/left/right, top/bottom, or a `ref` into view. |
| | `browser_drag` | Drag onto another element: pointer‑driven and HTML5 drag and drop. |
| | `browser_mouse` | Click, move, press, release, wheel or drag at x,y. |
| | `browser_file_upload` | Fill the caught file chooser, or a file field by `ref`. |
| Wait and script | `browser_wait_for` | Text appears or goes, the address matches, the network is quiet, or seconds. |
| | `browser_evaluate` | Run JavaScript (`() => …`, `(el) => …` with `ref`, a body, or an expression). |
| | `browser_handle_dialog` | Answer the open dialog: `accept`, `promptText`. |
| Inspect | `browser_console_messages` | Console, uncaught errors and network errors; filter by level. |
| | `browser_network_requests` | The route (through PdaNet and the phone or not) and recent requests. |
| Browser | `browser_find` | Find on page with the browser's find bar: count, next, previous, close. |
| | `browser_window` | Zoom, full screen, focus/minimize/maximize/restore, page area size (e.g. 390×844), developer tools. |
| | `browser_print` | Save as PDF (returns the path) or open the print dialog. |
| | `browser_downloads` | List, wait for, cancel, pause, resume; show or hide the panel. |
| | `browser_web_apps` | List, add, rename, remove, open the person's web apps. |
| | `browser_history` | List, search, forget a website, clear the list. |
| | `browser_settings` | Search engine, website colors, opening sites with safety warnings. |
| | `browser_clear_data` | Clear history, sign‑ins, cookies and website data, after the person approves. |
| The person | `browser_ask_human` | Hand over with a message and wait for Done, with an optional reply. |

The first version's names `browser_screenshot`, `browser_console` and `browser_network` still work.

Tools that report lists also return **structured content** matching their `outputSchema`
(`browser_tabs`, `browser_find`, `browser_window`, `browser_print`, `browser_downloads`,
`browser_web_apps`, `browser_history`, `browser_settings`, `browser_network_requests`,
`browser_ask_human`). Every tool carries the spec's annotations (`readOnlyHint`, `destructiveHint`,
`idempotentHint`, `openWorldHint`).

### Resources

| URI | |
|---|---|
| `browser://tabs` | Open tabs (JSON). |
| `browser://active/text` | Readable text of the active tab (markdown). |
| `browser://active/snapshot` | Outline of the active tab. |
| `browser://tab/{index}/text`, `browser://tab/{index}/snapshot` | The same for any tab (templates). |
| `browser://web-apps`, `browser://history`, `browser://downloads`, `browser://status` | JSON. |

In Claude Code, mention them in a prompt, for example `@pdanet-browser:browser://active/text`.
Sessions of the 2025 protocol revisions can subscribe to them.

### Prompts

`research` (topic), `summarize_tab` (tab), `check_route`.

---

## Protocol details

- **Versions:** `initialize` negotiates 2025‑11‑25, 2025‑06‑18, 2025‑03‑26 or 2024‑11‑05. Requests that
  carry `io.modelcontextprotocol/protocolVersion` 2026‑07‑28 in `_meta` are handled statelessly and
  need `io.modelcontextprotocol/clientCapabilities` (else `-32602`); an unknown version gets `-32022`
  with the supported list. `server/discover` answers with every supported version. Results of
  2026‑07‑28 requests carry `resultType: "complete"`.
- **Concurrency:** messages run concurrently, so `ping` and `notifications/cancelled` are answered
  while a long call waits. Browser actions run one at a time, in order. A cancelled request gets no
  response, as JSON‑RPC asks.
- **Progress:** long calls (loading, waiting, a person's answer, downloads) send
  `notifications/progress` when the request has a `progressToken`.
- **Logging:** `logging/setLevel`, then `notifications/message`.
- **Errors:** unknown tool `-32602`, unknown method `-32601`, unknown resource `-32002`. Expected tool
  failures (a gone element, a covered button, a declined approval) are normal results with
  `isError: true` and a plain explanation the model can act on.

### The HTTP endpoint

- Listens on `127.0.0.1` and `[::1]` only, path `/mcp`, no admin rights needed.
- Every request needs `Authorization: Bearer <key>` (else `401`). The key is random (256 bits),
  compared in constant time, and stored encrypted with Windows DPAPI for this Windows user.
- `Host` must be the loopback address and port, and an `Origin` header, if present, must be too
  (else `403`), so a website cannot reach the server through DNS rebinding.
- 2025 revisions: `initialize` returns `Mcp-Session-Id`; `GET` opens a stream of notifications;
  `DELETE` ends the session; an unknown session gets `404`.
- 2026‑07‑28: stateless `POST`s; `MCP-Protocol-Version`, `Mcp-Method` and `Mcp-Name` must agree with
  the body (else `400` with `-32020`).
- `tools/call` and `resources/read` answer as server‑sent events when the client accepts them, with
  progress as it happens and keep‑alives during long waits; other requests answer with JSON.

---

## Safety and privacy

- An agent uses the person's **real browser profile** (sign‑ins, cookies). Only connect agents you
  trust, the same as handing someone your browser.
- The page outline and the results never show the **value of a password field**, and typed
  passwords are reported only by their length. The server has no tool that reads **cookies**, so
  HttpOnly session cookies stay out of reach. `browser_evaluate` runs JavaScript in the page, so an
  agent can do whatever the page itself could: one more reason to connect only agents you trust.
- **Typed text is never logged.** The activity log says *"Agent Claude Code typed into textbox
  "Email" on example.com"*.
- The pipe accepts only processes of this Windows user. HTTP accepts only this PC, with the key.
- **Hidden runs** (`--hidden`) leave the person's saved tabs and window place alone. The built‑in
  check keeps history and web apps in memory and puts the download folder back.
- File choosers are caught only in the agent's tabs, and around an agent's action in another tab,
  so the person's own uploads keep working. Alerts close by themselves only in the agent's tabs.

---

## Troubleshooting

- **"The browser engine (WebView2) is not available."** Install the Microsoft Edge WebView2 runtime.
- **The client shows no tools.** Check the path in the client's config, then run
  `PdaNetPortable.exe --mcp-smoke`: it should end with `MCP SMOKE PASS`.
- **The agent waits a long time on its first call.** PdaNet is asking the person in the browser:
  press **Allow** in the bar at the top of the browser window.
- **"AI agent access is turned off in PdaNet."** Settings → AI agents → turn it on.
- **A page says it is in the wrong country.** That is the connection: connect PdaNet in Phone VPN
  mode with a VPN on the phone. `browser_network_requests` shows the route.
- **HTTP: "Port 47823 is in use".** Pick another port in Settings → AI agents.
- **Logs.** The server writes only protocol messages to stdout; human logs go to stderr and to
  `data\pdanet.log` next to the exe.

---

## For developers

```
Mcp/
  McpServer.cs           JSON-RPC and MCP for both protocol eras, concurrency, cancellation, progress
  HttpMcpListener.cs     Streamable HTTP on loopback (key, Host/Origin checks, sessions, SSE)
  StdioTransport.cs      LineChannel: newline-delimited JSON over stdio or a pipe
  AgentHub.cs            the server inside the running app, the pipe, HTTP from Settings
  McpHost.cs             --mcp: relay to the running app, or a browser of its own
  AgentPresence.cs       who drives, approvals, pause/stop, questions for the person
  BrowserSession*.cs     the engine: CDP, isolated world, dialogs, settling, downloads, every tool
  BrowserTools.cs        tool definitions, resources, prompts, server instructions
  snapshot.js            the in-page helper: outline, handles, hit testing, reading
  McpSmoke.cs            --mcp-smoke, the end-to-end check
Views/
  BrowserWindow.Automation.cs   the facade the engine drives (tabs, find, zoom, window, settings…)
  BrowserWindow.Agent.cs        the agent bar
```

Every browser touch runs on the WPF UI thread, one tool call at a time; transports and the protocol
run on background threads. Run the check after a change:

```
cd pdanet-windows
dotnet build -c Debug
bin\Debug\net9.0-windows\PdaNetPortable.exe --mcp-smoke
```
