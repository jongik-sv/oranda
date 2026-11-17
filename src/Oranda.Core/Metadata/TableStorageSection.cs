using System.Globalization;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 상세 「저장」 구역(설계 18.2): 테이블 저장 속성과 LOB 저장 절의 지금 값. Oracle·Tibero 공급자가 같이 쓴다.
/// 보기 모드에서도 늘 보여서, ✎ 편집을 켤 때 왼쪽 목록이 바뀌지 않는다(편집 중에는 같은 제목의 편집 화면이 대신 보인다).
/// 모르는 값은 「알 수 없음」.
/// </summary>
public static class TableStorageSection
{
    /// <summary>구역 제목. 테이블 편집기의 「저장」 페이지 제목과 같다.</summary>
    public const string Title = "저장";

    private const string Unknown = "알 수 없음";

    /// <summary>저장 정보를 읽은 테이블이면 구역 하나, 아니면(뷰·저장 정보 없음) null.</summary>
    public static DetailSection? Build(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Storage is not { } s)
        {
            return null;
        }

        List<IReadOnlyList<object?>> rows =
        [
            ["테이블스페이스", table.Tablespace ?? Unknown],
            ["PCTFREE", Num(s.PctFree)],
            ["INITRANS", Num(s.InitTrans)],
            ["압축", s.Compression switch
            {
                TableCompression.None => "없음",
                TableCompression.Basic => "BASIC",
                TableCompression.Advanced => "ADVANCED",
                TableCompression.Other => "그 밖",
                _ => Unknown,
            }],
            ["LOGGING", YesNo(s.Logging)],
            ["병렬", s.Parallel switch
            {
                null => Unknown,
                1 => "NOPARALLEL",
                0 => "PARALLEL(기본)",
                var n => "PARALLEL " + n.Value.ToString(CultureInfo.InvariantCulture),
            }],
        ];

        foreach (var lob in table.Lobs)
        {
            rows.Add(["LOB " + lob.Column, Lob(lob.Storage)]);
        }

        return new DetailSection(Title, ["항목", "값"], rows);
    }

    /// <summary>LOB 저장 절 한 줄. 아는 값만 잇는다(BASICFILE 의 압축·중복 제거, Tibero 의 방식은 사전에 없다).</summary>
    private static string Lob(LobStorage l)
    {
        string?[] parts =
        [
            l.Kind switch { LobStorageKind.SecureFile => "SECUREFILE", LobStorageKind.BasicFile => "BASICFILE", _ => null },
            l.Tablespace,
            l.Compression switch { null => null, LobCompression.None => "압축 없음", var c => "압축 " + c.Value.ToString().ToUpperInvariant() },
            l.Deduplicate switch { null => null, true => "중복 제거", false => "중복 유지" },
            l.InRow switch { null => null, true => "행 안에", false => "행 밖에" },
        ];
        var known = parts.Where(p => p is not null).ToList();
        return known.Count == 0 ? Unknown : string.Join(" · ", known);
    }

    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? Unknown;

    private static string YesNo(bool? v) => v switch { null => Unknown, true => "예", false => "아니오" };
}
