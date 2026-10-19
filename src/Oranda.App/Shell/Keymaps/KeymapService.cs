using System;
using System.Diagnostics;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 지금 쓰는 키맵(<see cref="SettingsService"/> 의 <c>keymapId</c>)을 키 조회표로 들고 있는다. 설정이 바뀌면 다시 만든다.
/// 프리셋이 아닌 id 는 데이터 폴더의 사용자 키맵을 프리셋 위에 겹친다. 읽지 못하면 <c>default</c> 로 대신한다.
/// </summary>
public sealed class KeymapService : IDisposable
{
    private readonly Settings.SettingsService _settings;
    private readonly bool _macOS;
    private readonly IDisposable _subscription;
    private readonly Oranda.Core.Storage.IAppDataPaths? _paths;
    private KeyBindingTable _table = KeyBindingTable.Empty;

    public KeymapService(Settings.SettingsService settings, IEventBus events, bool? macOS = null, Oranda.Core.Storage.IAppDataPaths? paths = null)
    {
        _settings = settings;
        _paths = paths;
        _macOS = macOS ?? OperatingSystem.IsMacOS();
        _subscription = events.Subscribe<SettingsChanged>(this, e =>
        {
            if (e.Section == "keymapId")
            {
                Reload();
            }
        });
    }

    public KeyBindingTable Table => _table;

    public string ActiveKeymapId { get; private set; } = "default";

    public string? ShortcutText(string commandId) => _table.ShortcutText(commandId);

    /// <summary>설정의 키맵 id 로 조회표를 다시 만든다.</summary>
    public void Reload()
    {
        var id = _settings.Current.KeymapId;
        Keymap keymap;
        try
        {
            keymap = KeymapLoader.PresetIds.Contains(id) ? KeymapLoader.LoadPreset(id) : LoadUser(id);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.Text.Json.JsonException or FormatException)
        {
            Trace.TraceWarning("키맵 '{0}' 을(를) 읽지 못해 default 로 대신한다: {1}", id, ex.Message);
            id = "default";
            keymap = KeymapLoader.LoadPreset(id);
        }

        ActiveKeymapId = id;
        _table = KeyBindingTable.Build(keymap, _macOS);
    }

    /// <summary>
    /// 사용자 키맵(<c>keymaps/{id}.json</c>, 단축키 편집기가 저장)을 <c>basedOn</c> 프리셋 위에 겹친다(TSK-04-07 의 KeymapComposer).
    /// 파일이 없으면 <see cref="ArgumentException"/>.
    /// </summary>
    private Keymap LoadUser(string id)
    {
        var path = _paths is null ? null : System.IO.Path.Combine(_paths.KeymapDirectory, id + ".json");
        if (path is null || !System.IO.File.Exists(path))
        {
            throw new ArgumentException($"사용자 키맵 '{id}' 이(가) 없다.", nameof(id));
        }

        using var stream = System.IO.File.OpenRead(path);
        var user = KeymapLoader.Parse(stream);
        var baseId = user.BasedOn is { } b && KeymapLoader.PresetIds.Contains(b) ? b : "default";
        return Oranda.App.Features.Templates.KeymapComposer.Compose(KeymapLoader.LoadPreset(baseId), user);
    }

    public void Dispose() => _subscription.Dispose();
}
