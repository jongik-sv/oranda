using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Templates;

/// <summary>단축키 설정 목록 한 줄: 명령 + 지금 키.</summary>
public sealed partial class KeymapRow(string commandId, string title, string? when, string keys, bool isCustom) : ObservableObject
{
    public string CommandId { get; } = commandId;

    public string Title { get; } = title;

    public string? When { get; } = when;

    [ObservableProperty]
    public partial string Keys { get; set; } = keys;

    [ObservableProperty]
    public partial bool IsCustom { get; set; } = isCustom;
}

/// <summary>
/// 단축키 설정 화면(<c>oranda.tools.keymap</c>)의 ViewModel. 프리셋 4종 전환, 명령별 키 바꾸기·지우기·되돌리기,
/// 충돌 경고, 저장(<c>{data}/keymaps/my-keys.json</c> + 설정의 <c>keymapId</c>).
/// </summary>
public sealed partial class KeymapEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<CommandDefinition> _commands;
    private readonly UserKeymapStore _store;
    private readonly SettingsService _settings;
    private readonly bool _macOS;
    private Keymap _baseKeymap;
    private Keymap _user;

    public KeymapEditorViewModel(IReadOnlyList<CommandDefinition> commands, UserKeymapStore store, SettingsService settings, Keymap? existingUser = null, bool macOS = false)
    {
        _commands = commands;
        _store = store;
        _settings = settings;
        _macOS = macOS;
        var current = settings.Current.KeymapId;
        string preset;
        if (KeymapLoader.PresetIds.Contains(current))
        {
            preset = current;
            _user = new Keymap { Id = UserKeymapStore.DefaultId, Name = "내 키맵", BasedOn = current };
        }
        else
        {
            preset = existingUser?.BasedOn is { } b && KeymapLoader.PresetIds.Contains(b) ? b : "default";
            _user = existingUser ?? new Keymap { Id = UserKeymapStore.DefaultId, Name = "내 키맵", BasedOn = preset };
        }

        _baseKeymap = KeymapLoader.LoadPreset(preset);
        SelectedPresetId = preset;
        Rebuild();
    }

    /// <summary>선택할 수 있는 프리셋 id.</summary>
    public IReadOnlyList<string> Presets { get; } = KeymapLoader.PresetIds;

    public ObservableCollection<KeymapRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string SelectedPresetId { get; set; } = "default";

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial KeymapRow? SelectedRow { get; set; }

    /// <summary>새로 줄 키(예: <c>Primary+Shift+F</c>, 연속 키는 공백). 쉼표로 여럿.</summary>
    [ObservableProperty]
    public partial string NewKeys { get; set; } = string.Empty;

    /// <summary>키 입력 오류 또는 충돌 경고. 없으면 빈 글자.</summary>
    [ObservableProperty]
    public partial string Warning { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>지금 사용자 키맵(저장 전 포함).</summary>
    public Keymap User => _user;

    /// <summary>바탕 프리셋에 사용자 덮어쓰기를 겹친 키맵.</summary>
    public Keymap Effective => KeymapComposer.Compose(_baseKeymap, _user);

    public bool HasWarning => Warning.Length > 0;

    partial void OnSelectedPresetIdChanged(string value)
    {
        if (value == _baseKeymap.Id)
        {
            return;
        }

        // 프리셋을 바꾸면 덮어쓰기는 유지하고 바탕만 바꾼다.
        _baseKeymap = KeymapLoader.LoadPreset(value);
        _user = _user with { BasedOn = value };
        Rebuild();
        RefreshWarning(null);
    }

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    /// <summary>선택한 줄의 키를 <see cref="NewKeys"/> 로 바꾼다. 형식이 틀리면 <see cref="Warning"/> 만 채우고 바꾸지 않는다.</summary>
    public bool Assign()
    {
        if (SelectedRow is not { } row)
        {
            Warning = "바꿀 명령을 먼저 고른다.";
            return false;
        }

        var keys = NewKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0)
        {
            Warning = "키를 입력한다. 지우려면 「지우기」.";
            return false;
        }

        foreach (var k in keys)
        {
            if (KeymapComposer.Validate(k) is { } error)
            {
                Warning = $"{k}: {error}";
                return false;
            }
        }

        Apply(row, keys);
        return true;
    }

    /// <summary>선택한 명령의 키를 모두 지운다(바탕 키도 끈다).</summary>
    public void Clear()
    {
        if (SelectedRow is { } row)
        {
            Apply(row, []);
        }
    }

    /// <summary>선택한 명령을 프리셋 키로 되돌린다.</summary>
    public void ResetRow()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        _user = _user with { Bindings = _user.Bindings.Where(b => !(b.Command == row.CommandId && b.When == row.When)).ToList() };
        Rebuild();
        RefreshWarning(row.CommandId);
    }

    /// <summary>모든 덮어쓰기를 버리고 프리셋 그대로.</summary>
    public void ResetAll()
    {
        _user = _user with { Bindings = [] };
        Rebuild();
        RefreshWarning(null);
    }

    /// <summary>
    /// 저장한다. 덮어쓰기가 있으면 사용자 키맵 파일을 쓰고 <c>keymapId</c> 를 그 id 로, 없으면 프리셋 id 로 둔다.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_settings.IsReadOnly)
        {
            Status = "설정 파일이 새 버전이라 저장하지 않는다.";
            return;
        }

        string id;
        if (_user.Bindings.Count > 0)
        {
            await _store.SaveAsync(_user, ct).ConfigureAwait(true);
            id = _user.Id;
        }
        else
        {
            id = SelectedPresetId;
        }

        await _settings.UpdateAsync(s => s with { KeymapId = id }, ct).ConfigureAwait(true);
        Status = "저장했다.";
    }

    private void Apply(KeymapRow row, IReadOnlyList<string> keys)
    {
        _user = KeymapComposer.SetKeys(_user, _baseKeymap, row.CommandId, row.When, keys);
        Rebuild();
        RefreshWarning(row.CommandId);
    }

    private void RefreshWarning(string? command)
    {
        var conflicts = KeymapComposer.FindConflicts(Effective, _macOS)
            .Where(c => command is null || c.Commands.Contains(command))
            .ToList();
        Warning = conflicts.Count == 0
            ? string.Empty
            : "충돌: " + string.Join("; ", conflicts.Select(c => $"{c.Keys}{(c.When is null ? string.Empty : " [" + c.When + "]")} = {string.Join(", ", c.Commands)}"))
              + " (뒤에 적힌 바인딩이 이긴다)";
    }

    private void Rebuild()
    {
        var selected = SelectedRow?.CommandId;
        var effective = Effective;
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Rows.Clear();
        var shown = new HashSet<(string, string?)>();
        foreach (var b in effective.Bindings)
        {
            AddRow(b.Command, b.When, string.Join(", ", b.KeysFor(_macOS)), words, shown);
        }

        // 키가 없는 명령도 키를 줄 수 있게 보인다.
        foreach (var c in _commands.Where(c => !effective.Bindings.Any(b => b.Command == c.Id)))
        {
            AddRow(c.Id, c.When, string.Empty, words, shown);
        }

        SelectedRow = Rows.FirstOrDefault(r => r.CommandId == selected);
    }

    private void AddRow(string command, string? when, string keys, string[] words, HashSet<(string, string?)> shown)
    {
        if (!shown.Add((command, when)))
        {
            return;
        }

        var title = _commands.FirstOrDefault(c => c.Id == command)?.Title ?? command;
        if (words.Length > 0 && !words.All(w => command.Contains(w, StringComparison.OrdinalIgnoreCase) || title.Contains(w, StringComparison.OrdinalIgnoreCase) || keys.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var custom = _user.Bindings.Any(b => b.Command == command && b.When == when);
        Rows.Add(new KeymapRow(command, title, when, keys, custom));
    }
}
