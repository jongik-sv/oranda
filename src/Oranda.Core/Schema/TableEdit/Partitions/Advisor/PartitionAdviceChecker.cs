using Oranda.Core.Connection;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>앱 확인 수준(✔ / ⚠ / ✖). ✖ 가 하나라도 있으면 「편집기에 채우기」를 끈다.</summary>
public enum AdviceCheckLevel
{
    Ok,
    Warn,
    Stop,
}

/// <summary>
/// 앱 확인 하나(설계 4.1). 문구는 App 이 <see cref="TextKey"/> 로 찾고 <see cref="Args"/> 를 <c>{0}</c>… 에 넣는다.
/// </summary>
/// <param name="Code"><see cref="AdviceCheckCodes"/> 값</param>
/// <param name="Subject">걸린 열·인덱스 이름(없으면 null)</param>
/// <param name="Args">문구에 넣을 값(비율·건수 등, 문화 무관 글)</param>
public sealed record AdviceCheck(AdviceCheckLevel Level, string Code, string? Subject = null, IReadOnlyList<string>? Args = null)
{
    /// <summary>문구 키 머리.</summary>
    public const string TextKeyPrefix = "TableEdit.partition.ai.check.";

    /// <summary>문구 키(<c>TableEdit.partition.ai.check.key-missing</c>).</summary>
    public string TextKey => TextKeyPrefix + Code;
}

/// <summary>앱 확인 code(설계 4.1 표 + 채우기의 되돌림 실패).</summary>
public static class AdviceCheckCodes
{
    public const string KeyMissing = "key-missing";
    public const string KeyType = "key-type";
    public const string IntervalString = "interval-string";
    public const string IntervalNull = "interval-null";
    public const string RangeNull = "range-null";
    public const string DateLikeRatio = "datelike-ratio";
    public const string PkGlobal = "pk-global";
    public const string LocalOk = "local-ok";
    public const string LocalUnique = "local-unique";
    public const string HashPow2 = "hash-pow2";
    public const string ListCover = "list-cover";
    public const string BoundsOrder = "bounds-order";
    public const string Dialect = "dialect";
    public const string SmallTable = "small-table";
    public const string StatsOld = "stats-old";

    /// <summary>모르는 방식·배치 글(스키마 밖 값).</summary>
    public const string MethodUnknown = "method-unknown";

    /// <summary>금고로 되돌릴 수 없는 자리표시자(설계 4.2 LIST, ✖).</summary>
    public const string ValueUnknown = "value-unknown";
}

/// <summary>AI 후보를 근거·방언으로 결정론 확인(PC4 에서 규칙을 채움).</summary>
public static class PartitionAdviceChecker
{
    /// <summary>후보 하나의 확인 목록(수준 순서와 무관, 표 순서).</summary>
    public static IReadOnlyList<AdviceCheck> Check(PartitionCandidate candidate, PartitionEvidence evidence, ITableEditDialect dialect, ServerInfo? server)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(dialect);

        // PC1 껍데기: 확인 없음.
        return [];
    }

    /// <summary>✖ 가 없으면 참(채우기 단추를 켬).</summary>
    public static bool CanFill(IEnumerable<AdviceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return !checks.Any(c => c.Level == AdviceCheckLevel.Stop);
    }
}
