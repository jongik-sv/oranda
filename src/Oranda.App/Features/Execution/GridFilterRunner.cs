using Oranda.App.Features.Binds;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 결과 거르기 줄(GRID-10)의 실행 쪽. 결과를 만든 질의를 <c>SELECT * FROM (…) WHERE (…) ORDER BY …</c> 로 감싸
/// 셀 값 바인드와 「바인드 변수」 목록의 지금 값으로 실행 엔진(치환 변수 → 정책 관문 → 드라이버)에 보낸다.
/// 실행하는 동안 세션을 「실행 중」으로 두어 다른 실행과 겹치지 않게 한다(<see cref="GridRowCounter"/> 와 같은 방식, 이력에는 남기지 않는다).
/// 성공하면 새 결과 집합을 돌려주고 수명을 넘긴다. 실패는 이유를 담은 예외로, 취소는 <see cref="OperationCanceledException"/> 으로 알린다.
/// </summary>
public sealed class GridFilterRunner(ExecutionState state, BindVariableStore binds, Func<ISession?> session, Func<IExecutionService> execution)
{
    /// <summary>앱이 대신 실행하는 도우미 질의라 이력에 남기지 않는다(정책 관문은 그대로 거친다).</summary>
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>
    /// 거른 문장. 원래 본문을 인라인 뷰로 감싸고, 조건은 괄호로 감싼다(조건 안의 <c>OR</c> 우선순위). 끝에 <c>--</c> 주석이 있어도
    /// 닫는 괄호·뒤 절이 주석에 먹히지 않게 줄을 바꾼다. 조건·정렬이 모두 비면 원래 문장 그대로(거르기 지우기).
    /// 바인드·치환 변수 이름은 원래 문장 것에 조건·정렬 글에 나온 것을 더한다(드라이버는 이 이름만 묶는다).
    /// </summary>
    public static SqlStatement BuildStatement(ResultFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query;
        if (request.IsEmpty)
        {
            return query with { Range = default };
        }

        var text = "SELECT * FROM (\n" + query.Text + "\n)";
        if (request.Where.Length > 0)
        {
            text += " WHERE (\n" + request.Where + "\n)";
        }

        if (request.OrderBy.Length > 0)
        {
            text += "\nORDER BY " + request.OrderBy;
        }

        var bindNames = new List<string>(query.BindNames);
        var substitutionNames = new List<string>(query.SubstitutionNames);
        AddNames(request.Where + "\n" + request.OrderBy, bindNames, substitutionNames);
        return query with
        {
            Text = text,
            Range = default,
            Kind = StatementKind.Query,
            Verb = "SELECT",
            BindNames = bindNames,
            SubstitutionNames = substitutionNames,
        };
    }

    /// <summary>거른 결과를 실행한다. 받은 결과 집합의 수명은 부른 쪽이 가진다.</summary>
    public async Task<IResultSet> RunAsync(ResultFilterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var s = Check(request);
        var stmt = BuildStatement(request);

        // 목록에 없는 이름은 더하지 않는다(거르기는 목록·포커스를 건드리지 않는다). 원래 실행 뒤 값을 바꿨으면 지금 값으로 거른다.
        var values = GridRowCounter.BuildBinds(binds, stmt.BindNames, request.Binds);
        state.BeginRun(s.Id);
        try
        {
            var exec = execution();
            var result = await Task.Run(() => exec.ExecuteAsync(s, stmt, values, Options, ct), CancellationToken.None).ConfigureAwait(true);
            return await TakeResultAsync(result, ct).ConfigureAwait(true);
        }
        finally
        {
            state.EndRun(s.Id);
        }
    }

    /// <summary>거른 결과의 전체 행 수(정렬 없이 <c>COUNT(*)</c>). 결과가 비어 읽을 수 없으면 null.</summary>
    public Task<long?> CountAsync(ResultFilterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stmt = BuildStatement(request with { OrderBy = string.Empty });
        return new GridRowCounter(state, binds, session, execution).CountAsync(stmt, request.Binds, ct);
    }

    private ISession Check(ResultFilterRequest request)
    {
        if (request.Query.Kind != StatementKind.Query)
        {
            throw new InvalidOperationException("질의(SELECT) 결과만 거를 수 있습니다.");
        }

        if (session() is not { } s)
        {
            throw new InvalidOperationException("접속이 없습니다. 접속한 뒤 다시 거르세요.");
        }

        if (state.IsBroken(s.Id))
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 거르세요.");
        }

        if (state.IsBusy(s.Id))
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 거르세요.");
        }

        return s;
    }

    /// <summary>
    /// 성공한 결과 집합을 넘겨받는다. 실패·거부·취소·결과 없음은 예외로 바꾸고, 받은 결과 집합이 있으면 닫는다.
    /// 외래키 따라가기(<see cref="RelatedRowsRunner"/>)도 쓴다.
    /// </summary>
    internal static async Task<IResultSet> TakeResultAsync(ExecutionResult result, CancellationToken ct, string noResult = "거른 질의가 결과를 돌려주지 않았습니다.")
    {
        if (result.Status == ExecutionStatus.Succeeded && result.ResultSet is { } rs)
        {
            return rs;
        }

        if (result.ResultSet is { } unused)
        {
            await unused.DisposeAsync().ConfigureAwait(true);
        }

        throw result.Status switch
        {
            ExecutionStatus.Cancelled => new OperationCanceledException(ct),
            ExecutionStatus.Denied => new InvalidOperationException(result.Policy?.Message ?? "정책 관문이 막았습니다."),
            ExecutionStatus.Failed => new InvalidOperationException(DbErrorText.Format(result.Error, "실행하지 못했습니다.")),
            _ => new InvalidOperationException(noResult),
        };
    }

    /// <summary>조건·정렬 글의 바인드(<c>:이름</c>)·치환 변수(<c>&amp;이름</c>) 이름을 등장 순서대로 더한다(문자열·주석 안은 빼고, 중복 없이).</summary>
    private static void AddNames(string text, List<string> bindNames, List<string> substitutionNames)
    {
        foreach (var t in SqlLexer.Tokenize(text))
        {
            if (t.Kind == TokenKind.Bind && t.Length > 1)
            {
                Add(bindNames, text.Substring(t.Start + 1, t.Length - 1));
            }
            else if (t.Kind == TokenKind.Substitution && t.Length > 1)
            {
                var skip = text[t.Start + 1] == '&' ? 2 : 1;
                if (t.Length > skip)
                {
                    Add(substitutionNames, text.Substring(t.Start + skip, t.Length - skip));
                }
            }
        }

        static void Add(List<string> names, string name)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
    }
}
