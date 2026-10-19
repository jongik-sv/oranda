using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.App.Shell.Layout;

/// <summary><c>layout.json</c> 읽기·쓰기(data-model 2.5).</summary>
public interface ILayoutStore
{
    /// <summary>파일이 없거나 손상이면 기본값(<c>Root = null</c>)이고, <c>Notice</c> 에 알릴 말이 있다.</summary>
    Task<StoredDocument<LayoutDocument>> LoadAsync(CancellationToken ct = default);

    /// <summary>읽기 전용(미래 버전) 파일이면 저장하지 않는다.</summary>
    Task SaveAsync(LayoutDocument layout, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class LayoutStore(IAppDataPaths paths, IEnumerable<ISettingsMigration> migrations) : ILayoutStore
{
    private bool _readOnly;

    public async Task<StoredDocument<LayoutDocument>> LoadAsync(CancellationToken ct = default)
    {
        var result = await JsonDocumentFile.LoadAsync(
            paths.LayoutFile,
            SettingsFileKinds.Layout,
            SchemaVersions.Layout,
            ShellJsonContext.Default.LayoutDocument,
            () => new LayoutDocument(),
            migrations,
            paths.BackupDirectory,
            ct).ConfigureAwait(false);
        _readOnly = result.IsReadOnly;
        return new StoredDocument<LayoutDocument>(result.Value, result.IsReadOnly) { Notice = result.Notice };
    }

    public async Task SaveAsync(LayoutDocument layout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (_readOnly)
        {
            return;
        }

        await JsonDocumentFile.WriteAsync(
            paths.LayoutFile,
            layout with { SchemaVersion = SchemaVersions.Layout },
            ShellJsonContext.Default.LayoutDocument,
            ct).ConfigureAwait(false);
    }
}
