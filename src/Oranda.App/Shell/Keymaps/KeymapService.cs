using System;
using System.Diagnostics;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 지금 쓰는 키맵(<see cref="SettingsService"/> 의 <c>keymapId</c>)을 키 조회표로 들고 있는다. 설정이 바뀌면 다시 만든다.
/// 사용자 키맵 겹치기·편집은 TSK-04-07 이 이 자리에 얹는다. 모르는 프리셋 id 는 <c>default</c> 로 대신한다.
/// </summary>
public sealed class KeymapService : IDisposable
{
    private readonly Settings.SettingsService _settings;
    private readonly bool _macOS;
    private readonly IDisposable _subscription;
    private KeyBindingTable _table = KeyBindingTable.Empty;

    public KeymapService(Settings.SettingsService settings, IEventBus events, bool? macOS = null)
    {
        _settings = settings;
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
            keymap = KeymapLoader.LoadPreset(id);
        }
        catch (ArgumentException)
        {
            Trace.TraceWarning("키맵 '{0}' 을(를) 찾지 못해 default 로 대신한다.", id);
            id = "default";
            keymap = KeymapLoader.LoadPreset(id);
        }

        ActiveKeymapId = id;
        _table = KeyBindingTable.Build(keymap, _macOS);
    }

    public void Dispose() => _subscription.Dispose();
}
