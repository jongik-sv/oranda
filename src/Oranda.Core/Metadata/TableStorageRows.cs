using System.Globalization;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Metadata;

/// <summary>
/// 공급자가 사전 값(<c>ALL_TABLES</c>·<c>ALL_LOBS</c>)을 <see cref="TableStorage"/>·<see cref="LobStorage"/> 로 옮기는 읽기 규칙(T13c).
/// Oracle·Tibero 가 같이 쓴다. 모르는 글은 null(바꿀 수 없는 값으로 짐작하지 않는다).
/// </summary>
public static class TableStorageRows
{
    /// <summary>
    /// 테이블 저장 속성. 값은 사전 열 그대로(<c>COMPRESS_FOR</c> 가 없는 Tibero 는 null).
    /// 익스텐트(<see cref="TableStorage.InitialExtent"/>·<see cref="TableStorage.NextExtent"/>)는 읽지 않는다 — 기존 테이블에는 쓰지 않고(TE-39),
    /// 사전 값은 지연 세그먼트(NULL)·최소 익스텐트(Tibero INITIAL 256K → 131072)라 입력과 달라 거짓 차이만 만든다.
    /// </summary>
    public static TableStorage Table(object? pctFree, object? iniTrans, object? compression, object? compressFor, object? logging, object? degree) =>
        new()
        {
            PctFree = Number(pctFree),
            InitTrans = Number(iniTrans),
            Compression = Compression(Str(compression), Str(compressFor)),
            Logging = YesNo(Str(logging)),
            Parallel = Parallel(Str(degree)),
        };

    /// <summary>
    /// 테이블 종류에 맞춘 저장 속성: IOT·외부 테이블의 테이블 줄 PCT_FREE·INI_TRANS·압축·LOGGING 은 실제 값이 아니어서(IOT 는 인덱스 쪽) 병렬만 남긴다.
    /// 편집은 어차피 막힌다(특수 테이블) — 화면의 「지금 값」이 틀리게 보이지 않게.
    /// </summary>
    public static TableStorage ForKind(TableStorage storage, TableKind kind)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return kind is TableKind.Iot or TableKind.External ? new TableStorage { Parallel = storage.Parallel } : storage;
    }

    /// <summary>LOB 열 하나. <paramref name="securefile"/> 가 null 이면(Tibero 는 열이 없다) 저장 방식 모름.</summary>
    public static LobInfo Lob(string column, object? securefile, object? tablespace, object? compression, object? deduplication, object? inRow) =>
        new(column, new LobStorage
        {
            Kind = Str(securefile)?.Trim().ToUpperInvariant() switch
            {
                "YES" => LobStorageKind.SecureFile,
                "NO" => LobStorageKind.BasicFile,
                _ => null,
            },
            Tablespace = Str(tablespace),
            Compression = Str(compression)?.Trim().ToUpperInvariant() switch
            {
                "NO" or "NOCOMPRESS" => LobCompression.None, // Tibero = NOCOMPRESS
                "LOW" => LobCompression.Low,
                "MEDIUM" => LobCompression.Medium,
                "HIGH" => LobCompression.High,
                _ => null, // BASICFILE 의 NONE·모르는 글
            },
            Deduplicate = Str(deduplication)?.Trim().ToUpperInvariant() switch
            {
                "NO" => false,
                "LOB" => true,
                _ => null,
            },
            InRow = YesNo(Str(inRow)),
        });

    /// <summary><c>DEGREE</c>: 앞뒤 공백을 떼고 <c>DEFAULT</c> = 0(서버 기본 <c>PARALLEL</c>), 숫자 = 그 값.</summary>
    public static int? Parallel(string? degree)
    {
        var d = degree?.Trim();
        if (string.IsNullOrEmpty(d))
        {
            return null;
        }

        if (string.Equals(d, "DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
    }

    /// <summary>Tibero <c>DEGREE</c>(숫자): 0 = <c>NOPARALLEL</c>(→ 1), -1 = <c>PARALLEL</c>(서버 기본 → 0), n = <c>PARALLEL n</c>. 실측 tibero-table-edit-probe.md 12장.</summary>
    public static int? TiberoParallel(object? degree) => Number(degree) switch
    {
        null => null,
        0 => 1,
        -1 => 0,
        > 0 and var n => n,
        _ => null,
    };

    /// <summary>
    /// Oracle <c>COMPRESSION</c> ENABLED/DISABLED + <c>COMPRESS_FOR</c>(BASIC·ADVANCED·OLTP, 11.1 의 DIRECT LOAD ONLY·FOR ALL OPERATIONS, 그 밖 = <see cref="TableCompression.Other"/>),
    /// Tibero <c>COMPRESSION</c> YES/NO.
    /// </summary>
    public static TableCompression? Compression(string? compression, string? compressFor) =>
        compression?.Trim().ToUpperInvariant() switch
        {
            "DISABLED" or "NO" => TableCompression.None,
            "YES" => TableCompression.Basic,
            "ENABLED" => compressFor?.Trim().ToUpperInvariant() switch
            {
                "BASIC" or "DIRECT LOAD ONLY" => TableCompression.Basic,
                "ADVANCED" or "OLTP" or "FOR ALL OPERATIONS" => TableCompression.Advanced,
                _ => TableCompression.Other,
            },
            _ => null,
        };

    private static bool? YesNo(string? v) => v?.Trim().ToUpperInvariant() switch
    {
        "YES" or "Y" => true,
        "NO" or "N" => false,
        _ => null,
    };

    /// <summary>정수 칸(사전 NUMBER). 숫자가 아니면 null(다른 판의 글 값으로 테이블 읽기 전체가 실패하지 않게).</summary>
    private static int? Number(object? v) =>
        v is null ? null
        : decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
          && n == decimal.Truncate(n) && n is >= int.MinValue and <= int.MaxValue
            ? (int)n
            : null;
}
