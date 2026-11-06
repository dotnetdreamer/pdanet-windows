using System.Windows.Threading;
using PdaNetPortable.Services;

namespace PdaNetPortable.Mcp;

/// <summary>Whether the person lets an agent use the browser.</summary>
public enum AgentAccess { Pending, Allowed, Denied }

/// <summary>
/// An AI agent that talks to the browser: the client name it gave, how it connects, and whether
/// the person allowed it. One per client name and transport, kept for the run of PdaNet.
/// </summary>
public sealed class AgentClient
{
    internal AgentClient(string key, string rawName, string name, string transport)
    {
        Key = key;
        RawName = rawName;
        Name = name;
        Transport = transport;
    }

    public string Key { get; }
    /// <summary>The name the client sent, such as "claude-code".</summary>
    public string RawName { get; }
    /// <summary>The name people read, such as "Claude Code".</summary>
    public string Name { get; }
    public string Transport { get; }
    public AgentAccess Access { get; internal set; }
    internal DateTime DeniedUntil { get; set; }
    /// <summary>The person pressed Stop: the agent comes back only once they allow it again, trusted or not.</summary>
    internal bool Stopped { get; set; }
    internal int Connections { get; set; }
    public DateTime LastSeen { get; internal set; } = DateTime.Now;

    public override string ToString() => Name;

    /// <summary>A readable name for the name an MCP client sends about itself.</summary>
    public static string Pretty(string? raw, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        var n = (raw ?? "").Trim();
        if (n.Length == 0) return "An AI agent";
        var key = n.ToLowerInvariant();
        if (key.Contains("claude-code") || key == "claude code") return "Claude Code";
        if (key is "claude-ai" or "claude" or "claude desktop") return "Claude";
        if (key.Contains("cursor")) return "Cursor";
        if (key.Contains("visual studio code") || key is "vscode" or "code") return "VS Code";
        if (key.Contains("windsurf") || key.Contains("codeium")) return "Windsurf";
        if (key.Contains("inspector")) return "MCP Inspector";
        if (key.Contains("codex")) return "Codex";
        if (key.Contains("gemini")) return "Gemini CLI";
        if (key.Contains("copilot")) return "GitHub Copilot";
        if (key.Contains("goose")) return "Goose";
        var words = n.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

public enum PromptKind { Connect, Sensitive, Ask }

public enum PromptChoice { Allow, AlwaysAllow, Deny, Done, Cant, TimedOut, Cancelled }

/// <summary>A question for the person, shown in the bar of the browser until someone answers it.</summary>
public sealed class AgentPrompt
{
    internal AgentPrompt(PromptKind kind, AgentClient agent, string title, string detail, bool wantsReply)
    {
        Kind = kind;
        Agent = agent;
        Title = title;
        Detail = detail;
        WantsReply = wantsReply;
    }

    public PromptKind Kind { get; }
    public AgentClient Agent { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool WantsReply { get; }
    internal TaskCompletionSource<(PromptChoice Choice, string? Reply)> Answer { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Who drives the browser, as the person sees it: the agents that are connected, the one acting
/// now and what it does, whether the person paused them, and the questions waiting for an answer.
/// It is the gate every tool call passes: an agent acts only once the person allowed it, never
/// while paused, and never after Stop. The browser window shows it in its agent bar.
///
/// State is kept under a lock because calls arrive from transport threads; Changed is raised on
/// the UI thread, where the bar reads it.
/// </summary>
public sealed class AgentPresence
{
    /// <summary>The presence the browser window shows, set by whoever hosts the MCP server.</summary>
    public static AgentPresence? Current { get; set; }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DenyHold = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PauseLimit = TimeSpan.FromMinutes(15);

    private readonly Dispatcher _ui;
    private readonly object _lock = new();
    private readonly Dictionary<string, AgentClient> _clients = new();
    private readonly List<AgentPrompt> _prompts = new();
    private TaskCompletionSource _resume = NewSignal(done: true);
    private bool _paused;

    public AgentPresence(Dispatcher ui) => _ui = ui;

    /// <summary>Standalone mode: the client that started PdaNet is allowed without asking.</summary>
    public bool AutoAllow { get; init; }

    /// <summary>Whether a person can see the browser window to answer questions.</summary>
    public Func<bool> CanAsk { get; set; } = () => true;

    /// <summary>Whether agent access is on (the setting in PdaNet).</summary>
    public Func<bool> Enabled { get; set; } = () => true;

    /// <summary>Client names the person chose to always allow, and how to remember a new one.</summary>
    public Func<string, bool> IsTrusted { get; set; } = _ => false;
    public Action<string>? Trust { get; set; }

    /// <summary>Raised on the UI thread whenever anything the bar shows changes.</summary>
    public event Action? Changed;

    /// <summary>The person pressed Stop: the host drops the connections of these agents.</summary>
    public event Action? StopRequested;

    /// <summary>A question waits for the person: the host brings the browser forward.</summary>
    public event Action? AttentionNeeded;

    /// <summary>The person paused, resumed or stopped the agents: (level, text) for connected agents.</summary>
    public event Action<string, string>? Notice;

    // ============================ What the bar shows ============================
    public string? Caption { get; private set; }
    public AgentClient? Acting { get; private set; }
    public bool Paused { get { lock (_lock) return _paused; } }

    /// <summary>Allowed agents that are connected, or acted in the last two minutes.</summary>
    public IReadOnlyList<AgentClient> ActiveAgents
    {
        get { lock (_lock) return _clients.Values.Where(c => c.Access == AgentAccess.Allowed && Live(c)).ToList(); }
    }

    private static bool Live(AgentClient c) => c.Connections > 0 || DateTime.Now - c.LastSeen < TimeSpan.FromMinutes(2);

    public AgentPrompt? Prompt { get { lock (_lock) return _prompts.FirstOrDefault(); } }

    /// <summary>True while any agent is connected or acting, so the browser keeps its automation hooks.</summary>
    public bool AnyActive
    {
        get { lock (_lock) return _clients.Values.Any(Live); }
    }

    // ============================ Clients ============================
    public AgentClient Client(string? rawName, string? title, string transport)
    {
        var name = AgentClient.Pretty(rawName, title);
        var key = transport + ":" + (string.IsNullOrWhiteSpace(rawName) ? name : rawName!.Trim()).ToLowerInvariant();
        lock (_lock)
        {
            if (!_clients.TryGetValue(key, out var c))
            {
                c = new AgentClient(key, rawName?.Trim() ?? name, name, transport);
                _clients[key] = c;
            }
            c.LastSeen = DateTime.Now;
            return c;
        }
    }

    /// <summary>A connection of this agent opened (stdio, pipe or an HTTP session).</summary>
    public void Attach(AgentClient c)
    {
        lock (_lock) c.Connections++;
        Raise();
    }

    public void Detach(AgentClient c)
    {
        lock (_lock)
        {
            c.Connections = Math.Max(0, c.Connections - 1);
            if (c.Connections == 0) c.LastSeen = DateTime.Now - TimeSpan.FromMinutes(5);
        }
        Raise();
    }

    // ============================ The gate ============================
    /// <summary>
    /// Lets the call through once the person allowed this agent. The first call of an agent the
    /// person has not allowed yet asks them in the browser, unless they chose to always allow it.
    /// </summary>
    public async Task AuthorizeAsync(ToolCall call)
    {
        var c = call.Agent;
        if (!Enabled())
            throw new McpToolException("AI agent access is turned off in PdaNet. The person can turn it on in PdaNet, Settings, AI agents.");
        lock (_lock)
        {
            c.LastSeen = DateTime.Now;
            if (c.Access == AgentAccess.Allowed) return;
            if (c.Access == AgentAccess.Denied && DateTime.Now < c.DeniedUntil)
                throw new McpToolException("The person did not allow this agent to use the PdaNet browser. Ask them before trying again.");
            if (!c.Stopped && (AutoAllow || IsTrusted(c.RawName)))
            {
                c.Access = AgentAccess.Allowed;
                Log.Info($"AI agent {c.Name} connected to the browser ({c.Transport})");
                Raise();
                return;
            }
        }
        if (!CanAsk())
            throw new McpToolException(c.Stopped
                ? "The person stopped this agent in the PdaNet browser. It can come back once they allow it again."
                : "The PdaNet browser has nobody to ask for permission. Open PdaNet so the person can allow this agent.");

        // One question per agent, however many calls wait for it.
        AgentPrompt? prompt;
        lock (_lock) prompt = _prompts.FirstOrDefault(p => p.Kind == PromptKind.Connect && ReferenceEquals(p.Agent, c));
        prompt ??= Add(new AgentPrompt(PromptKind.Connect, c, $"{c.Name} wants to use the PdaNet browser{(c.Stopped ? " again" : "")}",
            "It can open websites, read them, click and type, signed in as you", wantsReply: false));
        call.Progress($"Waiting for the person at the PC to allow {c.Name} in the PdaNet browser");
        var (choice, _) = await WaitAsync(prompt, ConnectTimeout, call);
        lock (_lock)
        {
            switch (choice)
            {
                case PromptChoice.Allow:
                case PromptChoice.AlwaysAllow:
                    c.Access = AgentAccess.Allowed;
                    c.Stopped = false;
                    break;
                case PromptChoice.Deny:
                    c.Access = AgentAccess.Denied;
                    c.DeniedUntil = DateTime.Now + DenyHold;
                    break;
            }
        }
        if (choice == PromptChoice.AlwaysAllow) Trust?.Invoke(c.RawName);
        Raise();
        switch (choice)
        {
            case PromptChoice.Allow:
            case PromptChoice.AlwaysAllow:
                Log.Info($"The person allowed AI agent {c.Name} to use the browser");
                return;
            case PromptChoice.Deny:
                Log.Info($"The person did not allow AI agent {c.Name}");
                throw new McpToolException("The person did not allow this agent to use the PdaNet browser.");
            default:
                throw new McpToolException("Nobody at the PC answered the question whether this agent may use the PdaNet browser. Ask the person to allow it in the browser, then try again.");
        }
    }

    /// <summary>Waits while the person has paused the agents, then marks the call as acting.</summary>
    public async Task EnterAsync(ToolCall call, string caption)
    {
        await HoldAsync(call);
        Act(call.Agent, caption);
    }

    /// <summary>
    /// Waits while the person has paused the agents. A call passes here before it starts and again
    /// before each key, click or scroll it sends, so a call that is typing when the person presses
    /// Pause stops after the key it is on, and goes on once they resume. True when it waited.
    /// </summary>
    public async Task<bool> HoldAsync(ToolCall call)
    {
        var start = DateTime.Now;
        bool waited = false;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (call.Agent.Access != AgentAccess.Allowed)
                    throw new McpToolException("The person stopped this agent in the PdaNet browser.");
                if (!_paused) return waited;
                wait = _resume.Task;
            }
            if (DateTime.Now - start > PauseLimit)
                throw new McpToolException("The person paused the agent in the PdaNet browser and has not resumed it. Try again later.");
            call.Progress("Paused by the person at the PC, waiting until they resume");
            waited = true;
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10), call.Ct));
            call.Ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Throws unless the agent may act right now: access is on and the person has not stopped it.</summary>
    public void Check(ToolCall call)
    {
        if (!Enabled())
            throw new McpToolException("AI agent access is turned off in PdaNet. The person can turn it on in PdaNet, Settings, AI agents.");
        if (call.Agent.Access != AgentAccess.Allowed)
            throw new McpToolException("The person stopped this agent in the PdaNet browser.");
    }

    public void Act(AgentClient agent, string caption)
    {
        lock (_lock)
        {
            Acting = agent;
            Caption = caption;
            agent.LastSeen = DateTime.Now;
        }
        Raise();
    }

    public void Leave(AgentClient agent)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(Acting, agent)) return;
            Acting = null;
            Caption = null;
            agent.LastSeen = DateTime.Now;
        }
        Raise();
    }

    // ============================ Questions ============================
    /// <summary>
    /// Asks the person to approve something an agent wants to do to what is theirs: acting in or
    /// closing their tabs, deleting history or data, or turning a safety setting off.
    /// </summary>
    public async Task<bool> ApproveAsync(ToolCall call, string title, string detail)
    {
        if (!CanAsk()) return false;
        var prompt = Add(new AgentPrompt(PromptKind.Sensitive, call.Agent, title, detail, wantsReply: false));
        call.Progress("Waiting for the person to approve: " + title);
        var (choice, _) = await WaitAsync(prompt, TimeSpan.FromMinutes(3), call);
        return choice == PromptChoice.Allow;
    }

    /// <summary>Hands the browser to the person with a message, and waits until they are done.</summary>
    public async Task<(PromptChoice Choice, string? Reply)> AskAsync(ToolCall call, string message, bool wantsReply, TimeSpan timeout)
    {
        var prompt = Add(new AgentPrompt(PromptKind.Ask, call.Agent, $"{call.Agent.Name} asks for your help", message, wantsReply));
        call.Progress("Waiting for the person: " + message);
        return await WaitAsync(prompt, timeout, call);
    }

    private AgentPrompt Add(AgentPrompt p)
    {
        lock (_lock) _prompts.Add(p);
        Raise();
        _ui.BeginInvoke(() => AttentionNeeded?.Invoke());
        return p;
    }

    private async Task<(PromptChoice, string?)> WaitAsync(AgentPrompt p, TimeSpan timeout, ToolCall call)
    {
        var until = DateTime.Now + timeout;
        try
        {
            while (true)
            {
                var left = until - DateTime.Now;
                if (left <= TimeSpan.Zero) return (PromptChoice.TimedOut, null);
                var tick = left < TimeSpan.FromSeconds(10) ? left : TimeSpan.FromSeconds(10);
                var done = await Task.WhenAny(p.Answer.Task, Task.Delay(tick, call.Ct));
                if (done == p.Answer.Task) return await p.Answer.Task;
                call.Ct.ThrowIfCancellationRequested();
                call.Progress($"Still waiting for the person ({(int)(until - DateTime.Now).TotalSeconds}s left)");
            }
        }
        finally
        {
            lock (_lock) _prompts.Remove(p);
            p.Answer.TrySetResult((PromptChoice.Cancelled, null));
            Raise();
        }
    }

    /// <summary>The person answered the question at the top of the bar.</summary>
    public void Answer(AgentPrompt p, PromptChoice choice, string? reply = null)
    {
        lock (_lock) _prompts.Remove(p);
        p.Answer.TrySetResult((choice, reply));
        Raise();
    }

    // ============================ The person's controls ============================
    public void Pause()
    {
        lock (_lock)
        {
            if (_paused) return;
            _paused = true;
            _resume = NewSignal(done: false);
        }
        Log.Info("The person paused the AI agents in the browser");
        Notice?.Invoke("notice", "The person paused the agents: calls wait until they resume.");
        Raise();
    }

    public void Resume()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (!_paused) return;
            _paused = false;
            signal = _resume;
        }
        signal.TrySetResult();
        Log.Info("The person resumed the AI agents in the browser");
        Notice?.Invoke("notice", "The person resumed the agents.");
        Raise();
    }

    /// <summary>Stops every agent: their calls fail and they must be allowed again to come back.</summary>
    public void Stop()
    {
        List<AgentPrompt> waiting;
        TaskCompletionSource paused;
        lock (_lock)
        {
            foreach (var c in _clients.Values)
            {
                if (c.Access == AgentAccess.Allowed || c.Connections > 0)
                {
                    c.Access = AgentAccess.Denied;
                    c.DeniedUntil = DateTime.Now + DenyHold;
                    c.Stopped = true;
                }
            }
            waiting = _prompts.ToList();
            _prompts.Clear();
            Acting = null;
            Caption = null;
            // Calls that wait out a pause wake up and find their agent stopped.
            _paused = false;
            paused = _resume;
        }
        foreach (var p in waiting) p.Answer.TrySetResult((PromptChoice.Deny, null));
        paused.TrySetResult();
        Log.Info("The person stopped the AI agents in the browser");
        Notice?.Invoke("warning", "The person stopped the agents: they must be allowed again to use the browser.");
        Raise();
        _ui.BeginInvoke(() => StopRequested?.Invoke());
    }

    /// <summary>Forgets every decision, for when access is turned off or the always allowed list is cleared.</summary>
    public void Reset()
    {
        List<AgentPrompt> waiting;
        lock (_lock)
        {
            foreach (var c in _clients.Values)
            {
                c.Access = AgentAccess.Pending;
                c.Stopped = false;
            }
            waiting = _prompts.ToList();
            _prompts.Clear();
        }
        foreach (var p in waiting) p.Answer.TrySetResult((PromptChoice.Deny, null));
        Raise();
    }

    private void Raise()
    {
        if (_ui.CheckAccess()) Changed?.Invoke();
        else _ui.BeginInvoke(() => Changed?.Invoke());
    }

    private static TaskCompletionSource NewSignal(bool done)
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done) t.SetResult();
        return t;
    }

    private static Logger Log => Logger.Instance;
}
