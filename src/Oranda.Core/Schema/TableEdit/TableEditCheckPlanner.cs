using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>서버에 물어야 하는 검사 하나의 종류(<see cref="ITableEditChecks"/> 멤버와 짝).</summary>
public enum TableEditCheckKind
{
    Overflow,
    NonNull,
    Nulls,
    DuplicateKeys,
    Orphans,
    CheckViolations,
    Expression,
    NameExists,
    Dependents,
}

/// <summary>
/// 서버에 물을 검사 하나. 쓰는 칸은 종류마다 다르다: Overflow(<see cref="Column"/>·<see cref="Target"/>), NonNull·Nulls(<see cref="Column"/>),
/// DuplicateKeys(<see cref="Columns"/>), Orphans(<see cref="Columns"/>·<see cref="ReferencedTable"/>·<see cref="ReferencedColumns"/>),
/// CheckViolations(<see cref="Condition"/>), Expression(<see cref="Expression"/>·<see cref="Columns"/>), NameExists(<see cref="Name"/>·<see cref="NameKind"/>),
/// Dependents(없음). 이름은 사전 표기.
/// </summary>
/// <param name="Code">걸렸을 때의 판정 종류</param>
/// <param name="RowKey">편집 줄</param>
/// <param name="Subject">대상 이름(문구용)</param>
public sealed record TableEditCheck(TableEditCheckKind Kind, CheckCode Code, string RowKey, string? Subject)
{
    public string? Column { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public ColumnDraft? Target { get; init; }

    public DbObjectRef? ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public string? Condition { get; init; }

    public string? Expression { get; init; }

    public string? Name { get; init; }

    public TableEditNameKind NameKind { get; init; }
}

/// <summary>검사 계획: 서버 없이 정해지는 판정(<see cref="Findings"/>: ⚠ 서버 이름·되돌릴 수 없음 등)과 서버에 물을 검사(<see cref="Checks"/>).</summary>
public sealed record TableEditCheckPlan(IReadOnlyList<CheckFinding> Findings, IReadOnlyList<TableEditCheck> Checks)
{
    public static TableEditCheckPlan Empty { get; } = new([], []);
}

/// <summary>검사 조건(설계 10장).</summary>
public sealed record TableEditCheckOptions
{
    /// <summary>접속 환경. 운영이면 큰 테이블 ⚠(LongLock)을 본다.</summary>
    public ConnectionEnvironment Environment { get; init; }

    /// <summary>큰 테이블 기준 행 수(기본 100만, 설계 10장).</summary>
    public long LargeTableRows { get; init; } = 1_000_000;

    /// <summary>검사 질의 하나의 시간 제한(기본 10초). 넘으면 ⚠ 「확인 못 함」.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>접속한 서버(방언 기능 판정). 모르면 null.</summary>
    public ServerInfo? Server { get; init; }
}

/// <summary>
/// 편집 → 검사 계획(설계 10장 표의 ✖/⚠ 조건). 순수 판정이라 서버 없이 시험한다. 실제로 묻는 일은 <see cref="TableEditCheckRunner"/>.
/// </summary>
public static class TableEditCheckPlanner
{
    public static TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options) =>
        throw new NotImplementedException("T1b");
}

/// <summary>
/// 검사 계획을 <see cref="ITableEditChecks"/> 로 돌려 판정 목록을 만든다(검사마다 시간 제한, 넘거나 읽기 실패 = ⚠ 「확인 못 함」).
/// 운영 큰 테이블 판정은 <see cref="ITableEditChecks.NumRowsAsync"/> 를 한 번 읽어 정한다.
/// </summary>
public static class TableEditCheckRunner
{
    /// <summary>판정 목록(✖ → ⚠ → ✔ 순서). 사용자 취소(<paramref name="ct"/>)는 그대로 던진다.</summary>
    public static Task<IReadOnlyList<CheckFinding>> RunAsync(
        TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct) =>
        throw new NotImplementedException("T1b");
}
