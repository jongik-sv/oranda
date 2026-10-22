using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.FormatterProfiles;

/// <summary>목록에 보이는 프로필 한 줄.</summary>
public sealed partial class ProfileEntry(FormatterProfile profile, bool isBuiltIn, bool isDefault) : ObservableObject
{
    public FormatterProfile Profile { get; } = profile;

    public bool IsBuiltIn { get; } = isBuiltIn;

    [ObservableProperty]
    private bool _isDefault = isDefault;

    public string Name => Profile.Name;

    public string Display => Profile.Name + (IsBuiltIn ? " (기본 제공)" : string.Empty) + (IsDefault ? " ★" : string.Empty);

    /// <summary>목록 아이콘: 기본 제공 = 자물쇠, 내 프로필 = 포맷.</summary>
    public string IconKey => IsBuiltIn ? "lock" : "format";

    /// <summary>기본 프로필이면 ★ 아이콘 키, 아니면 null.</summary>
    public string? StarKey => IsDefault ? "star" : null;

    /// <summary>목록 툴팁.</summary>
    public string Tip => (IsBuiltIn ? "기본 제공 — 고칠 수 없습니다. 복사해서 고치세요" : "내 프로필") + (IsDefault ? " · ★ 기본 프로필(편집기 포맷이 씀)" : string.Empty);

    partial void OnIsDefaultChanged(bool value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(StarKey));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>프로필 목록의 묶음 머리(「기본 제공」·「내 프로필」). 고를 수 없다.</summary>
public sealed record ProfileGroupHeader(string Title)
{
    /// <summary>「기본 제공」 머리(화면은 이 값으로 고정 글을 고른다 — 번역기가 바인딩 글을 덮지 않게).</summary>
    public bool IsBuiltIn => Title == FormatterProfilesViewModel.BuiltInHeader;
}

/// <summary>프로필 목록의 안내 줄(내 프로필이 없을 때). 고를 수 없다.</summary>
public sealed record ProfileListHint(string Text);

/// <summary>
/// <c>oranda.panel.formatterProfiles</c> — 포매터 프로필 목록·옵션 편집·예제 SQL 즉시 미리보기·기본 지정·내보내기/가져오기(TSK-05-03).
/// 화면(TSK-13-42): 기본 제공/내 프로필 두 묶음 목록, 아이콘 도구 막대, 「복사해서 고치기」 안내 띠, 테두리 묶음 옵션(3·2·1단),
/// 기본과 다른 옵션(파란 점)·저장 안 한 변경(주황 점), 아래 미리보기, 상태 줄 「옵션 26개 중 N개가 기본과 다름」.
/// 옵션 행은 <see cref="FormatterOptionSchema"/>(FormatterOptions 계약)에서 한 번 만들고, 프로필을 고르면 값만 다시 읽는다.
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Language 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.formatterProfiles", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class FormatterProfilesViewModel : ToolPanelViewModelBase
{
    /// <summary>예제 SQL(목록·별칭·CASE·JOIN·AND·IN 목록·주석·GROUP BY·ORDER BY 가 다 보인다).</summary>
    public const string DefaultSampleSql =
        "select d.department_name, e.job_id, count(*) cnt, sum(e.salary) as total_sal, case when avg(e.salary)>10000 then 'high' else 'low' end grade\n"
        + "from employees e join departments d on d.department_id=e.department_id left join locations l on l.location_id=d.location_id\n"
        + "where e.hire_date>=date '2020-01-01' and l.country_id in ('KR','US','JP','DE','FR') -- 주요 국가\n"
        + "group by d.department_name, e.job_id\n"
        + "order by total_sal desc, d.department_name";

    /// <summary>목록 묶음 머리 글.</summary>
    public const string BuiltInHeader = "기본 제공";

    /// <summary>목록 묶음 머리 글.</summary>
    public const string MineHeader = "내 프로필";

    /// <summary>내 프로필이 없을 때 목록 안내.</summary>
    public const string NoMineHint = "없음 — 복사하거나 새로 만드세요";

    private readonly IServiceProvider? _services;
    private IFormatterProfileStore? _store;
    private ISqlFormatter? _formatter;
    private ISettingsStore? _settings;
    private JsonObject _options = new();
    private JsonObject _baseline = OptionsNode(new FormatterOptions());
    private bool _loading;

    public FormatterProfilesViewModel()
    {
        Rows = CreateRows();
    }

    public FormatterProfilesViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 서비스를 직접 넣는다.</summary>
    internal FormatterProfilesViewModel(IFormatterProfileStore store, ISqlFormatter formatter, ISettingsStore? settings = null)
        : this()
    {
        _store = store;
        _formatter = formatter;
        _settings = settings;
    }

    /// <summary>프로필(기본 제공 먼저, 그다음 내 프로필).</summary>
    public ObservableCollection<ProfileEntry> Profiles { get; } = [];

    /// <summary>목록에 보이는 줄: 묶음 머리(<see cref="ProfileGroupHeader"/>) + 프로필(<see cref="ProfileEntry"/>).</summary>
    public ObservableCollection<object> ProfileItems { get; } = [];

    /// <summary>옵션 행 26개(선언 순서). 프로필을 바꿔도 같은 행이다.</summary>
    public ObservableCollection<OptionRow> Rows { get; }

    [ObservableProperty]
    private ProfileEntry? _selected;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _sampleSql = DefaultSampleSql;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [NotifyPropertyChangedFor(nameof(CanRename), nameof(CanDelete), nameof(CanSave), nameof(CanRevert))]
    [ObservableProperty]
    private bool _isEditable;

    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanRevert))]
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>상태 줄 글: 요약 + 마지막 알림.</summary>
    public string StatusLine => string.IsNullOrEmpty(StatusText) ? SummaryText : SummaryText + "  —  " + StatusText;

    /// <summary>예제 SQL 고치기 칸을 보인다.</summary>
    [ObservableProperty]
    private bool _isSampleEditing;

    /// <summary>기본과 다른 옵션 수(기준 = 기본 제공 「Oranda 기본」).</summary>
    [ObservableProperty]
    private int _changedCount;

    /// <summary>저장 안 한 변경 수(옵션 + 이름).</summary>
    [ObservableProperty]
    private int _dirtyCount;

    /// <summary>상태 줄 왼쪽: 「이름 · 종류 · 옵션 26개 중 N개가 기본과 다름 · 저장 안 한 변경 M개」.</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>미리보기 머리: 「예제 SQL 5줄 → 결과 21줄」.</summary>
    [ObservableProperty]
    private string _previewInfo = string.Empty;

    /// <summary>선택한 프로필의 지금 편집 중인 옵션.</summary>
    public FormatterOptions CurrentOptions => ToOptions(_options);

    public string DefaultProfileId { get; private set; } = FormatterIds.Default;

    /// <summary>목록의 고른 줄(묶음 머리는 고를 수 없어 무시한다).</summary>
    public object? SelectedItem
    {
        get => Selected;
        set
        {
            // 묶음 머리·안내 줄과 목록을 다시 채울 때 오는 null 은 무시한다.
            if (value is ProfileEntry entry)
            {
                Selected = entry;
            }
        }
    }

    /// <summary>기본 제공 프로필을 골랐다(안내 띠를 보인다).</summary>
    public bool IsBuiltInSelected => Selected is { IsBuiltIn: true };

    /// <summary>내 프로필을 골랐다(이름 칸을 보인다).</summary>
    public bool IsMineSelected => Selected is { IsBuiltIn: false };

    /// <summary>고른 프로필이 기본 프로필(★)이다.</summary>
    public bool IsDefaultSelected => Selected is { IsDefault: true };

    public bool CanRename => IsEditable;

    public bool CanDelete => IsEditable;

    public bool CanSave => IsEditable && IsDirty;

    public bool CanRevert => IsEditable && IsDirty;

    public bool CanCopy => Selected is not null;

    public bool CanExport => Selected is not null;

    public bool CanSetDefault => Selected is { IsDefault: false };

    /// <summary>머리 줄의 「저장 안 한 변경 N개」(없으면 빈 글).</summary>
    public string DirtyText => DirtyCount > 0 ? $"저장 안 한 변경 {DirtyCount}개" : string.Empty;

    public bool HasDirty => DirtyCount > 0;

    partial void OnSelectedChanged(ProfileEntry? value)
    {
        LoadSelected();
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(IsBuiltInSelected));
        OnPropertyChanged(nameof(IsMineSelected));
        OnPropertyChanged(nameof(IsDefaultSelected));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanSetDefault));
    }

    partial void OnSampleSqlChanged(string value) => RefreshPreview();

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnSummaryTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    partial void OnProfileNameChanged(string value)
    {
        if (!_loading)
        {
            UpdateDirty();
        }
    }

    partial void OnDirtyCountChanged(int value)
    {
        OnPropertyChanged(nameof(DirtyText));
        OnPropertyChanged(nameof(HasDirty));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (Store is not { } store)
        {
            return;
        }

        var list = await store.ListAsync(ct).ConfigureAwait(true);
        if (Settings is { } settings)
        {
            DefaultProfileId = (await settings.LoadAsync(ct).ConfigureAwait(true)).Value.FormatterProfileId;
        }

        await ReloadListAsync(list, Selected?.Profile.Id ?? DefaultProfileId).ConfigureAwait(true);
    }

    /// <summary>「Oranda 기본」 값에서 시작하는 새 사용자 프로필을 만든다.</summary>
    public async Task NewAsync(CancellationToken ct = default)
    {
        if (Store is not { } store)
        {
            return;
        }

        var all = await store.ListAsync(ct).ConfigureAwait(true);
        var name = UniqueName(all, "새 프로필");
        var id = UniqueId(all, "custom");
        var options = all.FirstOrDefault(p => p.Id == FormatterIds.Default)?.Options ?? new FormatterOptions();
        await store.SaveAsync(new FormatterProfile { Id = id, Name = name, Options = options }, ct).ConfigureAwait(true);
        await ReloadListAsync(await store.ListAsync(ct).ConfigureAwait(true), id).ConfigureAwait(true);
        StatusText = $"'{name}' 을(를) 만들었다(Oranda 기본 값에서).";
    }

    /// <summary>선택한 프로필을 복사해 새 사용자 프로필로 만든다(기본 제공은 이렇게만 고칠 수 있다).</summary>
    public async Task CopySelectedAsync(CancellationToken ct = default)
    {
        if (Store is not { } store || Selected is not { } cur)
        {
            return;
        }

        var all = await store.ListAsync(ct).ConfigureAwait(true);
        var name = UniqueName(all, ProfileName.Trim().Length > 0 ? ProfileName.Trim() + " 복사본" : "새 프로필");
        var id = UniqueId(all, "custom");
        var copy = new FormatterProfile { Id = id, Name = name, Options = CurrentOptions };
        await store.SaveAsync(copy, ct).ConfigureAwait(true);
        await ReloadListAsync(await store.ListAsync(ct).ConfigureAwait(true), id).ConfigureAwait(true);
        StatusText = $"'{cur.Profile.Name}' 을(를) 복사해 '{name}' 을(를) 만들었다.";
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (Store is not { } store || Selected is not { IsBuiltIn: false } cur)
        {
            return;
        }

        var name = ProfileName.Trim();
        if (name.Length == 0)
        {
            StatusText = "프로필 이름을 입력한다.";
            return;
        }

        try
        {
            await store.SaveAsync(cur.Profile with { Name = name, Options = CurrentOptions }, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
            return;
        }

        await ReloadListAsync(await store.ListAsync(ct).ConfigureAwait(true), cur.Profile.Id).ConfigureAwait(true);
        StatusText = $"'{name}' 을(를) 저장했다.";
    }

    /// <summary>저장하지 않은 변경(옵션·이름)을 버리고 저장한 값으로 되돌린다.</summary>
    public void Revert()
    {
        if (Selected is not { IsBuiltIn: false } cur || !IsDirty)
        {
            return;
        }

        LoadSelected();
        StatusText = $"'{cur.Profile.Name}' 을(를) 저장한 값으로 되돌렸다.";
    }

    /// <summary>이름 칸만 저장한 이름으로 되돌린다(이름 고치기 취소).</summary>
    public void RevertName()
    {
        if (Selected is { } cur)
        {
            ProfileName = cur.Profile.Name;
        }
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        if (Store is not { } store || Selected is not { IsBuiltIn: false } cur)
        {
            return;
        }

        await store.DeleteAsync(cur.Profile.Id, ct).ConfigureAwait(true);
        if (DefaultProfileId == cur.Profile.Id)
        {
            await WriteDefaultAsync(FormatterIds.Default, ct).ConfigureAwait(true);
        }

        await ReloadListAsync(await store.ListAsync(ct).ConfigureAwait(true), DefaultProfileId).ConfigureAwait(true);
        StatusText = $"'{cur.Profile.Name}' 을(를) 지웠다.";
    }

    /// <summary>선택한 프로필을 편집기 포맷 명령이 쓰는 기본 프로필로 지정한다.</summary>
    public async Task SetDefaultAsync(CancellationToken ct = default)
    {
        if (Selected is not { } cur)
        {
            return;
        }

        if (IsDirty && !cur.IsBuiltIn)
        {
            await SaveAsync(ct).ConfigureAwait(true);
            cur = Selected ?? cur;
        }

        await WriteDefaultAsync(cur.Profile.Id, ct).ConfigureAwait(true);
        foreach (var e in Profiles)
        {
            e.IsDefault = e.Profile.Id == DefaultProfileId;
        }

        OnPropertyChanged(nameof(IsDefaultSelected));
        OnPropertyChanged(nameof(CanSetDefault));
        UpdateSummary();
        StatusText = $"기본 프로필 = '{cur.Profile.Name}'.";
    }

    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        if (Store is not { } store || Selected is not { } cur)
        {
            return;
        }

        if (IsDirty && !cur.IsBuiltIn)
        {
            await SaveAsync(ct).ConfigureAwait(true);
        }

        await store.ExportAsync(cur.Profile.Id, path, ct).ConfigureAwait(true);
        StatusText = $"'{cur.Profile.Name}' 을(를) {path} 로 내보냈다.";
    }

    /// <summary>
    /// 파일을 가져온다. 이름·ID 가 겹치면 이름 뒤에 번호를 붙여 새 프로필로 저장하고(덮어쓰지 않는다),
    /// 이 버전이 모르는 옵션은 무시하고 알린다.
    /// </summary>
    public async Task ImportAsync(string path, CancellationToken ct = default)
    {
        if (Store is not { } store)
        {
            return;
        }

        IReadOnlyList<string> unknown = [];
        string? fileName = null;
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(true), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            unknown = FormatterOptionSchema.FindUnknown(root?["options"]);
            fileName = root?["name"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // 형식 오류는 저장소가 같은 이유로 거절한다.
        }

        FormatterProfile imported;
        try
        {
            imported = await store.ImportAsync(path, ct).ConfigureAwait(true);
        }
        catch (InvalidDataException ex)
        {
            StatusText = "가져오지 못했다: " + ex.Message;
            return;
        }

        await ReloadListAsync(await store.ListAsync(ct).ConfigureAwait(true), imported.Id).ConfigureAwait(true);
        var message = $"'{imported.Name}' 을(를) 가져왔다.";
        if (fileName is not null && !string.Equals(fileName, imported.Name, StringComparison.Ordinal))
        {
            message += $" (같은 이름이 있어 '{fileName}' → '{imported.Name}')";
        }

        if (unknown.Count > 0)
        {
            message += " 이 버전이 모르는 옵션은 무시했다: " + string.Join(", ", unknown);
        }

        StatusText = message;
    }

    /// <summary>예제 SQL 을 지금 옵션으로 포맷해 보여 준다(옵션을 바꾸는 즉시).</summary>
    public void RefreshPreview()
    {
        if (Selected is null || Formatter is not { } formatter)
        {
            return;
        }

        var result = formatter.Format(SampleSql, CurrentOptions);
        PreviewText = result.Succeeded
            ? result.Text
            : $"{SampleSql}\n\n-- 포맷하지 못했다(원문 표시): {FormatterProfilesCommands.Describe(result)}";
        PreviewInfo = $"예제 SQL {LineCount(SampleSql)}줄 → 결과 {LineCount(PreviewText)}줄 · 옵션을 바꾸면 바로 다시 그립니다";
    }

    private IFormatterProfileStore? Store => _store ??= _services?.GetService(typeof(IFormatterProfileStore)) as IFormatterProfileStore;

    private ISettingsStore? Settings => _settings ??= _services?.GetService(typeof(ISettingsStore)) as ISettingsStore;

    private ISqlFormatter? Formatter => _formatter ??= _services?.GetService(typeof(ISqlFormatter)) as ISqlFormatter;

    private ObservableCollection<OptionRow> CreateRows()
    {
        var rows = new ObservableCollection<OptionRow>();
        foreach (var d in FormatterOptionSchema.All)
        {
            var row = new OptionRow(d, false);
            row.Load(FormatterOptionSchema.Get(_baseline, d.Path));
            row.Changed += OnRowChanged;
            rows.Add(row);
        }

        return rows;
    }

    private async Task WriteDefaultAsync(string id, CancellationToken ct)
    {
        DefaultProfileId = id;
        if (Settings is { } settings)
        {
            var loaded = await settings.LoadAsync(ct).ConfigureAwait(true);
            await settings.SaveAsync(loaded.Value with { FormatterProfileId = id }, ct).ConfigureAwait(true);
        }
    }

    private Task ReloadListAsync(IReadOnlyList<FormatterProfile> list, string selectId)
    {
        _baseline = OptionsNode(list.FirstOrDefault(p => p.Id == FormatterIds.Default)?.Options ?? new FormatterOptions());
        Profiles.Clear();
        foreach (var p in list.Where(p => IsBuiltInId(p.Id)).Concat(list.Where(p => !IsBuiltInId(p.Id))))
        {
            Profiles.Add(new ProfileEntry(p, IsBuiltInId(p.Id), p.Id == DefaultProfileId));
        }

        ProfileItems.Clear();
        ProfileItems.Add(new ProfileGroupHeader(BuiltInHeader));
        foreach (var e in Profiles.Where(e => e.IsBuiltIn))
        {
            ProfileItems.Add(e);
        }

        ProfileItems.Add(new ProfileGroupHeader(MineHeader));
        foreach (var e in Profiles.Where(e => !e.IsBuiltIn))
        {
            ProfileItems.Add(e);
        }

        if (Profiles.All(e => e.IsBuiltIn))
        {
            ProfileItems.Add(new ProfileListHint(NoMineHint));
        }

        var target = Profiles.FirstOrDefault(e => e.Profile.Id == selectId) ?? Profiles.FirstOrDefault();
        if (ReferenceEquals(target, Selected))
        {
            LoadSelected(); // 같은 항목을 다시 골라도 편집 내용을 되돌린다.
        }
        else
        {
            Selected = target;
        }

        return Task.CompletedTask;
    }

    private void LoadSelected()
    {
        _loading = true;
        try
        {
            var cur = Selected;
            IsEditable = cur is { IsBuiltIn: false };
            ProfileName = cur?.Profile.Name ?? string.Empty;
            _options = cur is null ? OptionsNode(new FormatterOptions()) : OptionsNode(cur.Profile.Options);
            var saved = (JsonObject)_options.DeepClone();
            foreach (var row in Rows)
            {
                row.IsEditable = IsEditable;
                row.Load(FormatterOptionSchema.Get(_options, row.Path));
                row.SetReference(FormatterOptionSchema.Get(_baseline, row.Path), cur is null ? null : FormatterOptionSchema.Get(saved, row.Path));
            }

            if (cur is null)
            {
                PreviewText = string.Empty;
                PreviewInfo = string.Empty;
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateDirty();
        RefreshPreview();
    }

    private void OnRowChanged(OptionRow row)
    {
        if (_loading)
        {
            return;
        }

        FormatterOptionSchema.Set(_options, row.Path, row.ToNode());
        UpdateDirty();
        RefreshPreview();
    }

    private void UpdateApplicability()
    {
        var byPath = Rows.ToDictionary(r => r.Path, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.InapplicableReason = FormatterOptionSchema.InapplicableReason(row.Path, p => byPath.TryGetValue(p, out var r) ? r.ChoiceValue : null);
        }
    }

    private void UpdateDirty()
    {
        UpdateApplicability();
        var nameChanged = Selected is { IsBuiltIn: false } cur && !string.Equals(ProfileName.Trim(), cur.Profile.Name, StringComparison.Ordinal);
        DirtyCount = Rows.Count(r => r.IsDirty) + (nameChanged ? 1 : 0);
        IsDirty = DirtyCount > 0;
        ChangedCount = Rows.Count(r => r.IsChanged);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Selected is not { } cur)
        {
            SummaryText = string.Empty;
            return;
        }

        var kind = cur.IsBuiltIn ? "기본 제공(읽기 전용)" : "내 프로필";
        var star = cur.IsDefault ? " · ★ 기본 프로필" : string.Empty;
        var text = $"{cur.Profile.Name} · {kind}{star} · 옵션 {Rows.Count}개 중 {ChangedCount}개가 기본과 다름";
        if (DirtyCount > 0)
        {
            text += $" · 저장 안 한 변경 {DirtyCount}개";
        }

        SummaryText = text;
    }

    private static int LineCount(string text) => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    private static JsonObject OptionsNode(FormatterOptions options)
    {
        var node = JsonSerializer.SerializeToNode(new FormatterProfile { Id = "x", Name = "x", Options = options }, OrandaJsonContext.Default.FormatterProfile) as JsonObject;
        return node?["options"]?.DeepClone() as JsonObject ?? new JsonObject();
    }

    private static FormatterOptions ToOptions(JsonObject options)
    {
        var wrapper = new JsonObject { ["schemaVersion"] = 1, ["id"] = "x", ["name"] = "x", ["options"] = options.DeepClone() };
        return wrapper.Deserialize(OrandaJsonContext.Default.FormatterProfile)?.Options ?? new FormatterOptions();
    }

    private static bool IsBuiltInId(string id) => FormatterIds.IsBuiltIn(id);

    private static string UniqueName(IReadOnlyList<FormatterProfile> all, string baseName)
    {
        var name = baseName;
        for (var n = 2; all.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
        {
            name = $"{baseName} ({n})";
        }

        return name;
    }

    private static string UniqueId(IReadOnlyList<FormatterProfile> all, string baseId)
    {
        var id = baseId;
        for (var n = 2; all.Any(p => p.Id == id); n++)
        {
            id = $"{baseId}-{n}";
        }

        return id;
    }
}

/// <summary>기본 제공 프로필 ID(data-model 2.3).</summary>
public static class FormatterIds
{
    public const string Default = "oranda-default";
    public const string Orange5 = "orange5";
    public const string Compact = "compact";

    /// <summary>설정 창 「SQL 포맷」 쪽이 기본 제공 프로필을 고칠 때 복사해 두는 사용자 프로필(TSK-13-38).</summary>
    public const string SettingsCopy = "sql-format";

    /// <summary>기본 제공(고칠 수 없는) 프로필인가.</summary>
    public static bool IsBuiltIn(string id) => id is Default or Orange5 or Compact;

    /// <summary>
    /// 설정의 기본 프로필(<c>formatterProfileId</c>) 옵션. 그 프로필이 없으면 첫 프로필, 저장소가 없으면 「Oranda 기본」(<see cref="FormatterOptions.OrandaDefault"/>).
    /// 포맷 명령·테이블 레이아웃 창(쉼표 위치)이 쓴다.
    /// </summary>
    public static async Task<FormatterOptions> ActiveOptionsAsync(ISettingsStore? settings, IFormatterProfileStore? store, CancellationToken ct)
    {
        var id = Default;
        if (settings is not null)
        {
            id = (await settings.LoadAsync(ct).ConfigureAwait(true)).Value.FormatterProfileId;
        }

        if (store is null)
        {
            return FormatterOptions.OrandaDefault;
        }

        var all = await store.ListAsync(ct).ConfigureAwait(true);
        return (all.FirstOrDefault(p => p.Id == id) ?? (all.Count > 0 ? all[0] : null))?.Options ?? FormatterOptions.OrandaDefault;
    }
}
