using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: .sql·작업 공간 파일 열기·저장(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
    /// <summary>파일에서 연 작업 공간의 ID: 이름 + 경로 해시. 같은 파일은 늘 같은 ID(다시 열면 그 저장본을 쓴다), 다른 파일과는 겹치지 않는다.</summary>
    internal static string FileWorkspaceId(string name, string fullPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..8].ToLowerInvariant();
        var slug = SlugOf(name);
        if (slug.StartsWith("workspace-", StringComparison.Ordinal) && !name.Contains("workspace", StringComparison.OrdinalIgnoreCase))
        {
            slug = "file"; // 한글 이름 등 ASCII 가 없으면 무작위 꼬리 대신 고정 접두어
        }

        slug = slug.Length > 55 ? slug[..55].TrimEnd('-') : slug;
        return slug + "-" + hash;
    }

    public async Task<SqlEditorViewModel?> OpenFileAsync(string? path, CancellationToken ct)
    {
        path ??= await Dialogs.PickOpenFileAsync(ct).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var already = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (already is not null)
        {
            Active = already;
            return already;
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(true);
        var (text, enc) = FileEncoding.Decode(bytes);
        IReadOnlyList<WorkspaceBind>? fileBinds = null;
        if (CarriesBinds(full))
        {
            // 끝의 바인드 블록은 편집기에 보이지 않고 그 값만 이 탭의 바인드 목록에 들어간다.
            (text, fileBinds) = SqlFileBinds.Split(text);
        }

        var vm = new SqlEditorViewModel(Guid.NewGuid(), text)
        {
            Title = Path.GetFileName(full),
            FilePath = full,
            Encoding = enc,
        };
        vm.Binds.Restore(fileBinds);
        vm.IsDirty = false;
        AddTab(EnsureWorkspace(), vm);
        return vm;
    }

    /// <summary>저장한다. 파일이 없는 탭은 「다른 이름으로」. 취소하면 false.</summary>
    public async Task<bool> SaveAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.FilePath is null)
        {
            return await SaveAsAsync(tab, ct).ConfigureAwait(true);
        }

        await WriteFileAsync(tab, tab.FilePath, ct).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> SaveAsAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var suggested = tab.FilePath is null ? SaveFileNames.SuggestSql(tab.Title) : Path.GetFileName(tab.FilePath);
        var path = await Dialogs.PickSaveFileAsync(suggested, ct).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        await WriteFileAsync(tab, Path.GetFullPath(path), ct).ConfigureAwait(true);
        tab.FilePath = Path.GetFullPath(path);
        tab.Title = Path.GetFileName(path);
        return true;
    }

    public async Task<bool> SaveAllAsync(CancellationToken ct)
    {
        foreach (var tab in Tabs.Where(t => t.IsDirty).ToList())
        {
            if (!await SaveAsync(tab, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 바인드 블록을 싣는 파일인가(.sql 만. MyBatis·Markdown 작업 공간 파일과 다른 확장자는 예전 그대로 쓴다).
    /// </summary>
    internal static bool CarriesBinds(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteFileAsync(SqlEditorViewModel tab, string path, CancellationToken ct)
    {
        // .sql 이면 이 탭이 참조하는 바인드의 지금 값을 끝에 블록으로 붙인다(값이 없으면 본문만 — 예전과 같은 바이트).
        // 편집기 글은 블록 없는 본문이라 몇 번을 저장해도 블록은 하나다.
        var text = CarriesBinds(path) ? SqlFileBinds.Append(tab.Text, tab.Binds.Snapshot(tab.ReferencedBindNames)) : tab.Text;
        var bytes = FileEncoding.Encode(text, tab.Encoding);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".oranda-tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(true);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            throw;
        }

        tab.MarkSaved();
    }

    /// <summary>
    /// 보고 있는 작업 공간의 탭을 Markdown 작업 공간 파일 하나로 저장한다. 처음이거나 MyBatis 에서 왔거나
    /// <paramref name="askPath"/> 면 경로를 묻는다.
    /// </summary>
    public async Task<bool> SaveWorkspaceFileAsync(bool askPath, CancellationToken ct)
    {
        var ws = EnsureWorkspace();
        var path = askPath || WorkspaceFilePath is null
            ? await Dialogs.PickWorkspaceFileAsync(save: true, SaveFileNames.SuggestWorkspace(ws.Name), ct).ConfigureAwait(true)
            : WorkspaceFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        path = WorkspaceFiles.SavePath(path); // 새 파일은 이름.workspace.md (D-028)
        var name = WorkspaceFiles.NameOf(path);
        var tabs = ws.Tabs.Select(t => new WorkspaceFileTab(t.Title, t.Text, t.ConnectionProfileId?.ToString()));
        await File.WriteAllTextAsync(path, WorkspaceFiles.WriteMarkdown(name, tabs), new UTF8Encoding(false), ct).ConfigureAwait(true);
        ws.SourcePath = Path.GetFullPath(path);
        ws.Name = name;
        OnPropertyChanged(nameof(WorkspaceName));
        foreach (var t in ws.Tabs.Where(t => t.FilePath is null))
        {
            t.IsDirty = false; // 파일 없는 탭은 작업 공간 파일에 들어갔다.
        }

        return true;
    }

    /// <summary>
    /// 작업 공간 파일(.md)이나 MyBatis 매퍼(.xml)를 새 작업 공간 하나로 연다(파일의 탭·문장 하나가 SQL 탭 하나).
    /// 같은 파일을 연 작업 공간이 이미 있으면 그리로 옮겨 간다. 읽은 결과(알림 포함)를 돌려주고, 취소하면 null.
    /// </summary>
    public async Task<WorkspaceFile?> OpenWorkspaceFileAsync(string? path, CancellationToken ct)
    {
        path ??= await Dialogs.PickWorkspaceFileAsync(save: false, string.Empty, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        if (Workspaces.FirstOrDefault(w => string.Equals(w.SourcePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            ActiveWorkspace = open;
            return new WorkspaceFile(open.Name, [], []);
        }

        var file = WorkspaceFiles.Load(full);

        // MyBatis 는 namespace(대개 com.example.UserMapper 처럼 길다) 대신 파일 이름을 작업 공간 이름으로 쓴다.
        var name = WorkspaceFiles.IsMyBatis(full) ? Path.GetFileNameWithoutExtension(full) : file.Name;
        var ws = OpenWorkspace(UniqueId(FileWorkspaceId(name, full)), name);
        ws.SourcePath = full;
        ActiveWorkspace = ws;
        SqlEditorViewModel? first = null;
        foreach (var t in file.Tabs)
        {
            var vm = NewDocument(t.Text);
            vm.Title = t.Title;
            if (Guid.TryParse(t.Connection, out var profile))
            {
                vm.ConnectionProfileId = profile;
            }

            vm.Binds.Restore(t.Binds);
            first ??= vm;
        }

        if (first is not null)
        {
            Active = first;
        }

        return file;
    }

    private string UniqueId(string id)
    {
        var candidate = id;
        for (int n = 2; Workspaces.Any(w => w.Id == candidate); n++)
        {
            candidate = id + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
