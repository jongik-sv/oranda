using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Storage;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 파일(<c>.plsql-workspace.md</c>, <see cref="PlsqlWorkspaceMarkdown"/>) 열기·저장 명령 쪽(B-3).
/// 접속 이름 ↔ 프로필 ID 는 접속 저장소(<see cref="IConnectionStore"/>)로 푼다: 접속 서비스·Oracle 어셈블리를 깨우지 않는다.
/// </summary>
public static class PlsqlWorkspaceFiles
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 파일을 작업 공간 문서로 연다. 같은 파일(<see cref="PlsqlWorkspaceViewModel.SourcePath"/>)에서 온 작업 공간이 열려 있으면 그것을 앞으로 낸다.
    /// 아니면 새 작업 공간으로 열어 앞으로 내고, 이미 열린 접속이 있으면 탭에 바로 붙인다. 읽지 못하면 알림을 띄운다(true).
    /// 셸의 라우터가 없거나 문서를 열 수 없으면 false.
    /// </summary>
    public static async Task<bool> OpenAsync(IServiceProvider? services, SqlEditorHost host, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (PlsqlWorkspaceRouter.Of(services) is not { } router)
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        if (ShowOpen(router, host, full))
        {
            return true;
        }

        try
        {
            var text = await File.ReadAllTextAsync(full, ct).ConfigureAwait(true);
            var profiles = await LoadProfilesAsync(services, ct).ConfigureAwait(true);
            var ambiguous = new List<string>();
            var read = PlsqlWorkspaceMarkdown.Read(text, PlsqlWorkspaceMarkdown.NameFromPath(full), name =>
            {
                var same = profiles.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)).Take(2).ToList();
                if (same.Count > 1)
                {
                    ambiguous.Add(name); // 이름이 같은 프로필이 둘 이상이면 어느 쪽인지 알 수 없다: 접속 없음
                }

                return same.Count == 1 ? same[0].Id : null;
            });
            var file = read with { SourcePath = full, Tabs = [.. await Task.WhenAll(read.Tabs.Select(t => ReloadFileTabAsync(t, ct))).ConfigureAwait(true)] };
            if (ShowOpen(router, host, full))
            {
                return true; // 읽는 사이 같은 파일을 또 열었다(빠른 두 번 열기): 작업 공간은 하나만
            }

            if (router.Import(file) is not { } workspace)
            {
                return false;
            }

            router.Activate(workspace, focus: true); // Import 는 활성 문서를 바꾸지 않는다
            Persistence(services)?.AttachOpened(null); // 이미 열린 접속이 있으면 기다리지 않고 바로 붙인다
            host.NoteFileUsed(full, workspace: true);
            if (ambiguous.Count > 0)
            {
                await host.Dialogs.ShowMessageAsync("작업 공간 열기",
                    "같은 이름의 접속이 둘 이상이라 접속 없이 열었다: " + string.Join(", ", ambiguous.Distinct(StringComparer.Ordinal)), ct).ConfigureAwait(true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            await host.Dialogs.ShowMessageAsync("작업 공간 열기", "파일을 열지 못했다: " + ex.Message, ct).ConfigureAwait(true);
            return true;
        }
    }

    private static bool ShowOpen(PlsqlWorkspaceRouter router, SqlEditorHost host, string full)
    {
        if (router.Workspaces.FirstOrDefault(w => PlsqlFiles.Same(w.SourcePath, full)) is not { } existing)
        {
            return false;
        }

        router.Activate(existing, focus: true);
        host.NoteFileUsed(full, workspace: true);
        return true;
    }

    /// <summary>
    /// 파일에 묶인 탭(<c>file</c> 줄)을 연다: PL/SQL 소스 확장자이고 파일이 있으면 디스크에서 다시 읽어(인코딩 유지, 고치지 않은 상태) 파일 탭으로 묶고 md 글은 버린다.
    /// 그 밖의 확장자이거나 파일이 없으면 묶음을 떼고 md 글로 새 문서 탭을 만든다(제목 유지). 파일에 묶이지 않은 탭은 그대로.
    /// </summary>
    private static async Task<PlsqlTabFile> ReloadFileTabAsync(PlsqlTabFile tab, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tab.FilePath))
        {
            return tab;
        }

        if (!PlsqlFiles.IsPlsqlFile(tab.FilePath) || !File.Exists(tab.FilePath))
        {
            return tab with { FilePath = null, Encoding = null, IsDirty = false };
        }

        var (text, encoding) = FileEncoding.Decode(await File.ReadAllBytesAsync(tab.FilePath, ct).ConfigureAwait(true));
        return tab with { Text = text, Encoding = encoding, IsDirty = false, Caret = 0 };
    }

    /// <summary>
    /// 작업 공간을 파일로 저장한다. <see cref="PlsqlWorkspaceViewModel.SourcePath"/> 가 있으면 거기에 덮어쓰고, 없으면 <paramref name="host"/> 의 대화상자로 묻는다(취소하면 false).
    /// 저장하면 그 경로를 작업 공간이 기억한다. 쓰기는 임시 파일에 쓴 뒤 바꾸는 원자적 방식이고 UTF-8(BOM 없음)이다.
    /// </summary>
    public static async Task<bool> SaveAsync(IServiceProvider? services, SqlEditorHost host, PlsqlWorkspaceViewModel workspace, CancellationToken ct, bool saveAs = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(workspace);
        var path = (saveAs ? null : workspace.SourcePath)
            ?? await host.Dialogs.PickWorkspaceFileAsync(save: true, PlsqlWorkspaceMarkdown.SuggestFileName(workspace.SourceName ?? workspace.Name), ct).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        var full = Path.GetFullPath(WithExtension(path));
        var sameFile = PlsqlFiles.Same(workspace.SourcePath, full);
        try
        {
            var profiles = await LoadProfilesAsync(services, ct).ConfigureAwait(true);
            var md = PlsqlWorkspaceMarkdown.Write(workspace.Capture() with { Name = sameFile ? workspace.SourceName ?? workspace.Name : workspace.Name },
                id => profiles.FirstOrDefault(p => p.Id == id)?.Name ?? workspace.Tabs.Select(t => t.Session?.Profile).FirstOrDefault(p => p?.Id == id)?.Name);
            await WriteAtomicAsync(full, md, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await host.Dialogs.ShowMessageAsync("작업 공간 저장", $"'{Path.GetFileName(full)}' 을(를) 저장하지 못했다: {ex.Message}", ct).ConfigureAwait(true);
            return false;
        }

        workspace.SourcePath = full;
        if (!sameFile)
        {
            workspace.SourceName = null; // 새 파일에는 지금 이름을 쓴다
        }

        host.NoteFileUsed(full, workspace: true);
        return true;
    }

    /// <summary>저장 경로가 <c>.plsql-workspace.md</c> 로 끝나게 한다(<c>x.md</c> → <c>x.plsql-workspace.md</c>, 확장자 없는 이름은 뒤에 붙인다).</summary>
    internal static string WithExtension(string path)
    {
        if (PlsqlWorkspaceMarkdown.IsWorkspaceFile(path))
        {
            return path;
        }

        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] + PlsqlWorkspaceMarkdown.Extension : path + PlsqlWorkspaceMarkdown.Extension;
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".oranda-tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, Utf8NoBom.GetBytes(text), ct).ConfigureAwait(true);
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
    }

    private static async Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync(IServiceProvider? services, CancellationToken ct)
    {
        if (services?.GetService(typeof(IConnectionStore)) is not IConnectionStore store)
        {
            return [];
        }

        try
        {
            return (await store.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // 접속 목록을 못 읽어도 작업 공간은 연다(접속 없는 탭으로)
        }
    }

    private static PlsqlWorkspacePersistence? Persistence(IServiceProvider? services) =>
        (services?.GetService(typeof(IEnumerable<IStartupTask>)) as IEnumerable<IStartupTask>)?.OfType<PlsqlWorkspacePersistence>().FirstOrDefault();
}
