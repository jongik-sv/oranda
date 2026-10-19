using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Language.Snippets;

namespace Oranda.App.Features.Templates;

/// <summary>
/// <c>oranda.panel.templates</c> — 코드 조각·옵티마이저 힌트·내장 함수·의사 컬럼 목록과 설명·예제, 사용자 조각 관리.
/// 「삽입」은 활성 SQL 편집기에 넣고(조각은 자리 표시자 탭 이동) 설명류는 예제를 그대로 넣는다.
/// </summary>
[OrandaToolPanel("oranda.panel.templates", ToolPanelKind.Tool, DockLocation.Right, Order = 20)]
public sealed partial class TemplatesViewModel : ToolPanelViewModelBase
{
    private readonly SnippetStore? _store;
    private readonly List<Snippet> _user = [];

    public TemplatesViewModel()
    {
        Refresh();
    }

    public TemplatesViewModel(IAppDataPaths paths)
    {
        _store = new SnippetStore(paths);
        Refresh();
    }

    /// <summary>쓰는 편집기 호스트(시험은 직접 준다).</summary>
    public SqlEditorHost Host { get; init; } = SqlEditorHost.Current;

    public ObservableCollection<Snippet> Items { get; } = [];

    /// <summary>종류 선택지. null = 전체.</summary>
    public IReadOnlyList<SnippetKind?> Kinds { get; } = [null, SnippetKind.Snippet, SnippetKind.Hint, SnippetKind.Function, SnippetKind.PseudoColumn];

    public IReadOnlyList<Snippet> UserSnippets => _user;

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SnippetKind? KindFilter { get; set; }

    [ObservableProperty]
    public partial Snippet? Selected { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    // 사용자 조각 입력.
    [ObservableProperty]
    public partial string EditPrefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditBody { get; set; } = string.Empty;

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnKindFilterChanged(SnippetKind? value) => Refresh();

    partial void OnSelectedChanged(Snippet? value)
    {
        Detail = value is null
            ? string.Empty
            : value.Kind == SnippetKind.Snippet
                ? $"{value.Name} ({value.Prefix})\n{value.Description}\n\n{value.Example}\n\n탭: 자리 이동, 마지막 자리에서 끝"
                : $"{value.Name}\n{value.Description}\n\n예:\n{value.Example}";
    }

    /// <summary>사용자 조각을 파일에서 읽는다(잘못된 파일은 알림만).</summary>
    public async Task LoadUserAsync(CancellationToken ct = default)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var list = await _store.LoadAsync(ct).ConfigureAwait(true);
            _user.Clear();
            _user.AddRange(list);
            Refresh();
        }
        catch (InvalidDataException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>입력한 줄임말·본문으로 사용자 조각을 더하거나(같은 줄임말이면 바꿔) 저장한다.</summary>
    public async Task AddUserSnippetAsync(CancellationToken ct = default)
    {
        var prefix = EditPrefix.Trim();
        if (prefix.Length == 0 || EditBody.Length == 0 || prefix.Any(char.IsWhiteSpace))
        {
            Status = "줄임말(공백 없이)과 본문을 입력한다.";
            return;
        }

        _user.RemoveAll(s => string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        _user.Add(new Snippet(prefix, EditName.Length > 0 ? EditName : prefix, string.Empty, EditBody, SnippetKind.Snippet, IsUser: true));
        await SaveUserAsync(ct).ConfigureAwait(true);
        Refresh();
        Status = "조각 저장: " + prefix;
    }

    /// <summary>선택한 사용자 조각을 지운다. 기본 조각이면 아무것도 안 한다.</summary>
    public async Task DeleteUserSnippetAsync(CancellationToken ct = default)
    {
        if (Selected is not { IsUser: true } s)
        {
            Status = "사용자 조각만 지울 수 있다.";
            return;
        }

        _user.Remove(s);
        await SaveUserAsync(ct).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>선택한 항목을 활성 편집기에 넣는다. 편집기가 없거나 읽기 전용이면 false.</summary>
    public bool InsertSelected()
    {
        if (Selected is not { } s || Host.Active is not { } tab)
        {
            Status = "넣을 편집기가 없다.";
            return false;
        }

        var ok = SnippetInserter.Insert(tab, s);
        Status = ok ? string.Empty : "읽기 전용 문서다.";
        return ok;
    }

    private Task SaveUserAsync(CancellationToken ct) => _store is null ? Task.CompletedTask : _store.SaveAsync(_user, ct);

    private void Refresh()
    {
        Items.Clear();
        foreach (var s in SnippetLibrary.Search(Query, KindFilter, _user))
        {
            Items.Add(s);
        }
    }
}
