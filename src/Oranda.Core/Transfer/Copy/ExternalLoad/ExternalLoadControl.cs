namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// 도구 종류별 갈래를 한곳에 모은 것(XL1 SQL*Loader·XL2 tbLoader). 실행기(<c>CopyRunner.External.cs</c>)와 사전 점검 미리 보기가 이것만 부른다.
/// </summary>
public static class ExternalLoadControl
{
    /// <summary>이 빌드가 그 도구로 넣을 수 있는가. 아니면 사전 점검이 기본 방식으로 바꾼다.</summary>
    public static bool Supports(ExternalLoaderKind kind) => kind == ExternalLoaderKind.SqlLoader || TbLoaderControlFile.Enabled;

    /// <summary>그 도구가 읽는 데이터 파일 형식.</summary>
    public static LoaderFileFormat FileFormat(ExternalLoaderKind kind) =>
        kind == ExternalLoaderKind.SqlLoader ? LoaderFileFormat.SqlLoader : LoaderFileFormat.TbLoader;

    /// <summary>사전 점검 미리 보기의 제어 파일(파일 위치는 실행 때 정한다, 비밀번호 없음).</summary>
    public static string Preview(ExternalLoaderKind kind, string owner, string table, IReadOnlyList<LoaderColumn> columns, int maxErrors, bool direct)
    {
        var spec = new LoaderControlSpec(owner, table, columns, LoaderDataWriter.DataFileName, "load.bad", "load.dsc", maxErrors, direct);
        return kind == ExternalLoaderKind.SqlLoader ? SqlLoaderControlFile.Build(spec) : TbLoaderControlFile.Build(spec);
    }

    /// <summary>
    /// 실행 준비: 제어 파일을 작업 폴더에 쓰고 실행 묶음을 만든다. 부른 쪽은 <see cref="PreparedLoad.Begin"/>(parfile 쓰기) → 실행 → <c>finally</c> <see cref="PreparedLoad.End"/> 순서로 쓴다.
    /// </summary>
    public static PreparedLoad Prepare(ExternalLoaderKind kind, LoaderWorkspace workspace, string toolPath, string owner, string table,
                                       IReadOnlyList<LoaderColumn> columns, string dataFile, int maxErrors, bool direct,
                                       ExternalLoadLogin login, LoaderPasswordVia passwordVia)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(login);
        var spec = new LoaderControlSpec(owner, table, columns, dataFile, workspace.BadFile, workspace.DiscardFile, maxErrors, direct)
        {
            Rows = direct ? null : 5000,
        };
        if (kind == ExternalLoaderKind.SqlLoader)
        {
            File.WriteAllText(workspace.ControlFile, SqlLoaderControlFile.Build(spec), SqlLoaderControlFile.FileEncoding);
            var invocation = SqlLoaderCommand.Build(new SqlLoaderRunSpec(toolPath, workspace.Directory, workspace.ParFile, workspace.ControlFile,
                                                                          workspace.LogFile, workspace.BadFile, login, maxErrors, direct, passwordVia)
            {
                DiscardFile = workspace.DiscardFile,
            });
            return new PreparedLoad(invocation.Start, invocation.Describe(), workspace.LogFile, invocation.WriteParFile, () => invocation.DeleteParFile());
        }

        File.WriteAllText(workspace.ControlFile, TbLoaderControlFile.Build(spec), SqlLoaderControlFile.FileEncoding);
        var tb = TbLoaderCommand.Build(toolPath, workspace, login, maxErrors, direct);
        return new PreparedLoad(tb.Start, tb.Description, workspace.LogFile, () => { }, () => true);
    }

    public static LoaderLogResult ParseLog(ExternalLoaderKind kind, string? text) =>
        kind == ExternalLoaderKind.SqlLoader ? SqlLoaderLog.Parse(text) : TbLoaderLog.Parse(text);

    /// <summary>종료 코드가 「끝까지 돌았다」(성공·경고)인가. 실패 행·상한은 로그로 따로 본다.</summary>
    public static bool ExitOk(ExternalLoaderKind kind, int exitCode) =>
        kind == ExternalLoaderKind.SqlLoader
            ? SqlLoaderLog.ClassifyExit(exitCode) is SqlLoaderExit.Success or SqlLoaderExit.Warning
            : TbLoaderLog.ExitOk(exitCode);
}

/// <summary>
/// 준비한 도구 실행 하나. <see cref="Description"/> 은 로그용(비밀번호 없음). <see cref="Begin"/> 은 실행 직전(비밀 parfile 쓰기), <see cref="End"/> 는 실행 뒤(성공·실패·취소) 반드시 부른다.
/// </summary>
public sealed class PreparedLoad(ExternalProcessStart start, string description, string logFile, Action begin, Func<bool> end)
{
    public ExternalProcessStart Start { get; } = start;

    public string Description { get; } = description;

    public string LogFile { get; } = logFile;

    public void Begin() => begin();

    /// <summary>비밀 parfile 을 지운다. 못 지웠으면 false(작업 폴더 정리가 다시 지운다).</summary>
    public bool End() => end();
}
