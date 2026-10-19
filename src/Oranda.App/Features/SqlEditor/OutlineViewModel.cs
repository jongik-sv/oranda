using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Shell;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.SqlEditor;

/// <summary>문서 개요 항목 = 문장 하나. <see cref="Line"/> 은 1부터.</summary>
public sealed record OutlineItem(string Title, int Line, int Offset, StatementKind Kind);

/// <summary>
/// <c>oranda.panel.outline</c> — 활성 SQL 편집기의 문장 목록. 글자가 바뀌면 잠깐(300 ms) 기다렸다가 다시 만든다.
/// 항목을 고르면 그 문장 앞으로 커서가 간다.
/// </summary>
[OrandaToolPanel("oranda.panel.outline", ToolPanelKind.Tool, DockLocation.Right, Order = 10)]
public sealed partial class OutlineViewModel : ToolPanelViewModelBase, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly ObservableCollection<OutlineItem> _items = [];
    private SqlEditorHost? _host;
    private SqlEditorViewModel? _watched;
    private CancellationTokenSource? _pending;
    private bool _attached;

    // 매개변수 없는 생성자만 둔다. 셸의 생성자 고르기(ServiceContainer.Create)는 등록되지 않은 참조 형식 매개변수를 만나면
    // 지연 어셈블리를 훑어 시작 때 Oranda.Oracle·Oranda.Language 를 로드한다(D-006, 시작 시험이 막는다).
    // 호스트에는 처음 Items 를 읽을 때 붙고, 시험은 Host 를 초기화 구문으로 준다.

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public ObservableCollection<OutlineItem> Items
    {
        get
        {
            EnsureAttached();
            return _items;
        }
    }

    private void EnsureAttached()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Host.ActiveChanged += OnActiveChanged;
        Attach(Host.Active);
    }

    private void OnActiveChanged(object? sender, EventArgs e) => Attach(Host.Active);

    public void Dispose()
    {
        if (_attached)
        {
            Host.ActiveChanged -= OnActiveChanged;
        }

        _pending?.Cancel();
        _pending?.Dispose();
        if (_watched is not null)
        {
            _watched.TextChanged -= OnTextChanged;
        }
    }

    [ObservableProperty]
    public partial OutlineItem? Selected { get; set; }

    partial void OnSelectedChanged(OutlineItem? value)
    {
        if (value is not null)
        {
            GoTo(value);
        }
    }

    public void GoTo(OutlineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var tab = Host.Active;
        if (tab is null)
        {
            return;
        }

        tab.Select(item.Offset, 0);
        tab.Surface?.ScrollToLine(item.Line);
        tab.Surface?.FocusEditor();
    }

    private void Attach(SqlEditorViewModel? tab)
    {
        if (_watched is not null)
        {
            _watched.TextChanged -= OnTextChanged;
        }

        _watched = tab;
        if (tab is not null)
        {
            tab.TextChanged += OnTextChanged;
        }

        Refresh();
    }

    private async void OnTextChanged(object? sender, EventArgs e)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = _pending = new CancellationTokenSource();
        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(true);
            Refresh();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>지금 활성 문서로 목록을 다시 만든다.</summary>
    public void Refresh()
    {
        _items.Clear();
        var tab = _watched;
        if (tab is null)
        {
            return;
        }

        foreach (var item in Build(tab.Text))
        {
            _items.Add(item);
        }
    }

    public static IReadOnlyList<OutlineItem> Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var items = new List<OutlineItem>();
        int line = 1, pos = 0;
        foreach (var s in StatementSplitter.Split(text, SplitOptions.Default))
        {
            var (title, delta) = Summarize(s.Text);
            int target = Math.Min(s.Range.Start + (text.AsSpan(Math.Min(s.Range.Start, text.Length)).StartsWith(s.Text, StringComparison.Ordinal) ? delta : 0), text.Length);
            for (; pos < target; pos++)
            {
                if (text[pos] == (char)10)
                {
                    line++;
                }
            }

            items.Add(new OutlineItem(title, line, target, s.Kind));
        }

        return items;
    }

    /// <summary>앞쪽 주석·빈 줄을 건너뛴 첫 줄을 제목으로, 그 줄 첫 글자까지의 거리를 함께 돌려준다.</summary>
    private static (string Title, int Delta) Summarize(string statementText)
    {
        int lineStart = 0;
        while (lineStart <= statementText.Length)
        {
            int nl = statementText.IndexOf((char)10, lineStart);
            int end = nl < 0 ? statementText.Length : nl;
            var raw = statementText.AsSpan(lineStart, end - lineStart);
            var trimmed = raw.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("--"))
            {
                var collapsed = string.Join(' ', trimmed.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var title = collapsed.Length <= 60 ? collapsed : collapsed[..60] + "…";
                return (title, lineStart + (raw.Length - raw.TrimStart().Length));
            }

            if (nl < 0)
            {
                break;
            }

            lineStart = nl + 1;
        }

        return ("(빈 문장)", 0);
    }
}
