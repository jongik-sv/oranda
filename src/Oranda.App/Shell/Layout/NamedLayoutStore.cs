using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Storage;

namespace Oranda.App.Shell.Layout;

/// <summary>
/// 이름 붙인 레이아웃(Toad 의 Desktop Layout, PL/SQL Developer 의 Layout 저장): <c>{data}/layouts/{id}.json</c>.
/// 형식은 <c>layout.json</c>(<see cref="LayoutDocument"/>)과 같고 <see cref="LayoutDocument.Name"/> 이 들어간다. 창 위치는 싣지 않는다.
/// 이름으로 찾는다(파일 이름은 이름에서 만든 ID 라 한글 이름도 같은 파일로 덮어쓴다).
/// </summary>
[OrandaService(typeof(NamedLayoutStore))]
public sealed class NamedLayoutStore(IAppDataPaths paths, IEnumerable<ISettingsMigration> migrations)
{
    public string Directory => Path.Combine(paths.DataDirectory, "layouts");

    /// <summary>저장된 레이아웃 이름(이름순). 읽지 못하는 파일은 건너뛴다.</summary>
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var (name, _) in await ReadAllAsync(ct).ConfigureAwait(false))
        {
            names.Add(name);
        }

        return [.. names.Order(StringComparer.CurrentCulture)];
    }

    /// <summary>같은 이름이 있으면 덮어쓴다.</summary>
    public async Task SaveAsync(string name, LayoutDocument layout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(layout);
        name = name.Trim();
        var path = (await FindAsync(name, ct).ConfigureAwait(false)) ?? NewPath(name);
        System.IO.Directory.CreateDirectory(Directory);
        await JsonDocumentFile.WriteAsync(
            path,
            layout with { Name = name, Window = null, SchemaVersion = SchemaVersions.Layout },
            ShellJsonContext.Default.LayoutDocument,
            ct).ConfigureAwait(false);
    }

    public async Task<LayoutDocument?> LoadAsync(string name, CancellationToken ct)
    {
        var path = await FindAsync(name, ct).ConfigureAwait(false);
        return path is null ? null : (await ReadAsync(path, ct).ConfigureAwait(false))?.Layout;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        if (await FindAsync(name, ct).ConfigureAwait(false) is not { } path)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private async Task<string?> FindAsync(string name, CancellationToken ct)
    {
        foreach (var (n, path) in await ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private async Task<List<(string Name, string Path)>> ReadAllAsync(CancellationToken ct)
    {
        var all = new List<(string, string)>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return all;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (await ReadAsync(path, ct).ConfigureAwait(false) is { Layout.Root: not null } read)
            {
                all.Add((read.Layout.Name ?? Path.GetFileNameWithoutExtension(path), path));
            }
        }

        return all;
    }

    private async Task<(LayoutDocument Layout, bool ReadOnly)?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            // layout.json 과 같은 버전 변환(1 → 2 등)을 거친다.
            var result = await JsonDocumentFile.LoadAsync(
                path,
                SettingsFileKinds.Layout,
                SchemaVersions.Layout,
                ShellJsonContext.Default.LayoutDocument,
                () => new LayoutDocument(),
                migrations,
                paths.BackupDirectory,
                ct).ConfigureAwait(false);
            return (result.Value, result.IsReadOnly);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string NewPath(string name)
    {
        var id = Features.SqlEditor.SqlEditorHost.SlugOf(name);
        var path = Path.Combine(Directory, id + ".json");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(Directory, id + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
        }

        return path;
    }
}
