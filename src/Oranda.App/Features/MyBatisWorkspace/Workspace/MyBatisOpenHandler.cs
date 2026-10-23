using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Composition;
using Oranda.Language.MyBatis;
using CoreWorkspace = Oranda.Core.Workspaces.Workspace;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>
/// .xml 매퍼를 MyBatis 작업 공간으로 연다(MB-03, design.md 2장·3.4·9장). 지금의 「SQL 로 펼쳐 읽기 전용으로 열기」를 대신한다.
/// 같은 파일의 작업 공간이 있으면 그리로 옮겨 간다. 깨진 xml·루트가 mapper 가 아닌 파일·읽을 수 없는 인코딩은 알리고 작업 공간을 만들지 않는다.
/// 매퍼는 바이트로 읽는다(<c>Parse(bytes)</c>: euc-kr 등 원래 인코딩을 지킨다, 02-01 후속 메모). 탭은 고른 문장만 연다(1MB 열기 1 s).
/// </summary>
[OrandaService(typeof(IMyBatisOpenHandler))]
public sealed class MyBatisOpenHandler : IMyBatisOpenHandler
{
    private const string StatementsToolId = "oranda.panel.mybatisStatements";
    private const string ParametersToolId = "oranda.panel.binds";

    private readonly DockWorkspace? _docking;
    private readonly HashSet<string> _opening = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>시험용(도구 창 앞으로 가져오기 없음).</summary>
    public MyBatisOpenHandler()
    {
    }

    public MyBatisOpenHandler(DockWorkspace docking)
    {
        _docking = docking;
    }

    public async Task<bool> TryOpenAsync(SqlEditorHost host, string fullPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fullPath);
        var same = host.Workspaces.FirstOrDefault(w => string.Equals(w.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (same is { MyBatis: not null } || _opening.Contains(fullPath))
        {
            if (same is not null)
            {
                host.ActiveWorkspace = same;
                host.NoteFileUsed(fullPath); // 최근 파일 맨 위로
            }

            return true; // 이미 열렸거나 같은 경로를 여는 중(끌어 놓기 두 번 등)
        }

        _opening.Add(fullPath);
        try
        {
            return await OpenAsync(host, fullPath, same, ct).ConfigureAwait(true);
        }
        finally
        {
            _opening.Remove(fullPath);
        }
    }

    private async Task<bool> OpenAsync(SqlEditorHost host, string fullPath, SqlWorkspaceViewModel? sessionless, CancellationToken ct)
    {
        IMyBatisMapperDocument document;
        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(true);
            document = await Task.Run(() => Parse(bytes), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            await host.Dialogs.ShowMessageAsync("MyBatis 매퍼 열기", $"{Path.GetFileName(fullPath)} 을(를) MyBatis 작업 공간으로 열지 못했습니다: {ex.Message}", ct)
                .ConfigureAwait(true);
            return true;
        }

        // 세션 없이 남은 같은 경로의 작업 공간(예전 읽기 전용 펼침 저장본, 복구 실패)은 그 탭을 닫고 MyBatis 작업 공간으로 바꾼다.
        var workspace = sessionless ?? CreateWorkspace(host, fullPath);
        foreach (var old in workspace.Tabs.ToList())
        {
            host.Close(old);
        }

        host.ActiveWorkspace = workspace;
        var session = new MyBatisWorkspaceSession(workspace, fullPath, document);
        workspace.MyBatis = session;
        _ = MapperParseWarmUp.Start(document); // TSK-03-11: 조합 검사 첫 분석을 위해 이 매퍼 문장 모양으로 파서 예측 캐시를 백그라운드에서 데운다
        if (session.VisibleEntries.FirstOrDefault(e => e.IsStatement && e.CanOpen) is { Id: { } first })
        {
            session.OpenStatement(first);
        }

        // design.md 3.4: 숨김에서 돌아온 창은 활성 탭이 되지 않으므로 처음 열 때 앞으로 가져온다.
        _docking?.ActivateTool(StatementsToolId);
        _docking?.ActivateTool(ParametersToolId);
        host.NoteFileUsed(fullPath); // 최근 파일(파일 열기·작업 공간 열기·매퍼 폴더·로그 붙여넣기 모두 이 길)
        return true;
    }

    private static IMyBatisMapperDocument Parse(byte[] bytes) => new MapperParser().Parse(bytes);

    /// <summary>
    /// 빈 작업 공간(탭 0개)을 만든다. 호스트의 공개 경로 중 빈 탭을 더하지 않는 것은 <see cref="SqlEditorHost.Restore(CoreWorkspace)"/> 뿐이다.
    /// 원본 경로는 복원 뒤에 넣는다: 복원 중에 넣으면 복구 참여자가 예전 곁 파일로 세션을 붙인다(새로 연 매퍼는 디스크 그대로 시작한다).
    /// </summary>
    private static SqlWorkspaceViewModel CreateWorkspace(SqlEditorHost host, string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var id = SqlEditorHost.FileWorkspaceId(name, fullPath);
        var unique = id;
        for (int n = 2; host.Workspaces.Any(w => w.Id == unique); n++)
        {
            unique = id + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var workspace = host.Restore(new CoreWorkspace { Id = unique, Name = name, SavedAt = DateTimeOffset.UtcNow });
        workspace.SourcePath = fullPath;
        return workspace;
    }
}
