using System.Globalization;

namespace CloudScribe.App.Composition;

/// <summary>A no-cost rehearsal's checklist. The final verdict counts every blocking check (voice, folder, tools,
/// approval and each file), so it can never say "ready" while any line above it says otherwise.</summary>
public sealed class BatchRehearsalReport
{
    private readonly List<(char Mark, string Text)> _lines = [];
    private int _readyFiles;
    private int _blockedFiles;

    public int Blocking { get; private set; }

    public void Pass(string text) => _lines.Add(('✓', text));

    public void Note(string text) => _lines.Add(('•', text));

    public void Block(string text) { _lines.Add(('✗', text)); Blocking++; }

    public void File(bool ready, string text)
    {
        if (ready) { _readyFiles++; Pass(text); }
        else { _blockedFiles++; Block(text); }
    }

    public bool Ready => Blocking == 0;

    public string Verdict
    {
        get
        {
            if (Blocking == 0)
                return string.Create(CultureInfo.InvariantCulture, $"Ready: all {_readyFiles} remaining file(s) can start. Nothing was sent.");
            int other = Blocking - _blockedFiles;
            var parts = new List<string>();
            if (_blockedFiles > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_blockedFiles} file(s) would stop"));
            if (other > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{other} batch check(s) failed"));
            return string.Create(CultureInfo.InvariantCulture, $"Not ready: {string.Join(" and ", parts)} ({_readyFiles} file(s) ready). Fix the ✗ lines first. Nothing was sent.");
        }
    }

    public override string ToString() =>
        "Rehearsal (no cost, nothing sent):\n" + string.Join('\n', _lines.Select(x => $"{x.Mark} {x.Text}")) + "\n" + Verdict;
}
