using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SWCompanion.Core.Ask;
using SWCompanion.Core.Io;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// "Ask AI": you ask your own AI service (Claude, OpenAI, or a model on this PC) about what SOLIDWORKS is doing.
/// The Companion sends the question with what it sees right now, private names hidden, and shows the answer in the
/// conversation. Before the first question (and every question, if you prefer) it shows exactly what will be sent.
/// The AI gets no tools: it can only answer. Every question is noted in the activity log.
/// </summary>
public partial class MainWindow
{
    // One scrubber per run of the Companion, so a file keeps the same placeholder across follow-up questions.
    private readonly Scrubber _askScrub = new(Environment.MachineName, Environment.UserName,
        Environment.UserDomainName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? null : [Environment.UserDomainName]);
    private readonly List<AskTurn> _askTurns = new();
    private CancellationTokenSource? _askCancel;
    private bool _aiLoading;

    private AiProvider Provider => App.Settings.AiProvider switch
    {
        "claude" => AiProvider.Claude,
        "openai" => AiProvider.OpenAI,
        "local" => AiProvider.Local,
        _ => AiProvider.Off,
    };

    private string Model => string.IsNullOrWhiteSpace(App.Settings.AiModel) ? AskAi.DefaultModel(Provider) : App.Settings.AiModel!.Trim();

    /// <summary>Ready to ask: a service is chosen, a model is known, and (for online services) a key is saved.</summary>
    private bool AiReady => Provider != AiProvider.Off && Model.Length > 0 && (Provider == AiProvider.Local || AiKeyStore.Has(Provider));

    private void SetupAsk()
    {
        _aiLoading = true;
        var s = App.Settings;
        AiUrlBox.Text = s.AiLocalUrl;
        AiModelBox.Text = Model;
        AiPreviewEach.IsChecked = !s.AiSkipPreview;
        _aiLoading = false;
        ShowAskState();
    }

    /// <summary>Shows or hides the ask box and the Settings rows to match what's set up.</summary>
    private void ShowAskState()
    {
        var p = Provider;
        foreach (var b in AiProviderChips.Children.OfType<Button>())
            b.Tag = (string)b.CommandParameter == App.Settings.AiProvider ? "on" : null;
        bool online = p is AiProvider.Claude or AiProvider.OpenAI;
        AiKeyLabel.Visibility = AiKeyRow.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        AiUrlLabel.Visibility = AiUrlBox.Visibility = p == AiProvider.Local ? Visibility.Visible : Visibility.Collapsed;
        AiKeyState.Text = online ? (AiKeyStore.Has(p) ? "a key is saved (encrypted, this PC only)" : "no key saved yet") : "";
        AskRow.Visibility = AiReady ? Visibility.Visible : Visibility.Collapsed;
        AskSetup.Visibility = AiReady ? Visibility.Collapsed : Visibility.Visible;
        HangAskKey.Visibility = AiReady ? Visibility.Visible : Visibility.Collapsed;
        AiStatus.Text = p == AiProvider.Off ? "Off. Nothing is ever sent anywhere."
            : AiReady ? $"Ready: questions go to {AskAi.Name(p)}, model {Model}."
            : Model.Length == 0 ? "Press \"check connection\", then pick a model." : "Save your key to finish setting up.";
    }

    // ---- Settings ----

    private void AiProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string tag }) return;
        if (tag == App.Settings.AiProvider) return;
        App.Settings.AiProvider = tag;
        App.Settings.AiModel = null; // each service has its own models
        App.Settings.Save();
        _aiLoading = true;
        AiModelList.ItemsSource = null;
        AiModelListScroll.Visibility = Visibility.Collapsed;
        AiModelBox.Text = Model;
        _aiLoading = false;
        _log.Write($"settings: Ask AI set to {AskAi.Name(Provider)}.");
        ShowAskState();
    }

    private void AiSaveKey_Click(object sender, RoutedEventArgs e)
    {
        string key = AiKeyBox.Password.Trim();
        if (key.Length < 10) { AiStatus.Text = "That doesn't look like a whole key. Paste it again."; return; }
        AiKeyStore.Save(Provider, key);
        AiKeyBox.Clear();
        _log.Write($"settings: an API key for {AskAi.Name(Provider)} was saved (encrypted; the key itself is never logged).");
        ShowAskState();
    }

    private void AiRemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (!AiKeyStore.Has(Provider)) return;
        if (MessageBox.Show(this, $"Remove the saved key for {AskAi.Name(Provider)} from this PC?", "Ask AI", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        AiKeyStore.Remove(Provider);
        _log.Write($"settings: the API key for {AskAi.Name(Provider)} was removed.");
        ShowAskState();
    }

    private void AiUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        string url = AiUrlBox.Text.Trim();
        if (url.Length == 0 || url == App.Settings.AiLocalUrl) return;
        App.Settings.AiLocalUrl = url;
        App.Settings.Save();
        ShowAskState();
    }

    private void AiModel_Changed(object sender, RoutedEventArgs e)
    {
        if (_aiLoading) return;
        string m = (AiModelBox.Text ?? "").Trim();
        if (m == (App.Settings.AiModel ?? "")) return;
        App.Settings.AiModel = m.Length == 0 ? null : m;
        App.Settings.Save();
        ShowAskState();
    }

    private async void AiCheck_Click(object sender, RoutedEventArgs e)
    {
        var p = Provider;
        if (p == AiProvider.Off) { AiStatus.Text = "Choose an AI service first."; return; }
        if (p != AiProvider.Local && !AiKeyStore.Has(p)) { AiStatus.Text = "Save your key first."; return; }
        AiStatus.Text = "Checking...";
        var (models, problem) = await AskAi.ListModels(p, AiKeyStore.Load(p), App.Settings.AiLocalUrl, CancellationToken.None);
        if (problem is not null) { AiStatus.Text = problem; _log.Write($"ai: connection check to {AskAi.Name(p)} failed: {problem}"); return; }
        _aiLoading = true;
        string keep = Model;
        AiModelBox.Text = keep.Length > 0 ? keep : models.FirstOrDefault() ?? "";
        _aiLoading = false;
        AiModel_Changed(sender, e);
        _aiModels = models;
        ShowModelChips();
        AiStatus.Text = $"Connected. {models.Count} models available: click one below, or type a name. The one in the box is used.";
        _log.Write($"ai: connection check to {AskAi.Name(p)} worked ({models.Count} models).");
        ShowAskState();
    }

    private List<string> _aiModels = new();

    private void ShowModelChips()
    {
        AiModelList.ItemsSource = _aiModels.Select(m => new ModelChip(m, m == Model ? "on" : null)).ToList();
        AiModelListScroll.Visibility = _aiModels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AiModelPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModelChip chip) return;
        AiModelBox.Text = chip.Name;
        AiModel_Changed(sender, e);
        ShowModelChips();
    }

    private sealed record ModelChip(string Name, string? On);

    private void AiPreviewEach_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AiSkipPreview = AiPreviewEach.IsChecked != true;
        App.Settings.Save();
    }

    private void AskSetup_Click(object sender, MouseButtonEventArgs e) => ShowPage("settings");

    // ---- asking ----

    private void AskBox_TextChanged(object sender, TextChangedEventArgs e) =>
        AskHint.Visibility = AskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Ask_Click(sender, e); }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        string q = AskBox.Text.Trim();
        if (q.Length == 0 || !AiReady) return;
        var answer = await AskQuestion(q, focus: null);
        if (answer is not null) AskBox.Clear();
    }

    private async void HangAsk_Click(object sender, RoutedEventArgs e)
    {
        if (_hangShown is not { } m) return;
        var x = HangExplainer.For(m, _winEvents);
        string focus = $"{x.Title} ({x.When}). What happened: {x.WhatHappened} " +
                       (x.WindowsSaw.Count > 0 ? "Windows recorded at the same time: " + string.Join("; ", x.WindowsSaw) + ". " : "") +
                       "Recorded: " + string.Join("; ", x.Technical.Take(5));
        HangAiBox.Visibility = Visibility.Visible;
        HangAiFrom.Text = $"Asking {AskAi.Name(Provider)}...";
        HangAiText.Text = "";
        var answer = await AskQuestion("Why did this happen, and what should I check first?", focus);
        HangAiFrom.Text = answer is null ? "Not asked." : $"{AskAi.Name(Provider)} says:";
        HangAiText.Text = answer ?? "";
        if (answer is null) HangAiBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>Asks one question. Returns the answer with real names restored, or null if you cancelled.</summary>
    private async Task<string?> AskQuestion(string question, string? focus)
    {
        var p = Provider;
        var request = AskPrompt.Build(question, AskContextNow(focus), _askScrub, _askTurns);
        if (!App.Settings.AiSkipPreview)
        {
            var preview = new AskPreviewWindow(request, AskAi.Name(p), Model, _askScrub.HiddenCount, sendButton: true) { Owner = this };
            if (preview.ShowDialog() != true) return null;
            if (preview.DontShowAgain) { App.Settings.AiSkipPreview = true; App.Settings.Save(); AiPreviewEach.IsChecked = false; }
        }

        _askCancel?.Cancel();
        _askCancel = new CancellationTokenSource();
        var now = DateTime.Now;
        _chat.Post(new ChatMessage(now, ChatFrom.You, "", question, Mood.Calm));
        var waiting = new ChatMessage(now, ChatFrom.Companion, "", $"Asking {AskAi.Name(p)}...", Mood.Working);
        _chat.Post(waiting);
        AskKey.IsEnabled = HangAskKey.IsEnabled = false;
        _log.Write($"ai: asked {AskAi.Name(p)} ({Model}): \"{question}\"" + (focus is null ? "" : " about a hang-up") +
                   $"; sent {request.AsText().Length:N0} characters, {_askScrub.HiddenCount} private names hidden.");

        var a = await AskAi.Ask(p, Model, AiKeyStore.Load(p), App.Settings.AiLocalUrl, request, _askCancel.Token);
        AskKey.IsEnabled = HangAskKey.IsEnabled = true;
        if (a.Text is null)
        {
            _chat.Replace(waiting, waiting with { Title = "Couldn't get an answer", Text = a.Problem ?? "", Mood = Mood.Warn });
            _log.Write($"ai: no answer after {a.Seconds:0.0} s: {a.Problem}");
            return a.Problem is null ? null : "Couldn't get an answer: " + a.Problem;
        }
        _askTurns.Add(new AskTurn(_askScrub.Hide(question), a.Text));
        string shown = _askScrub.Restore(a.Text);
        _chat.Replace(waiting, new ChatMessage(DateTime.Now, ChatFrom.Companion, $"{AskAi.Name(p)} says:", shown, Mood.Calm));
        _log.Write($"ai: answered in {a.Seconds:0.0} s ({a.Text.Length:N0} characters).");
        return shown;
    }

    /// <summary>"what's sent?": shows exactly what would go to the AI right now, without sending anything.</summary>
    private void AskWhatSent_Click(object sender, RoutedEventArgs e)
    {
        string q = AskBox.Text.Trim();
        var request = AskPrompt.Build(q.Length > 0 ? q : "(your question goes here)", AskContextNow(null), new Scrubber(Environment.MachineName, Environment.UserName), _askTurns);
        new AskPreviewWindow(request, Provider == AiProvider.Off ? "no AI (Ask AI is off)" : AskAi.Name(Provider), Model, -1, sendButton: false) { Owner = this }.ShowDialog();
    }

    /// <summary>What the Companion sees right now, for a question (names are hidden later, by the scrubber).</summary>
    private AskContext AskContextNow(string? focus)
    {
        var lastSaid = _chat.Messages.LastOrDefault(m => m.From == ChatFrom.Companion && !m.Title.EndsWith(" says:", StringComparison.Ordinal)
                                                         && !m.Text.StartsWith("Asking ", StringComparison.Ordinal));
        string status = lastSaid is null ? "watching" : (lastSaid.Title.TrimEnd('.') + ". " + lastSaid.Text).Trim(' ', '.') + ".";
        var cpu = _lastSmoothed;
        var mem = _lastMemory;
        double disk = _lastIo?.SolidWorks is { } io ? io.ReadBytesPerSec + io.WriteBytesPerSec : 0;
        string vitals = string.Join(", ", new[]
        {
            cpu is null ? null : $"processor {cpu.SystemPercent:0}% of the PC ({cpu.BusyCores} of {cpu.CoreCount} cores busy)" +
                                 (cpu.SolidWorks is { } sw ? $", SOLIDWORKS {sw.CoresWorth:0.0} cores" : ""),
            mem is null ? null : $"memory {mem.UsedRam / 1073741824.0:0} of {mem.TotalRam / 1073741824.0:0} GB" +
                                 (mem.SolidWorks is { } sm ? $", SOLIDWORKS {sm.InRam / 1073741824.0:0.0} GB" : ""),
            _lastGpu?.SolidWorksAdapter is { } g ? $"graphics card {g.BusyPercent:0}% busy" : null,
            $"SOLIDWORKS disk {IoExplainer.Size((ulong)disk)}/s",
        }.Where(x => x is not null));
        return new AskContext(
            _versions?.Describe(_processes.Any(pr => pr.Role == Core.Processes.ProcessRole.Main)),
            status, vitals,
            _story.Lines.Select(l => $"{l.Clock}  {l.Text}").ToList(),
            _catcher.Caught.Select(m => m.Line + (m.LookInto ? "  [worth looking into]" : "")).ToList(),
            _fixes.Select(f => f.Title).ToList(),
            focus);
    }
}

/// <summary>"What will be sent": exactly the text that goes to the AI, names already hidden. Nothing is sent from here unless you press Send.</summary>
internal sealed class AskPreviewWindow : Window
{
    private readonly CheckBox _dontShow = new() { Content = "Don't show this before every question", Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    public bool DontShowAgain => _dontShow.IsChecked == true;

    public AskPreviewWindow(AskRequest request, string service, string model, int hidden, bool sendButton)
    {
        Title = "What will be sent";
        Width = 760; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["Bg"];
        string text = request.AsText();
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 13.5,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Text"],
            Text = $"This goes to {service}" + (model.Length > 0 ? $" (model {model})" : "") + $", {text.Length:N0} characters. " +
                   "Names of files, folders, servers, this PC and you have been replaced with placeholders" +
                   (hidden >= 0 ? $" ({hidden} so far in this conversation)" : "") +
                   ". The answer you see puts the real names back, on this PC only. Nothing else is sent.",
        };
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["MonoFont"], FontSize = 12, Padding = new Thickness(8),
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Module"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Text"],
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Line"],
        };
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var key = (Style)Application.Current.Resources["KeyButton"];
        _dontShow.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Text"];
        var close = new Button { Content = sendButton ? "Cancel" : "Close", Padding = new Thickness(16, 5, 16, 5), IsCancel = true, Style = key };
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        if (sendButton)
        {
            var send = new Button { Content = "Send", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Style = key };
            send.Click += (_, _) => { DialogResult = true; };
            DockPanel.SetDock(send, Dock.Right);
            buttons.Children.Add(send);
            buttons.Children.Add(_dontShow);
        }
        else buttons.Children.Add(new TextBlock());
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(buttons);
        root.Children.Add(box);
        Content = root;
    }
}
