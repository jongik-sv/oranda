using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Language.Snippets;

namespace Oranda.App.Features.Templates;

/// <summary>
/// <c>oranda.panel.templates</c> — 코드 조각·옵티마이저 힌트·내장 함수·의사 컬럼 목록과 미리보기, 사용자 조각 관리.
/// 목록은 종류별 묶음(<see cref="Groups"/>, 접기 가능, 묶음마다 개수)이고 위의 종류 단추(<see cref="KindFilter"/>)와 찾기 칸으로 거른다.
/// 「삽입」은 활성 SQL 편집기에 넣고(조각은 자리 표시자 탭 이동) 설명류는 예제를 그대로 넣는다.
/// 사용자 조각은 편집 대화상자(<see cref="ShowEditor"/>)로 만들고 고친다. 기본 제공 항목은 고칠 수 없고 복제만 된다.
/// 화면 글은 <see cref="IShellText"/> 의 언어를 따르고 언어가 바뀌면 다시 만든다.
/// </summary>
[OrandaToolPanel("oranda.panel.templates", ToolPanelKind.Tool, DockLocation.Right, Order = 20)]
public sealed partial class TemplatesViewModel : ToolPanelViewModelBase
{
    private static readonly SnippetKind[] KindOrder = [SnippetKind.Snippet, SnippetKind.Hint, SnippetKind.Function, SnippetKind.PseudoColumn];

    private readonly SnippetStore? _store;
    private readonly IShellText? _text;
    private readonly List<Snippet> _user = [];
    private readonly Dictionary<SnippetKind, TemplateGroupRow> _groups = [];
    private bool _syncing;

    public TemplatesViewModel()
        : this(null, null)
    {
    }

    public TemplatesViewModel(IAppDataPaths? paths, IShellText? text = null)
    {
        _store = paths is null ? null : new SnippetStore(paths);
        _text = text;
        foreach (var kind in KindOrder)
        {
            _groups[kind] = new TemplateGroupRow(kind);
        }

        if (text is not null)
        {
            text.LanguageChanged += (_, _) => Refresh();
        }

        Refresh();
    }

    /// <summary>쓰는 편집기 호스트(시험은 직접 준다).</summary>
    public SqlEditorHost Host { get; init; } = SqlEditorHost.Current;

    /// <summary>편집 대화상자를 띄우고 저장을 눌렀으면 true. View 가 넣는다(시험은 가짜를 넣는다).</summary>
    public Func<SnippetEditViewModel, Task<bool>>? ShowEditor { get; set; }

    /// <summary>거르고 찾은 항목(종류 순서). 묶음 목록 <see cref="Groups"/> 의 바탕이다.</summary>
    public ObservableCollection<Snippet> Items { get; } = [];

    /// <summary>항목이 있는 종류 묶음(조각, 힌트, 함수, 의사 열 순).</summary>
    public ObservableCollection<TemplateGroupRow> Groups { get; } = [];

    public IReadOnlyList<Snippet> UserSnippets => _user;

    /// <summary>지금 화면 언어(<c>ko</c> | <c>en</c>).</summary>
    public string Language => _text?.Language ?? UiText.Current?.Language ?? "ko";

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>종류 거르기. null = 전체.</summary>
    [ObservableProperty]
    public partial SnippetKind? KindFilter { get; set; }

    /// <summary>목록에서 고른 행(묶음 머리일 수도 있다).</summary>
    [ObservableProperty]
    public partial TemplateRow? SelectedRow { get; set; }

    /// <summary>고른 항목. 묶음 머리를 고르면 null.</summary>
    [ObservableProperty]
    public partial Snippet? Selected { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    // 종류 단추(라디오). 켜진 단추를 다시 눌러도 꺼지지 않는다(RadioButton).
    public bool IsAllKinds
    {
        get => KindFilter is null;
        set => SetKind(value, null);
    }

    public bool IsSnippetKind
    {
        get => KindFilter == SnippetKind.Snippet;
        set => SetKind(value, SnippetKind.Snippet);
    }

    public bool IsHintKind
    {
        get => KindFilter == SnippetKind.Hint;
        set => SetKind(value, SnippetKind.Hint);
    }

    public bool IsFunctionKind
    {
        get => KindFilter == SnippetKind.Function;
        set => SetKind(value, SnippetKind.Function);
    }

    public bool IsPseudoColumnKind
    {
        get => KindFilter == SnippetKind.PseudoColumn;
        set => SetKind(value, SnippetKind.PseudoColumn);
    }

    // 미리보기 칸.
    public bool HasSelection => Selected is not null;

    public bool IsUserSelected => Selected is { IsUser: true };

    public string PreviewTitle => Selected is { } s ? SnippetLibrary.DisplayName(s, Language) : string.Empty;

    public string PreviewDescription => Selected is { } s ? SnippetLibrary.DisplayDescription(s, Language) : string.Empty;

    /// <summary>미리보기에 보일 SQL(삽입될 글자, 자리 표시자는 기본값으로).</summary>
    public string PreviewText => Selected?.Example ?? string.Empty;

    /// <summary>아래 상태 줄: 알림이 있으면 알림, 없으면 사용법.</summary>
    public string Footer => Status.Length > 0
        ? Status
        : Selected is { Kind: SnippetKind.Snippet }
            ? Text("Templates.hint.snippet", "두 번 누르기·Enter: 삽입 · Tab: 다음 자리")
            : Text("Templates.hint", "두 번 누르기·Enter: 삽입 · 편집기로 끌어 놓기");

    partial void OnQueryChanged(string value)
    {
        // 찾을 때는 접어 둔 묶음도 편다(찾은 것이 숨지 않게).
        foreach (var g in _groups.Values)
        {
            g.IsExpanded = true;
        }

        Refresh();
    }

    partial void OnKindFilterChanged(SnippetKind? value)
    {
        OnPropertyChanged(nameof(IsAllKinds));
        OnPropertyChanged(nameof(IsSnippetKind));
        OnPropertyChanged(nameof(IsHintKind));
        OnPropertyChanged(nameof(IsFunctionKind));
        OnPropertyChanged(nameof(IsPseudoColumnKind));
        Refresh();
    }

    partial void OnSelectedRowChanged(TemplateRow? value)
    {
        if (!_syncing)
        {
            _syncing = true;
            Selected = (value as TemplateItemRow)?.Snippet;
            _syncing = false;
        }
    }

    partial void OnSelectedChanged(Snippet? value)
    {
        if (!_syncing)
        {
            _syncing = true;
            SelectedRow = value is null ? null : FindRow(value);
            _syncing = false;
        }

        Status = string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsUserSelected));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewDescription));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(Footer));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(Footer));

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

    /// <summary>묶음 머리를 접거나 편다.</summary>
    public static void Toggle(TemplateGroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.IsExpanded = !group.IsExpanded;
    }

    /// <summary>목록 첫 항목을 고른다(찾기 칸에서 아래 화살표·Enter). 없으면 false.</summary>
    public bool SelectFirst()
    {
        if (Selected is not null && Items.Contains(Selected))
        {
            return true;
        }

        Selected = Items.FirstOrDefault();
        return Selected is not null;
    }

    /// <summary>선택한 항목을 활성 편집기에 넣고 편집기로 초점을 옮긴다. 편집기가 없거나 읽기 전용이면 false.</summary>
    public bool InsertSelected()
    {
        if (Selected is not { } s)
        {
            return false;
        }

        if (Host.Active is not { } tab)
        {
            Status = Text("Templates.status.noEditor", "넣을 SQL 편집기가 없다.");
            return false;
        }

        if (!SnippetInserter.Insert(tab, s))
        {
            Status = Text("Templates.status.readOnly", "읽기 전용 문서다.");
            return false;
        }

        Status = string.Empty;
        tab.Surface?.FocusEditor();
        return true;
    }

    /// <summary>새 사용자 조각 편집 내용(빈 칸).</summary>
    public SnippetEditViewModel BeginNew() => NewEditor(Text("Templates.dialog.new", "새 조각"), null);

    /// <summary>선택한 사용자 조각을 고칠 편집 내용. 기본 제공 항목이면 알림만 하고 null.</summary>
    public SnippetEditViewModel? BeginEdit()
    {
        if (Selected is not { IsUser: true } s)
        {
            Status = Text("Templates.status.userOnly", "기본 제공 항목은 고치거나 지울 수 없다. 사용자 조각으로 복제해서 고친다.");
            return null;
        }

        var e = NewEditor(Text("Templates.dialog.edit", "조각 편집"), s);
        e.Prefix = s.Prefix;
        e.Name = s.Name;
        e.Description = s.Description;
        e.Body = s.Body;
        return e;
    }

    /// <summary>선택한 항목(기본 제공·사용자 모두)을 사용자 조각으로 복제할 편집 내용. 줄임말은 겹치지 않게 바꿔 준다.</summary>
    public SnippetEditViewModel? BeginDuplicate()
    {
        if (Selected is not { } s)
        {
            return null;
        }

        var e = NewEditor(Text("Templates.dialog.new", "새 조각"), null);
        e.Prefix = UniquePrefix(s.Prefix);
        e.Name = SnippetLibrary.DisplayName(s, Language);
        e.Description = SnippetLibrary.DisplayDescription(s, Language);
        e.Body = s.Body;
        return e;
    }

    /// <summary>편집 내용 검사: 줄임말(영문·숫자·밑줄), 본문, 다른 사용자 조각과 줄임말 중복. 문제가 없으면 null.</summary>
    public string? ValidateEdit(SnippetEditViewModel edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var prefix = edit.Prefix.Trim();
        if (!PrefixPattern().IsMatch(prefix))
        {
            return Text("Templates.error.prefix", "줄임말은 공백 없이 영문·숫자·밑줄로 쓴다.");
        }

        if (string.IsNullOrWhiteSpace(edit.Body))
        {
            return Text("Templates.error.body", "본문을 입력한다.");
        }

        var clash = _user.FirstOrDefault(u => !ReferenceEquals(u, edit.Original) && SamePrefix(u.Prefix, prefix));
        return clash is null ? null : Format(Text("Templates.error.duplicate", "같은 줄임말의 사용자 조각이 이미 있다: {0}"), clash.Prefix);
    }

    /// <summary>편집 내용을 검사해 저장한다(고치는 중이면 원래 조각을 바꾼다). 저장했으면 그 조각을 고르고 true.</summary>
    public async Task<bool> CommitAsync(SnippetEditViewModel edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit.Validate())
        {
            return false;
        }

        var snippet = edit.ToSnippet();
        var at = edit.Original is null ? -1 : _user.FindIndex(u => ReferenceEquals(u, edit.Original));
        if (at >= 0)
        {
            _user[at] = snippet;
        }
        else
        {
            _user.Add(snippet);
        }

        await SaveUserAsync(ct).ConfigureAwait(true);
        Refresh();
        Selected = snippet;
        Status = Format(Text("Templates.status.saved", "조각을 저장했다: {0}"), snippet.Prefix);
        return true;
    }

    /// <summary>새 조각 대화상자를 띄워 저장한다.</summary>
    public Task<bool> NewSnippetAsync(CancellationToken ct = default) => RunEditorAsync(BeginNew(), ct);

    /// <summary>선택한 사용자 조각을 대화상자로 고친다.</summary>
    public Task<bool> EditSelectedAsync(CancellationToken ct = default) => RunEditorAsync(BeginEdit(), ct);

    /// <summary>선택한 항목을 사용자 조각으로 복제한다(대화상자에서 고친 뒤 저장).</summary>
    public Task<bool> DuplicateSelectedAsync(CancellationToken ct = default) => RunEditorAsync(BeginDuplicate(), ct);

    /// <summary>선택한 사용자 조각을 지운다. 기본 제공 항목이면 알림만 한다.</summary>
    public async Task<bool> DeleteUserSnippetAsync(CancellationToken ct = default)
    {
        if (Selected is not { IsUser: true } s)
        {
            Status = Text("Templates.status.userOnly", "기본 제공 항목은 고치거나 지울 수 없다. 사용자 조각으로 복제해서 고친다.");
            return false;
        }

        _user.Remove(s);
        await SaveUserAsync(ct).ConfigureAwait(true);
        Refresh();
        Status = Format(Text("Templates.status.deleted", "조각을 지웠다: {0}"), s.Prefix);
        return true;
    }

    /// <summary>확인을 받고 선택한 사용자 조각을 지운다.</summary>
    public async Task<bool> ConfirmAndDeleteAsync(CancellationToken ct = default)
    {
        if (Selected is not { IsUser: true } s)
        {
            return await DeleteUserSnippetAsync(ct).ConfigureAwait(true);
        }

        var question = Format(Text("Templates.confirm.delete", "사용자 조각 {0} 을(를) 지울까요?"), s.Prefix);
        return await Host.Dialogs.ConfirmAsync(question, ct).ConfigureAwait(true) && await DeleteUserSnippetAsync(ct).ConfigureAwait(true);
    }

    private async Task<bool> RunEditorAsync(SnippetEditViewModel? edit, CancellationToken ct)
    {
        if (edit is null || ShowEditor is null || !await ShowEditor(edit).ConfigureAwait(true))
        {
            return false;
        }

        return await CommitAsync(edit, ct).ConfigureAwait(true);
    }

    private SnippetEditViewModel NewEditor(string title, Snippet? original) =>
        new(title, original, ValidateEdit, OverrideNote);

    /// <summary>줄임말이 기본 조각과 같으면 「대신한다」 알림(사용자 조각이 Tab 펼치기에서 이긴다).</summary>
    private string? OverrideNote(string prefix) =>
        SnippetLibrary.Snippets.FirstOrDefault(s => SamePrefix(s.Prefix, prefix)) is { } builtin
            ? Format(Text("Templates.note.override", "기본 조각 {0} 대신 이 조각이 Tab 으로 펼쳐진다."), builtin.Prefix)
            : null;

    /// <summary>기본 조각·사용자 조각과 겹치지 않는 줄임말(겹치면 _2, _3 …을 붙인다).</summary>
    private string UniquePrefix(string prefix)
    {
        var stem = new string([.. prefix.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')]);
        if (stem.Length == 0)
        {
            stem = "snippet";
        }

        bool Taken(string p) => _user.Exists(u => SamePrefix(u.Prefix, p)) || SnippetLibrary.Snippets.Any(s => SamePrefix(s.Prefix, p));
        if (!Taken(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = stem + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    private Task SaveUserAsync(CancellationToken ct) => _store is null ? Task.CompletedTask : _store.SaveAsync(_user, ct);

    private TemplateItemRow? FindRow(Snippet snippet) =>
        _groups.TryGetValue(snippet.Kind, out var g) ? g.Items.FirstOrDefault(r => ReferenceEquals(r.Snippet, snippet)) ?? g.Items.FirstOrDefault(r => r.Snippet == snippet) : null;

    private void Refresh()
    {
        var keep = Selected;
        var language = Language;
        var found = SnippetLibrary.Search(Query, KindFilter, _user);
        Items.Clear();
        Groups.Clear();
        foreach (var kind in KindOrder)
        {
            var group = _groups[kind];
            IEnumerable<Snippet> members = found.Where(s => s.Kind == kind);

            // 찾는 글이 없으면 사용자 조각을 먼저 보인다(찾을 때는 줄임말이 맞는 순서 그대로).
            if (Query.Trim().Length == 0)
            {
                members = members.OrderBy(s => s.IsUser ? 0 : 1);
            }

            group.Items.Clear();
            foreach (var s in members)
            {
                Items.Add(s);
                var name = SnippetLibrary.DisplayName(s, language);
                var description = SnippetLibrary.DisplayDescription(s, language);
                var tip = description.Length > 0 ? name + "\n" + description : name;
                group.Items.Add(new TemplateItemRow(s, SnippetLibrary.Summary(s, language), tip));
            }

            group.Title = KindName(kind);
            group.Count = group.Items.Count;
            if (group.Count > 0)
            {
                Groups.Add(group);
            }
        }

        // 고른 항목이 남아 있으면 그대로, 사라졌으면 찾는 중일 때만 첫 항목을 고른다(Enter 로 바로 넣게).
        var selected = keep is not null ? Items.FirstOrDefault(s => ReferenceEquals(s, keep)) ?? Items.FirstOrDefault(s => s == keep) : null;
        if (selected is null && Query.Trim().Length > 0)
        {
            selected = Items.FirstOrDefault();
        }

        _syncing = true;
        Selected = selected;
        SelectedRow = selected is null ? null : FindRow(selected);
        _syncing = false;

        // 언어가 바뀌어도 미리보기 머리글이 따라오게 한다.
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewDescription));
        OnPropertyChanged(nameof(Footer));
    }

    private string KindName(SnippetKind kind) => kind switch
    {
        SnippetKind.Snippet => Text("Templates.kind.snippet", "조각"),
        SnippetKind.Hint => Text("Templates.kind.hint", "힌트"),
        SnippetKind.Function => Text("Templates.kind.function", "함수"),
        _ => Text("Templates.kind.pseudoColumn", "의사 열"),
    };

    private void SetKind(bool on, SnippetKind? kind)
    {
        // 라디오 묶음에서 다른 단추가 켜질 때 들어오는 false 는 무시한다.
        if (on)
        {
            KindFilter = kind;
        }
    }

    private string Text(string key, string fallback) => _text?.TryLookup(key) ?? UiText.Get(key, fallback);

    private static string Format(string format, string value) => string.Format(CultureInfo.CurrentCulture, format, value);

    private static bool SamePrefix(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex PrefixPattern();
}
