using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema.TableEdit.Conversion;

/// <summary>
/// 열 형식 변경 자동 변환의 실행 전 검사 읽기(설계 18.9). <see cref="ITableEditChecks"/> 와 같은 규칙: 실행 엔진 밖 세션 읽기, 이력·일지·묶음에 남기지 않음,
/// 읽기만(잠금 없음), 취소는 서버 명령 취소 → <see cref="OperationCanceledException"/>. 이름은 사전 표기로 받는다.
/// 구현은 <c>[OrandaService(typeof(IColumnConversionChecks), Dbms = …)] internal sealed</c>. Tibero 는 아직 없음(행 단위 시험 질의로 구현할 몫 — win30).
/// </summary>
public interface IColumnConversionChecks
{
    Dbms Dbms { get; }

    /// <summary>
    /// 「변환 안 되는 행」 수: 변환식 값을 새 정의(<paramref name="target"/>)에 담을 수 없는 행(변환 오류·넘침·자름). 새 정의가 NOT NULL 이면 값이 NULL 이 되는 행도 센다.
    /// Oracle 12.2+ 는 기본 변환식이면 <c>VALIDATE_CONVERSION</c> 집합 질의(<see cref="ColumnConversionDefaults.Validation"/>), 그 밖은 행 단위 시험 질의.
    /// 시간 안에 다 세지 못하면 <see cref="ConversionFailures.IsLowerBound"/>.
    /// </summary>
    /// <param name="source">지금 열</param>
    /// <param name="expression">변환식(옛 열 이름을 쓴 SQL 식)</param>
    Task<ConversionFailures> FailingRowsAsync(
        ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct);

    /// <summary>
    /// 이 열이 든 PK·UK 를 참조하는 FK(다른 테이블·이 테이블). 3단계 <c>CASCADE CONSTRAINTS</c> 가 함께 지워 버리므로 바꿔치기를 막는다.
    /// </summary>
    Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct);

    /// <summary>
    /// 변환한 값으로 CHECK <paramref name="condition"/>(옛 열 이름)을 어기는 행 수 — 5단계가 다시 만들 때 실패하지 않을지(3단계 DROP 뒤라 되돌릴 수 없음).
    /// 조건 계산이 오류면 <see cref="ConversionFailures.IsLowerBound"/> + 서버 오류.
    /// </summary>
    /// <param name="otherColumns">테이블의 다른 열(조건이 쓸 수 있는 열)</param>
    Task<ConversionFailures> ConvertedCheckViolationsAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct);

    /// <summary>변환한 값으로 PK·UK <paramref name="keyColumns"/> 가 겹치는 행 수(모든 키 열이 NULL 인 행은 빼고 — UK 규칙).</summary>
    Task<ConversionFailures> ConvertedDuplicateKeysAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct);
}

/// <summary>FK 하나(소유자·테이블·제약 이름, 사전 표기).</summary>
public sealed record ForeignKeyRef(string Owner, string Table, string Name);

/// <summary>변환 검사 결과(창의 ✖·⚠ 줄과 2단계 「N행」).</summary>
/// <param name="Rows">2단계 UPDATE 가 바꿀 행 수(통계 <c>NUM_ROWS</c>, 없으면 <c>COUNT(*)</c>). 읽지 못하면 null.</param>
public sealed record ColumnConversionCheckResult(IReadOnlyList<CheckFinding> Findings, long? Rows)
{
    /// <summary>✖ 가 있어 실행을 막는가.</summary>
    public bool IsBlocked => Findings.Any(f => f.Severity == CheckSeverity.Block);
}

/// <summary>
/// 변환 검사 돌리기: 서버 없는 판정(<see cref="ColumnConversionBuilder.Findings"/>) + 변환 안 되는 행(✖ <see cref="CheckCode.ConversionFails"/>, 0 이면 ✔) +
/// 다시 만들 제약이 새 값에서 성립하나(CHECK 위반 ✖ <see cref="CheckCode.CheckViolations"/>·PK/UK 중복 ✖ <see cref="CheckCode.DuplicateKeys"/>, Detail <c>afterConversion</c>) +
/// (바꿔치기) 이 열을 참조하는 FK(✖ <see cref="CheckCode.InvalidDefinition"/> <c>referencedByForeignKey:…</c>, 사전 권한이 없으면 ⚠ 다른 스키마 FK 확인 못 함) + 행 수 + 운영 큰 테이블 ⚠.
/// 검사마다 <see cref="TableEditCheckOptions.Timeout"/>. 넘거나 읽기 실패 = ⚠ 「확인 못 함」 — 다만 변환 검사가 실패를 이미 봤으면 ✖ 「1건 이상」.
/// </summary>
public static class ColumnConversionCheckRunner
{
    public static async Task<ColumnConversionCheckResult> RunAsync(
        ColumnConversionRequest request, ColumnConversionMethod method, ITableEditDialect dialect,
        IColumnConversionChecks conversions, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(conversions);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(options);

        var key = request.RowKey;
        var source = request.Source;
        var target = ColumnConversionBuilder.ResolveTarget(source, request.Target); // 실행과 같은 길이 기준으로 검사
        var table = request.Table;
        var expression = request.Expression.Trim();
        var findings = new List<CheckFinding>(ColumnConversionBuilder.Findings(request, method, dialect, options.Server));
        if (findings.Any(f => f.Code == CheckCode.Unsupported || (f.Code == CheckCode.InvalidDefinition && f.Detail is "virtualColumn" or "emptyExpression")))
        {
            return Sorted(findings, null); // 변환식을 돌려 볼 수 없다
        }

        var failures = await TryAsync(findings, key, source.Name, options,
            t => conversions.FailingRowsAsync(session, table, source, target, expression, t), ct).ConfigureAwait(false);
        if (failures is not null)
        {
            findings.Add(new CheckFinding(failures.Count > 0 ? CheckSeverity.Block : CheckSeverity.Ok, CheckCode.ConversionFails, key)
            {
                Subject = source.Name,
                Count = failures.Count,
                Detail = failures.IsLowerBound ? "atLeast" : failures.FirstError,
                ViewRowsSql = failures.Count > 0 ? failures.ViewRowsSql : null,
            });
        }

        var others = request.Original.Columns.Where(c => c.RowKey != key).Select(c => c.Name).ToList();
        foreach (var k in ColumnConversionBuilder.AffectedConstraints(request))
        {
            var (code, read) = k.Kind switch
            {
                ConstraintKind.Check when k.Condition is { } condition => (CheckCode.CheckViolations,
                    (Func<CancellationToken, Task<ConversionFailures>>?)(t => conversions.ConvertedCheckViolationsAsync(session, table, others, source.Name, expression, condition, t))),
                ConstraintKind.PrimaryKey or ConstraintKind.Unique => (CheckCode.DuplicateKeys,
                    t => conversions.ConvertedDuplicateKeysAsync(session, table, others, source.Name, expression, KeyNames(request, k), t)),
                _ => (CheckCode.Passed, null),
            };
            if (read is null)
            {
                continue;
            }

            var broken = await TryAsync(findings, key, k.Name, options, read, ct).ConfigureAwait(false);
            if (broken is not null)
            {
                findings.Add(new CheckFinding(broken.Count > 0 ? CheckSeverity.Block : CheckSeverity.Ok, code, key)
                {
                    Subject = k.Name, Count = broken.Count, Detail = broken.FirstError ?? "afterConversion",
                });
            }
        }

        if (method == ColumnConversionMethod.ColumnSwap)
        {
            if (options.Server?.Capabilities.CanQueryDictionary == false)
            {
                // ALL_ 사전에는 권한 없는 스키마의 FK 가 안 보이는데 CASCADE CONSTRAINTS 는 그것도 지운다.
                findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, key) { Subject = source.Name, Detail = "foreignKeysInOtherSchemas" });
            }

            var fks = await TryAsync(findings, key, source.Name, options,
                t => conversions.ReferencingForeignKeysAsync(session, table, source.Name, t), ct).ConfigureAwait(false);
            foreach (var fk in fks ?? [])
            {
                findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, key)
                {
                    Subject = source.Name, Detail = $"referencedByForeignKey:{fk.Owner}.{fk.Table}.{fk.Name}",
                });
            }
        }

        var rows = await TryAsync(findings, key, source.Name, options, async t =>
            (long?)(await checks.NumRowsAsync(session, table, t).ConfigureAwait(false) ?? await checks.CountRowsAsync(session, table, t).ConfigureAwait(false)), ct)
            .ConfigureAwait(false);
        if (options.Environment == ConnectionEnvironment.Production && (rows is null || rows >= options.LargeTableRows))
        {
            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.LongLock, key) { Subject = source.Name, Count = rows, Detail = rows is null ? "unknown" : null });
        }

        return Sorted(findings, rows);
    }

    private static async Task<T?> TryAsync<T>(
        List<CheckFinding> findings, string key, string subject, TableEditCheckOptions options, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            return await read(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, key) { Subject = subject, Detail = "timeout" });
        }
#pragma warning disable CA1031 // 검사 읽기는 어떤 이유로 실패해도 ⚠ 「확인 못 함」(TableEditCheckRunner 와 같음)
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, key) { Subject = subject, Detail = ex.Message });
        }

        return default;
    }

    /// <summary>제약 열 이름(지금 이름). 바꿀 열은 옛 이름 그대로 — 검사 질의가 변환식 값을 그 이름으로 낸다.</summary>
    private static List<string> KeyNames(ColumnConversionRequest request, ConstraintDraft k) =>
        [.. k.ColumnKeys.Select(c => request.Original.Column(c)?.Name ?? c)];

    private static ColumnConversionCheckResult Sorted(List<CheckFinding> findings, long? rows) =>
        new([.. findings.OrderBy(f => f.Severity switch { CheckSeverity.Block => 0, CheckSeverity.Warning => 1, _ => 2 })], rows);
}
