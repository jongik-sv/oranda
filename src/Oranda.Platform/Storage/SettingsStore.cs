using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>
/// settings.json 읽기·쓰기(data-model 2.1). 읽을 때 4.1 규칙(손상 백업·상향 변환·미래 버전 읽기 전용)을 따르고,
/// 저장하면 바뀐 섹션마다 <see cref="SettingsChanged"/> 를 발행한다. 소유: TSK-03-01.
/// </summary>
[OrandaService(typeof(ISettingsStore))]
internal sealed class SettingsStore : ISettingsStore, IDisposable
{
    private readonly IAppDataPaths _paths;
    private readonly IEventBus _events;
    private readonly IEnumerable<ISettingsMigration> _migrations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _current;
    private bool _readOnly;

    public SettingsStore(IAppDataPaths paths, IEventBus events, IEnumerable<ISettingsMigration> migrations)
    {
        _paths = paths;
        _events = events;
        _migrations = migrations;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await JsonDocumentFile.LoadAsync(
                _paths.SettingsFile,
                SettingsFileKinds.Settings,
                SchemaVersions.Settings,
                OrandaJsonContext.Default.AppSettings,
                () => new AppSettings(),
                _migrations,
                _paths.BackupDirectory,
                ct).ConfigureAwait(false);

            var value = Normalize(result.Value);
            _current = value;
            _readOnly = result.IsReadOnly;
            return new StoredDocument<AppSettings>(value, result.IsReadOnly) { Notice = result.Notice };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> changed;
        try
        {
            if (_current is null)
            {
                // 아직 읽지 않았다면 읽기 전용 여부를 먼저 안다.
                var loaded = await JsonDocumentFile.LoadAsync(
                    _paths.SettingsFile,
                    SettingsFileKinds.Settings,
                    SchemaVersions.Settings,
                    OrandaJsonContext.Default.AppSettings,
                    () => new AppSettings(),
                    _migrations,
                    _paths.BackupDirectory,
                    ct).ConfigureAwait(false);
                _current = Normalize(loaded.Value);
                _readOnly = loaded.IsReadOnly;
            }

            if (_readOnly)
            {
                return;
            }

            var normalized = Normalize(settings) with { SchemaVersion = SchemaVersions.Settings };
            changed = ChangedSections(_current, normalized);
            await JsonDocumentFile.WriteAsync(_paths.SettingsFile, normalized, OrandaJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
            _current = normalized;
        }
        finally
        {
            _gate.Release();
        }

        foreach (var section in changed)
        {
            _events.Publish(new SettingsChanged(section));
        }
    }

    /// <summary>data-model 2.1 에 적힌 범위로 맞춘다.</summary>
    internal static AppSettings Normalize(AppSettings s) => s with
    {
        Editor = s.Editor with
        {
            FontSize = Math.Clamp(s.Editor.FontSize, 8, 32),
            TabSize = Math.Clamp(s.Editor.TabSize, 1, 16),
        },
        Monitor = s.Monitor with { RefreshSeconds = Math.Clamp(s.Monitor.RefreshSeconds, 2, 60) },
        Workspace = s.Workspace with { AutosaveSeconds = Math.Clamp(s.Workspace.AutosaveSeconds, 1, 10) },
    };

    /// <summary>값이 달라진 섹션의 JSON 이름(<c>SettingsChanged.Section</c>).</summary>
    internal static IReadOnlyList<string> ChangedSections(AppSettings before, AppSettings after)
    {
        var list = new List<string>();
        void Add(bool different, string section)
        {
            if (different)
            {
                list.Add(section);
            }
        }

        Add(before.Ui != after.Ui, "ui");
        Add(before.Editor != after.Editor, "editor");
        Add(before.KeymapId != after.KeymapId, "keymapId");
        Add(before.FormatterProfileId != after.FormatterProfileId, "formatterProfileId");
        Add(before.Execution != after.Execution, "execution");
        Add(before.Grid != after.Grid, "grid");
        Add(before.History != after.History, "history");
        Add(before.MetadataCache != after.MetadataCache, "metadataCache");
        Add(before.Monitor != after.Monitor, "monitor");
        Add(before.Workspace != after.Workspace, "workspace");
        Add(before.SchemaBrowser != after.SchemaBrowser, "schemaBrowser");
        return list;
    }
}
