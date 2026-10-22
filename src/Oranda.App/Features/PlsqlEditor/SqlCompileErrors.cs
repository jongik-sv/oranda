using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>
/// SQL 편집기에서 실행한 PL/SQL 객체 하나의 컴파일 오류(TSK-13-28). 오류 줄·열은 서버 소스 기준이므로
/// SQL 편집기 안의 위치(문장 시작 줄 + 유형 낱말이 있는 줄 + 오류 줄 − 1)로 바꿔 커서를 옮긴다.
/// </summary>
public sealed class SqlCompileErrorSource(string title, SqlEditorViewModel tab) : ICompileErrorSource
{
    private int _firstLine = 1;
    private int _firstColumnShift;

    public string Title { get; } = title;

    public ObservableCollection<CompileError> Errors { get; } = [];

    /// <summary>오류가 가리키는 SQL 편집기 탭(문장을 다시 실행하면 새 탭으로 바뀐다).</summary>
    public SqlEditorViewModel Tab { get; private set; } = tab;

    /// <summary>
    /// 서버 소스 1줄이 SQL 편집기의 어디인지 정한다. <paramref name="statementLine"/> = 문장이 시작하는 줄(1부터),
    /// <paramref name="statementText"/> = 실행한 문장 글자(머리 주석·<c>CREATE OR REPLACE</c> 를 건너뛰어 유형 낱말 위치를 찾는다).
    /// </summary>
    public void Anchor(SqlEditorViewModel tab, int statementLine, string statementText)
    {
        Tab = tab;
        var (lineOffset, colOffset) = PlsqlSourceText.SourceStart(statementText);
        _firstLine = statementLine + lineOffset;
        _firstColumnShift = colOffset;
    }

    public bool NavigateTo(CompileError item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var line = _firstLine + item.Line - 1;
        if (line < 1 || line > Tab.Document.LineCount)
        {
            return false;
        }

        var dl = Tab.Document.GetLineByNumber(line);
        var col = item.Column + (item.Line == 1 ? _firstColumnShift : 0);
        Tab.Select(dl.Offset + Math.Clamp(col - 1, 0, dl.Length), 0);
        Tab.Surface?.ScrollToLine(line);
        Tab.Surface?.FocusEditor();
        return true;
    }
}

/// <summary>
/// SQL 편집기 실행 경로가 문장을 마칠 때 부르는 훅(TSK-13-28). CREATE [OR REPLACE] PROCEDURE·FUNCTION·PACKAGE [BODY]·TRIGGER·TYPE [BODY]
/// 문장이 끝나면 서버의 오류 목록(<c>ALL_ERRORS</c>)을 읽어 컴파일 오류 창을 채운다. 오류 없이 끝나면 그 객체의 이전 항목을 지운다.
/// 기존 PL/SQL 편집기 컴파일 경로(<see cref="PlsqlEditorViewModel.CompileAsync"/>)는 건드리지 않는다.
/// </summary>
public static class SqlCompileErrors
{
    private static readonly Dictionary<string, SqlCompileErrorSource> Sources = [];

    /// <summary>
    /// 이 문장이 오류를 읽어 볼 만한가: CREATE 문장이고 성공했거나 「오류가 있는 채로 컴파일됨」(ORA-24344)으로 끝났다.
    /// 그 밖의 실패(구문 오류 ORA-00900 등)는 객체가 만들어지지 않았으므로 읽지 않는다.
    /// </summary>
    public static bool ShouldCapture(SqlStatement stmt, ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(result);
        if (PlsqlSourceText.ParseCreateHeader(stmt.Text) is null)
        {
            return false;
        }

        return result.Status == ExecutionStatus.Succeeded
            || (result.Status == ExecutionStatus.Failed && result.Error is { } e && e.Code.Contains("24344", StringComparison.Ordinal));
    }

    /// <summary>
    /// 오류를 읽어 창에 채운다. 오류(경고 포함)가 있으면 채우고 true(호출한 쪽이 창을 앞으로 가져온다), 없거나 읽지 못하면 false.
    /// 읽기 실패는 실행 결과를 흐리지 않도록 조용히 넘긴다.
    /// </summary>
    public static async Task<bool> CaptureAsync(IMetadataProvider meta, ISession session, SqlEditorViewModel tab, SqlStatement stmt,
        ExecutionResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        if (!ShouldCapture(stmt, result) || PlsqlSourceText.ParseCreateHeader(stmt.Text) is not { } header)
        {
            return false;
        }

        var owner = header.Owner ?? session.Profile.User.ToUpperInvariant();
        var target = new DbObjectRef(owner, header.Name, header.ObjectType);
        IReadOnlyList<CompileError> errors;
        try
        {
            errors = await Task.Run(() => meta.GetCompileErrorsAsync(session, target, ct), CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // 오류 목록을 못 읽어도 실행 결과는 그대로 둔다.
        catch
#pragma warning restore CA1031
        {
            return false;
        }

        var key = $"{session.Profile.Id}|{target.Owner}|{target.Name}|{target.ObjectType}";
        Sources.TryGetValue(key, out var source);
        if (errors.Count == 0)
        {
            source?.Errors.Clear();
            return false;
        }

        if (source is null)
        {
            source = new SqlCompileErrorSource($"{target.Owner}.{target.Name}" + (target.ObjectType.EndsWith("BODY", StringComparison.Ordinal) ? " (본문)" : string.Empty), tab);
            Sources[key] = source;
        }

        var line = tab.Document.GetLineByOffset(Math.Clamp(stmt.Range.Start, 0, tab.Document.TextLength)).LineNumber;
        source.Anchor(tab, line, stmt.Text);
        source.Errors.Clear();
        foreach (var e in errors)
        {
            source.Errors.Add(e);
        }

        CompileErrorsViewModel.Publish(source);
        return true;
    }
}
