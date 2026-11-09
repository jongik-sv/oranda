using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Language;

namespace Oranda.Core.Tuning.Providers;

/// <summary>벤치마크 공통 규칙(문장 검사·결과 소진). 실행 흐름은 공급자 서비스가 맡는다.</summary>
public static partial class Benchmark
{
    /// <summary>벤치마크 반복 상한(실수로 큰 값을 넣어 운영 DB 를 오래 두드리지 않게).</summary>
    public const int MaxIterations = 100;

    /// <summary>
    /// 벤치마크로 보낼 문장: 한 문장, SELECT/WITH 질의, FOR UPDATE 없음(잠금), 치환 변수 없음, 바인드는 이름마다 <paramref name="binds"/> 에 값이 있을 때만.
    /// 분류는 언어 서비스(StatementClassifier)가 한다. 아니면 <see cref="ArgumentException"/> — 아무것도 보내지 않는다.
    /// 바인드 값이 없는 호출(사람이 쓰는 튜닝 도우미)은 예전처럼 바인드가 있는 문장을 거부한다.
    /// </summary>
    public static SqlStatement Statement(ILanguageService language, string sql, IReadOnlyDictionary<string, BindValue>? binds = null)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("벤치마크할 문장이 비었습니다.", nameof(sql));
        }

        var stmts = language.Split(sql, SplitOptions.Default);
        if (stmts.Count != 1)
        {
            throw new ArgumentException($"벤치마크는 문장 하나만 받습니다(받은 문장 {stmts.Count}개).", nameof(sql));
        }

        var stmt = stmts[0];
        if (stmt.Kind != StatementKind.Query || stmt.IsClassificationUncertain || stmt.Verb is not ("SELECT" or "WITH"))
        {
            throw new ArgumentException($"벤치마크는 SELECT(WITH) 질의만 실행합니다: {stmt.Verb ?? stmt.Kind.ToString()}", nameof(sql));
        }

        if (ForUpdate().IsMatch(stmt.Text))
        {
            throw new ArgumentException("SELECT … FOR UPDATE 는 행을 잠가 벤치마크하지 않습니다.", nameof(sql));
        }

        if (stmt.SubstitutionNames.Count > 0 || (stmt.BindNames.Count > 0 && (binds is null || binds.Count == 0)))
        {
            throw new ArgumentException("바인드·치환 변수가 있는 문장은 벤치마크하지 않습니다(값을 글자로 넣어 주세요).", nameof(sql));
        }

        if (BenchmarkBinds.MissingReason(stmt.BindNames, binds ?? new Dictionary<string, BindValue>()) is { } missing)
        {
            throw new ArgumentException(missing, nameof(sql));
        }

        return stmt;
    }

    /// <summary>실행 결과를 끝까지 소진해 행 수를 센다. 실패·취소·거부는 예외로 올린다.</summary>
    public static async Task<long> DrainAsync(ExecutionResult result, CancellationToken ct)
    {
        switch (result.Status)
        {
            case ExecutionStatus.Succeeded:
                break;
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException("벤치마크를 취소했습니다.");
            case ExecutionStatus.Denied:
                throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책 관문이 실행을 막았습니다.");
            default:
                throw new InvalidOperationException(result.Error?.Message ?? "실행하지 못했습니다.");
        }

        if (result.ResultSet is null)
        {
            return result.RowsAffected ?? 0;
        }

        await using var rs = result.ResultSet;
        long rows = 0;
        for (var page = 0; ; page++)
        {
            var p = await rs.GetPageAsync(page, ct);
            rows += p.RowCount;
            if (p.IsLast || p.RowCount == 0)
            {
                break;
            }
        }

        return rows;
    }

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForUpdate();
}
