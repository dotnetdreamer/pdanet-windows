using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PdaNetPortable.Mcp;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PdaNetPortable.Views;

/// <summary>
/// The agent bar of the browser: shown while an AI agent may use the browser. It says which agent
/// it is and what it does right now, lets the person pause or stop it, and asks the questions an
/// agent waits on: may it start, may it clear the browsing data, and can the person help (sign in,
/// a CAPTCHA, a code) before it goes on.
/// </summary>
public partial class BrowserWindow
{
    private AgentPresence? _agents;
    private AgentPrompt? _shownPrompt;
    private DateTime _promptShownAt;
    private string _barState = "";

    private void WireAgents()
    {
        _agents = AgentPresence.Current;
        if (_agents == null) return;
        _agents.Changed += ShowAgents;
        Closed += (_, _) => _agents.Changed -= ShowAgents;
        ShowAgents();
    }

    private void ShowAgents()
    {
        var a = _agents;
        if (a == null) return;
        var prompt = a.Prompt;
        var active = a.ActiveAgents;
        var names = string.Join(" and ", active.Select(c => c.Name).Distinct().Take(2)) + (active.Count > 2 ? $" and {active.Count - 2} more" : "");

        if (prompt != null)
        {
            switch (prompt.Kind)
            {
                case PromptKind.Connect:
                    ShowBar("connect", "B.Cyan", "\uea18", prompt.Title, prompt.Detail, "Allow", "Always allow", "Deny",
                        "Let it use this browser now", $"Always let {prompt.Agent.Name} use the browser without asking", "Do not let it use the browser");
                    break;
                case PromptKind.Sensitive:
                    ShowBar("sensitive", "B.Amber", "\ue7ba", prompt.Title, prompt.Detail, "Allow", null, "Deny",
                        "Let it do this", null, "Do not let it do this");
                    break;
                default:
                    ShowBar("ask", "B.Amber", "\ue77b", prompt.Title, prompt.Detail, "Done", "I can't", "Stop",
                        "Tell the agent you are done, it goes on", "Tell the agent you cannot do it", StopTip);
                    break;
            }
            AgentReply.Visibility = prompt.Kind == PromptKind.Ask && prompt.WantsReply ? Visibility.Visible : Visibility.Collapsed;
            if (!ReferenceEquals(prompt, _shownPrompt))
            {
                _shownPrompt = prompt;
                _promptShownAt = DateTime.Now;
                AgentReply.Text = "";
                if (AgentReply.Visibility == Visibility.Visible) Dispatcher.BeginInvoke(() => AgentReply.Focus());
            }
            return;
        }
        _shownPrompt = null;
        AgentReply.Visibility = Visibility.Collapsed;
        if (active.Count == 0 && a.Acting == null)
        {
            ShowBar("", null, null, null, null, null, null, null, null, null, null);
            return;
        }
        if (a.Paused)
            ShowBar("paused", "B.Amber", "\ue769", "Paused, you have the browser", $"{names} waits until you resume", "Resume", null, "Stop",
                "Let the agent go on", null, StopTip);
        else if (a.Acting is { } acting)
            ShowBar("acting", "B.Accent", "\ue99a", $"{acting.Name} is using this browser", a.Caption ?? "Working", null, "Pause", "Stop",
                null, "Pause the agent and take over the browser", StopTip);
        else
            ShowBar("idle", "B.Accent", "\ue99a", $"{names} can use this browser", "Waiting for its next step. Its tab comes back to the front when it goes on, Pause keeps it waiting", null, "Pause", "Stop",
                null, "Pause the agent", StopTip);
    }

    private const string StopTip = "Stop the AI agent: it can no longer use this browser until you allow it again";

    /// <summary>Shows the bar in one of its states, or hides it when <paramref name="state"/> is empty.</summary>
    private void ShowBar(string state, string? key, string? glyph, string? title, string? detail, string? primary, string? secondary, string? stop,
        string? primaryTip, string? secondaryTip, string? stopTip)
    {
        if (state.Length == 0)
        {
            if (AgentBar.Visibility == Visibility.Visible)
            {
                AgentBar.Visibility = Visibility.Collapsed;
                Pulse(false);
            }
            _barState = "";
            return;
        }
        var brush = Ui.Brush(key!);
        var color = ((SolidColorBrush)brush).Color;
        AgentBar.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x14));
        AgentBar.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x66));
        AgentGlyph.Text = glyph;
        AgentGlyph.Foreground = brush;
        AgentPulse.Fill = brush;
        AgentTitle.Text = title;
        AgentDetail.Text = detail;
        AgentDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        Button(AgentPrimary, primary, primaryTip);
        Button(AgentSecondary, secondary, secondaryTip);
        Button(AgentStop, stop, stopTip);
        if (AgentBar.Visibility != Visibility.Visible)
        {
            AgentBar.Visibility = Visibility.Visible;
            Ui.Enter(AgentBar, rise: 6, ms: 200);
        }
        // The dot breathes while the agent acts or waits for an answer, and rests while it idles.
        Pulse(state is "acting" or "connect" or "sensitive" or "ask");
        _barState = state;
    }

    private static void Button(System.Windows.Controls.Button b, string? text, string? tip)
    {
        b.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        b.Content = text;
        b.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(b, text ?? "");
    }

    private bool _pulsing;

    private void Pulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        if (on)
        {
            var grow = Ui.Loop(0.7, 1.9, 1.6, 24, Ui.EaseOut);
            AgentPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            AgentPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            AgentPulse.BeginAnimation(OpacityProperty, Ui.Loop(0.45, 0, 1.6, 24, Ui.EaseOut));
        }
        else
        {
            AgentPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AgentPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AgentPulse.BeginAnimation(OpacityProperty, null);
            AgentPulse.Opacity = 0;
        }
    }

    // ============================ The person's answers ============================
    private void OnAgentPrimary(object sender, RoutedEventArgs e)
    {
        var a = _agents;
        if (a == null) return;
        if (_shownPrompt is { } p)
        {
            if (TooSoon()) return;
            a.Answer(p, p.Kind switch
            {
                PromptKind.Ask => PromptChoice.Done,
                _ => PromptChoice.Allow,
            }, Reply(p));
            FocusContent();
        }
        else if (a.Paused) a.Resume();
    }

    private void OnAgentSecondary(object sender, RoutedEventArgs e)
    {
        var a = _agents;
        if (a == null) return;
        if (_shownPrompt is { } p)
        {
            if (TooSoon()) return;
            a.Answer(p, p.Kind == PromptKind.Connect ? PromptChoice.AlwaysAllow : PromptChoice.Cant, Reply(p));
            FocusContent();
        }
        else a.Pause();
    }

    private void OnAgentStop(object sender, RoutedEventArgs e)
    {
        var a = _agents;
        if (a == null) return;
        // On a question about one action, the red button says no to that action only.
        if (_shownPrompt is { Kind: PromptKind.Connect or PromptKind.Sensitive } p) a.Answer(p, PromptChoice.Deny);
        else a.Stop();
    }

    private void OnAgentReplyKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _shownPrompt is not { Kind: PromptKind.Ask } p || _agents == null) return;
        _agents.Answer(p, PromptChoice.Done, Reply(p));
        e.Handled = true;
    }

    /// <summary>
    /// A click that lands just as a new question appears belongs to the question before (a double
    /// click on Allow), so it must not answer the new one.
    /// </summary>
    private bool TooSoon() => DateTime.Now - _promptShownAt < TimeSpan.FromMilliseconds(450);

    private string? Reply(AgentPrompt p) => p.WantsReply && AgentReply.Text.Trim().Length > 0 ? AgentReply.Text.Trim() : null;
}
