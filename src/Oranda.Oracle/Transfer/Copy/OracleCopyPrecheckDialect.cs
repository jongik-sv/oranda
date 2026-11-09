using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 사전 점검 방언(<see cref="ICopyPrecheckDialect"/>). 글은 옮기기 전 <c>CopyPrecheckService</c> 의 것과 글자 그대로다.
/// </summary>
internal sealed class OracleCopyPrecheckDialect : ICopyPrecheckDialect
{
    public static readonly OracleCopyPrecheckDialect Instance = new();

    private static readonly (string CatalogId, string Label)[] Probes =
    [
        ("copy.col_priv_count", "열 권한"),
        ("copy.other_grantor_count", "다른 계정이 준 권한"),
        ("copy.mview_log_count", "MV 로그"),
        ("copy.vpd_policy_count", "VPD 정책"),
        ("copy.fga_policy_count", "FGA 정책"),
    ];

    /// <summary>12c 부터 조회만 주는 READ 객체 권한·READ ANY TABLE 이 있다. 복사가 쓰는 조회(SELECT·COUNT)에는 SELECT 와 같다.</summary>
    public bool SupportsReadPrivilege => true;

    public string? RecreateBlockedReason(ISession target) => null;

    /// <summary>STANDARD 한도(VARCHAR2 4000, NVARCHAR2·RAW 2000)를 넘는 길이는 대상 MAX_STRING_SIZE 가 EXTENDED 로 확인될 때만 만든다.</summary>
    public async Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                         CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetParameter);
        var extended = source.Columns.Where(c => c.Length is { } n && c.DataType.ToUpperInvariant() switch
        {
            "VARCHAR2" => n > 4000,
            "NVARCHAR2" or "RAW" => n > 2000,
            _ => false,
        }).Select(c => c.Name).ToList();
        if (extended.Count == 0)
        {
            return [];
        }

        // V$PARAMETER 를 읽지 못하면(null) EXTENDED 인지 알 수 없다: 막는 쪽.
        var size = await targetParameter("max_string_size", ct).ConfigureAwait(false);
        return string.Equals(size, "EXTENDED", StringComparison.OrdinalIgnoreCase)
            ? []
            : [$"열 {string.Join(", ", extended)} 의 길이가 STANDARD 한도를 넘는데 대상 MAX_STRING_SIZE 가 EXTENDED 로 확인되지 않습니다({size ?? "읽지 못함"})"];
    }

    public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => Probes;

    public string RecycleBinNote(string? tablespace)
        => string.Equals(tablespace, "SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? " 이 표는 SYSTEM 테이블스페이스에 있어 휴지통에 남지 않고 영구 삭제됩니다."
            : " 지운 표는 휴지통에 남습니다(PURGE 없음, 공간이 부족하면 휴지통 객체가 자동으로 지워질 수 있음).";

    public string ReadConsistencyParameter => "undo_retention";

    public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention)
    {
        if (retention is not { } r)
        {
            return (CheckLevel.Pass, "원본 UNDO_RETENTION 을 읽을 수 없어 같은 시점 읽기가 끝까지 유지되는지는 알 수 없습니다.");
        }

        return estimatedSeconds > r
            ? (CheckLevel.Confirm,
                $"예상 읽기 시간 약 {Number(estimatedSeconds)}초가 원본 UNDO_RETENTION {Number(r)}초보다 깁니다. ORA-01555 로 멈출 수 있으니 기간을 줄이거나 나눠 실행하세요.")
            : (CheckLevel.Pass, $"예상 읽기 시간 약 {Number(estimatedSeconds)}초, 원본 UNDO_RETENTION {Number(r)}초.");
    }

    public string ChildRecordFoundError => "ORA-02292";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
