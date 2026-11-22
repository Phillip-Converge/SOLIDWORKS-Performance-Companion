using System.Windows;
using System.Windows.Controls;
using SWCompanion.Core.Explain;

namespace SWCompanion.App;

/// <summary>
/// A row of "ask why" key-cap buttons with one answer box underneath. Click a
/// key to open its answer; click it again to close. Used by every section.
/// </summary>
internal sealed class WhyPanel
{
    private readonly Border _answerBox;
    private readonly TextBlock _answerTitle;
    private readonly TextBlock _answerText;
    private readonly Func<string, string?> _answer;
    private readonly Dictionary<string, Button> _keys = new();
    private WhyQuestion? _open;

    /// <param name="answer">Returns the answer for a question id, or null while there is no reading yet.</param>
    public WhyPanel(Panel keys, Border answerBox, TextBlock answerTitle, TextBlock answerText,
                    IEnumerable<WhyQuestion> questions, Func<string, string?> answer, Style keyStyle)
    {
        _answerBox = answerBox;
        _answerTitle = answerTitle;
        _answerText = answerText;
        _answer = answer;

        foreach (var q in questions)
        {
            var key = new Button { Content = q.Label, Style = keyStyle };
            key.Click += (_, _) => Toggle(q);
            _keys[q.Id] = key;
            keys.Children.Add(key);
        }
    }

    /// <summary>Call after each new reading so an open answer's live numbers stay current.</summary>
    public void Refresh()
    {
        if (_open is not null) _answerText.Text = _answer(_open.Id) ?? "measuring... the answer will appear in a moment.";
    }

    private void Toggle(WhyQuestion q)
    {
        _open = _open?.Id == q.Id ? null : q;
        foreach (var (id, key) in _keys)
            key.Tag = id == _open?.Id ? "on" : null;

        if (_open is null)
        {
            _answerBox.Visibility = Visibility.Collapsed;
            return;
        }
        _answerTitle.Text = q.Label;
        Refresh();
        _answerBox.Visibility = Visibility.Visible;
    }
}
