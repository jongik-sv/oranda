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

    public string Display => Profile.Name + (IsBuiltIn ? " (기본 제공)" : string.Empty) + (IsDefault ? " ★" : string.Empty);

    partial void OnIsDefaultChanged(bool value) => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// <c>oranda.panel.formatterProfiles</c> — 포매터 프로필 목록·옵션 편집·예제 SQL 즉시 미리보기·기본 지정·내보내기/가져오기(TSK-05-03).
/// 옵션 행은 <see cref="FormatterOptionSchema"/>(FormatterOptions 계약)에서 만든다. 서비스는 처음 쓸 때 꺼낸다
/// (시작 경로에서 Language 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.formatterProfiles", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class FormatterProfilesViewModel : ToolPanelViewModelBase
{
    public const string DefaultSampleSql =
        "select e.employee_id, e.first_name||' '||e.last_name as full_name, d.department_name, case when e.salary>10000 then 'high' else 'low' end grade\n"
        + "from employees e join departments d on d.department_id=e.department_id\n"
        + "where e.salary>5000 and d.location_id in (1700,1800,1900,2400,2500,2700) -- 주요 지역\n"
        + "order by e.salary desc";

    private readonly IServiceProvider? _services;
    private IFormatterProfileStore? _store;
    private ISqlFormatter? _formatter;
    private ISettingsStore? _settings;
    private JsonObject _options = new();
    private bool _loading;

    public FormatterProfilesViewModel()
    {
    }

    public FormatterProfilesViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 서비스를 직접 넣는다.</summary>
    internal FormatterProfilesViewModel(IFormatterProfileStore store, ISqlFormatter formatter, ISettingsStore? settings = null)
    {
        _store = store;
        _formatter = formatter;
        _settings = settings;
    }

    public ObservableCollection<ProfileEntry> Profiles { get; } = [];

    public ObservableCollection<OptionRow> Rows { get; } = [];

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

    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>선택한 프로필의 지금 편집 중인 옵션.</summary>
    public FormatterOptions CurrentOptions => ToOptions(_options);

    public string DefaultProfileId { get; private set; } = FormatterIds.Default;

    partial void OnSelectedChanged(ProfileEntry? value) => LoadSelected();

    partial void OnSampleSqlChanged(string value) => RefreshPreview();

    partial void OnProfileNameChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
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
        }

        await WriteDefaultAsync(cur.Profile.Id, ct).ConfigureAwait(true);
        foreach (var e in Profiles)
        {
            e.IsDefault = e.Profile.Id == DefaultProfileId;
        }

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

    private IFormatterProfileStore? Store => _store ??= _services?.GetService(typeof(IFormatterProfileStore)) as IFormatterProfileStore;

    private ISettingsStore? Settings => _settings ??= _services?.GetService(typeof(ISettingsStore)) as ISettingsStore;

    private ISqlFormatter? Formatter => _formatter ??= _services?.GetService(typeof(ISqlFormatter)) as ISqlFormatter;

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
        Profiles.Clear();
        foreach (var p in list)
        {
            Profiles.Add(new ProfileEntry(p, IsBuiltInId(p.Id), p.Id == DefaultProfileId));
        }

        Selected = Profiles.FirstOrDefault(e => e.Profile.Id == selectId) ?? Profiles.FirstOrDefault();
        LoadSelected(); // 같은 항목을 다시 골라도 편집 내용을 되돌린다.
        return Task.CompletedTask;
    }

    private void LoadSelected()
    {
        _loading = true;
        try
        {
            Rows.Clear();
            if (Selected is not { } cur)
            {
                IsEditable = false;
                ProfileName = string.Empty;
                PreviewText = string.Empty;
                return;
            }

            IsEditable = !cur.IsBuiltIn;
            ProfileName = cur.Profile.Name;
            var profileNode = JsonSerializer.SerializeToNode(cur.Profile, OrandaJsonContext.Default.FormatterProfile) as JsonObject;
            _options = profileNode?["options"] as JsonObject ?? new JsonObject();
            foreach (var d in FormatterOptionSchema.All)
            {
                var row = new OptionRow(d, IsEditable);
                row.Load(FormatterOptionSchema.Get(_options, d.Path));
                row.Changed += OnRowChanged;
                Rows.Add(row);
            }

            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }

        RefreshPreview();
    }

    private void OnRowChanged(OptionRow row)
    {
        if (_loading)
        {
            return;
        }

        FormatterOptionSchema.Set(_options, row.Path, row.ToNode());
        IsDirty = true;
        RefreshPreview();
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
    }

    private static FormatterOptions ToOptions(JsonObject options)
    {
        var wrapper = new JsonObject { ["schemaVersion"] = 1, ["id"] = "x", ["name"] = "x", ["options"] = options.DeepClone() };
        return wrapper.Deserialize(OrandaJsonContext.Default.FormatterProfile)?.Options ?? new FormatterOptions();
    }

    private static bool IsBuiltInId(string id) => id is FormatterIds.Default or FormatterIds.Orange5 or FormatterIds.Compact;

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
}
