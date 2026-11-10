using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 적재 방식 판정(조정 결정 88·89). 순수 함수 — 사전 점검(실제 방식·알림·제어 파일 미리 보기)과 실행기(데이터 파일 열 배치)가 같은 판정을 쓴다.
/// <para>
/// 외부 도구는 INSERT 적재만 한다: 키로 맞추기(MERGE)와 「모두 끝난 뒤 한 번」 커밋(도구가 따로 커밋하므로 실행 전체를 한 번에 되돌릴 수 없다)은 기본 방식이다.
/// 이번 실행이 만든 테이블(없으면 만들기·다시 만들기)만 direct path 로 넣고, 이미 있는 테이블은 일반 경로(conventional)로 넣는다 —
/// direct path 는 트리거·FK·CHECK 를 건너뛰고 키가 겹치면 인덱스를 못 쓰게 두므로, 이미 있는 테이블에서는 앱의 INSERT 와 같게 동작하는 일반 경로를 쓴다.
/// </para>
/// </summary>
public static class CopyLoad
{
    /// <summary>사용자가 고른 적재 방식: 테이블이 따로 고른 값, 없으면 전체 기본값, 그것도 없으면 기본 방식.</summary>
    public static LoadMethod Requested(CopyJob job, TableRule rule)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Load ?? job.TableDefaults?.Load ?? LoadMethod.Standard;
    }

    /// <summary>대상 DBMS 의 적재 도구. 없는 DBMS 면 null.</summary>
    public static ExternalLoaderKind? ToolFor(Dbms target) => target switch
    {
        Dbms.Oracle => ExternalLoaderKind.SqlLoader,
        Dbms.Tibero => ExternalLoaderKind.TbLoader,
        _ => null,
    };

    /// <summary>도구 이름(알림·로그).</summary>
    public static string ToolName(ExternalLoaderKind kind) => kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader" : "tbLoader";

    /// <summary>
    /// 실행 전체가 외부 도구를 못 쓰는 이유(없으면 null): 커밋 방식, 대상 DBMS, 대상 접속의 인증 방식. 도구 유무는 따로 확인한다(<see cref="IExternalToolProbe.CheckAsync"/>).
    /// </summary>
    public static string? RunBlocker(CopyJob job, Dbms target, ConnectionProfile targetProfile)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(targetProfile);
        if (job.Options.Commit == CommitMode.AtEnd)
        {
            return "커밋 방식이 「모두 끝난 뒤 한 번」이면 외부 도구가 따로 커밋해 실행 전체를 되돌릴 수 없습니다";
        }

        if (ToolFor(target) is null)
        {
            return $"{target} 대상에는 적재 도구가 없습니다";
        }

        if (targetProfile.Auth != AuthMode.Default || !string.IsNullOrWhiteSpace(targetProfile.ProxyUser) || !string.IsNullOrWhiteSpace(targetProfile.WalletLocation))
        {
            return "대상 접속이 기본 인증(사용자·비밀번호)이 아니라 외부 도구가 접속할 수 없습니다(관리자 권한·프록시·지갑)";
        }

        return string.IsNullOrWhiteSpace(targetProfile.DataSource) ? "대상 접속 주소가 비어 있습니다" : null;
    }

    /// <summary>테이블이 외부 도구를 못 쓰는 이유(없으면 null): 대상 처리, 열 형식.</summary>
    public static string? TableBlocker(CopyTablePlan plan, IReadOnlyList<LoaderColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(columns);
        // 기본 방식이 MERGE 로 넣는 테이블(PK 기준 MERGE, 키가 있는 「같은 범위 지우고 넣기」 — 범위 밖 같은 키 행을 덮어쓴다)은 INSERT 만 하는 도구로 넣을 수 없다.
        if (PrecheckRules.UsesMerge(plan.Rule, plan.KeyColumns) && !Fresh(plan))
        {
            return "키로 맞추기(MERGE)로 넣는 테이블이라 외부 도구로 넣을 수 없습니다";
        }

        if (columns.Count == 0)
        {
            return "넣을 열이 없습니다";
        }

        if (columns.FirstOrDefault(c => !LoaderValueText.Supports(c.TargetType)) is { } unsupported)
        {
            return $"열 {unsupported.Name}({unsupported.TargetType}) 은(는) 외부 도구로 넣을 수 없는 형식입니다";
        }

        if (!Fresh(plan))
        {
            var source = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            foreach (var column in columns)
            {
                if (source.TryGetValue(column.Name, out var s) && Family(s.DataType) is { } from && Family(column.TargetType) is { } to && from != to)
                {
                    // 앱의 INSERT 는 서버가 형식을 바꾸지만(NLS 기준), 데이터 파일은 대상 형식의 글자로 써야 해 결과가 달라질 수 있다.
                    return $"열 {column.Name} 의 원본({s.DataType})·대상({column.TargetType}) 형식 갈래가 달라 외부 도구로 넣지 않습니다";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 외부 도구가 direct path 로 넣는가(결정 96): 이번 실행이 만드는 테이블, 또는 이미 있는 테이블 가운데 트리거가 없고(<see cref="CopyTablePlan.NoTargetTriggers"/>)
    /// FK·CHECK 제약도 없으며(<see cref="HasRowChecks"/> — direct path 는 이것들을 끄고 넣어 어긋난 행을 거절하지 않는다) 「모두 지우고 넣기」라
    /// 테이블 전체를 적재 전에 비우고 확정하는 것. 그 밖은 일반 경로(트리거·제약이 앱의 INSERT 와 같게 동작).
    /// </summary>
    public static bool Direct(CopyTablePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Fresh(plan) || (plan.NoTargetTriggers && plan.Rule.Action == TargetAction.DeleteAllInsert && !HasRowChecks(plan.Target));
    }

    /// <summary>대상 테이블에 행을 검사하는 FK·CHECK 제약이 있는가(NOT NULL 만 보는 CHECK 는 direct path 도 지킨다).</summary>
    public static bool HasRowChecks(TableInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Constraints.Any(c => c.Kind == ConstraintKind.ForeignKey
                                           || (c.Kind == ConstraintKind.Check && !IsNotNullOnly(c.Condition)));
    }

    private static bool IsNotNullOnly(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true; // 조건을 모르는 CHECK 는 시스템 NOT NULL(메타데이터가 조건 없이 준다)
        }

        var c = condition.Trim();
        return c.EndsWith("IS NOT NULL", StringComparison.OrdinalIgnoreCase)
               && c.IndexOf(" AND ", StringComparison.OrdinalIgnoreCase) < 0 && c.IndexOf(" OR ", StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>이번 실행이 만드는 테이블(없으면 만들기·다시 만들기).</summary>
    public static bool Fresh(CopyTablePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.CreateMissing || plan.Rule.Action == TargetAction.Recreate;
    }

    /// <summary>
    /// 데이터 파일·제어 파일의 열(<paramref name="copyColumns"/> 순서 그대로). 형식 = 이미 있는 테이블은 대상 열 형식,
    /// 이번에 만드는 테이블은 만드는 형식(같은 DBMS = 원본 형식, 교차 = <see cref="CrossTypeMap"/> 이 옮긴 형식).
    /// </summary>
    public static IReadOnlyList<LoaderColumn> Columns(CopyTablePlan plan, IReadOnlyList<string> copyColumns, Dbms source, Dbms target)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(copyColumns);
        var sources = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var targets = plan.Target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var fresh = Fresh(plan);
        var list = new List<LoaderColumn>(copyColumns.Count);
        foreach (var name in copyColumns)
        {
            sources.TryGetValue(name, out var s);
            string? type;
            if (!fresh && targets.TryGetValue(name, out var t))
            {
                type = t.DataType;
            }
            else if (s is not null && source != target)
            {
                var map = CrossTypeMap.Map(source, target, s);
                type = map.Target?.DataType ?? map.BindType;
            }
            else
            {
                type = s?.DataType ?? (targets.TryGetValue(name, out var t2) ? t2.DataType : null);
            }

            list.Add(new LoaderColumn(name, type ?? "VARCHAR2(4000)"));
        }

        return list;
    }

    /// <summary>
    /// 교차 복사에서 옮길 형식이 없어 빼는 열을 뺀 복사 열(실행기 <c>RowShape</c> 와 같은 판정, 사전 점검 미리 보기용). 같은 DBMS 면 그대로.
    /// </summary>
    public static IReadOnlyList<string> CopyColumns(CopyTablePlan plan, Dbms source, Dbms target)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (source == target)
        {
            return plan.Columns;
        }

        var sources = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        return [.. plan.Columns.Where(c => !sources.TryGetValue(c, out var s) || !CrossTypeMap.Map(source, target, s).IsDropped)];
    }

    /// <summary>형식 갈래(글자·숫자·날짜·시각·시간대 시각·구간·이진·LOB). 모르는 형식은 null(비교하지 않는다).</summary>
    internal static string? Family(string serverType)
    {
        var t = serverType.Trim().ToUpperInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        var head = paren < 0 ? t : t[..paren].TrimEnd();
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            return t.Contains("TIME ZONE", StringComparison.Ordinal) ? "TSTZ" : "TS";
        }

        if (t.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return t.Contains("MONTH", StringComparison.Ordinal) ? "YM" : "DS";
        }

        return head switch
        {
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" or "CHAR" or "NCHAR" => "TEXT",
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" => "NUMBER",
            "BINARY_FLOAT" or "BINARY_DOUBLE" => "BINNUM",
            "DATE" => "DATE",
            "RAW" => "RAW",
            "CLOB" or "NCLOB" => "CLOB",
            "BLOB" => "BLOB",
            _ => null,
        };
    }
}
