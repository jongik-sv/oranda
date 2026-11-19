using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>아이콘 모서리 표식(뱃지)의 모양. 색만으로 가르지 않게 종류마다 모양도 다르다(<see cref="SchemaBadgeGeometry"/>).</summary>
public enum BadgeShape
{
    Dot,
    Ring,
    Square,
    Diamond,
    Bars,
    Bar,
    Plus,
    Cross,
    Play,

    /// <summary>쓸 수 없음(주황 세모). 상태 표식만 쓴다.</summary>
    Warning,
}

/// <summary>뱃지 자리. 오른쪽 아래 = 주 종류, 오른쪽 위 = 파티션·단위 같은 덧붙은 속성, 왼쪽 위 = 둘째 종류, 왼쪽 아래 = 상태(쓸 수 없음).</summary>
public enum BadgeCorner
{
    BottomRight,
    TopRight,
    TopLeft,
    BottomLeft,
}

/// <summary>뱃지 하나. <see cref="Tone"/> 은 아이콘 색 토큰 이름(<c>OrandaIcon&lt;Tone&gt;Brush</c>, 밝게·어둡게 두 벌). <see cref="Tip"/> 은 툴팁 한 줄.</summary>
public sealed record SchemaBadge(BadgeShape Shape, string Tone, BadgeCorner Corner, string Tip);

/// <summary>
/// 트리 줄 앞 아이콘의 모양: 기본 아이콘(<see cref="IconKey"/>)에 모서리 뱃지를 얹고, 꺼진 것(<see cref="Dimmed"/>)은 흐린 회색으로 보인다.
/// <see cref="Tip"/> 은 뱃지·상태를 줄마다 적은 툴팁(없으면 null).
/// </summary>
public sealed record SchemaLook(string? IconKey, IReadOnlyList<SchemaBadge> Badges, bool Dimmed, string? Tip)
{
    public static SchemaLook Plain(string? iconKey) => new(iconKey, [], false, null);

    /// <summary>시험·화면 비교용: 같은 키·뱃지·흐림이면 같다(목록은 차례대로 비교).</summary>
    public bool Equals(SchemaLook? other) =>
        other is not null && IconKey == other.IconKey && Dimmed == other.Dimmed && Tip == other.Tip && Badges.SequenceEqual(other.Badges);

    public override int GetHashCode() => HashCode.Combine(IconKey, Dimmed, Tip, Badges.Count);
}

/// <summary>
/// 종류·상태 → 아이콘 모양(순수 함수, 시험 대상). 한 규칙으로 상태를 보인다:
/// <list type="bullet">
/// <item>꺼진 것(트리거·제약·작업 DISABLED, 숨은 열): 흐린 회색(<see cref="SchemaLook.Dimmed"/>) + 툴팁.</item>
/// <item>쓸 수 없는 것(UNUSABLE 인덱스·깨진 동의어·STALE 구체화 뷰·BROKEN/실패 작업): 왼쪽 아래 주황 세모(<see cref="BadgeShape.Warning"/>).</item>
/// <item>INVALID: 이름 글자를 빨갛게(기존 <c>TextBlock.invalid</c>) + 툴팁 줄.</item>
/// </list>
/// 기본인 것(일반 테이블·일반 인덱스·객체 타입·켜진 예약 작업·개인 동의어의 PUBLIC 아님)은 뱃지가 없다. 뱃지 색은 상태 색(주황·빨강·회색)을 쓰지 않는다.
/// </summary>
public static class SchemaLooks
{
    private const string Blue = "Blue";
    private const string Green = "Green";
    private const string Amber = "Amber";
    private const string Purple = "Purple";
    private const string Teal = "Teal";
    private const string Neutral = "Neutral";
    private const string Orange = "Orange";

    /// <summary>영어 화면이면 문구 표(literals-en.json)의 영어로.</summary>
    internal static string L(string korean) =>
        Localization.LiteralTranslator.IsEnglish && Localization.LiteralTranslator.Table.TryGetValue(korean, out var english) ? english : korean;

    /// <summary>
    /// 목록 단계 객체(테이블·트리거·작업·동의어·구체화 뷰·타입). <paramref name="mark"/> = 보조 질의 표식(없으면 null — 예전 캐시·아직 안 읽음 = 뱃지 없음).
    /// PUBLIC 동의어는 표식 없이도 소유자로 안다.
    /// </summary>
    public static SchemaLook ForObject(string iconKey, DbObjectRef r, ObjectMark? mark, bool invalid)
    {
        ArgumentNullException.ThrowIfNull(r);
        var badges = new List<SchemaBadge>();
        var lines = new List<string>();
        var dimmed = false;
        switch (r.ObjectType)
        {
            case "TABLE":
                TableBadges(mark, badges);
                break;
            case "TRIGGER":
                TriggerBadges(mark, badges);
                break;
            case "SYNONYM":
                if (string.Equals(r.Owner, "PUBLIC", StringComparison.Ordinal))
                {
                    badges.Add(new(BadgeShape.Ring, Teal, BadgeCorner.TopRight, L("PUBLIC 동의어")));
                }

                SynonymBadges(mark, badges);
                break;
            case "MATERIALIZED VIEW":
                MViewBadges(mark, badges);
                break;
            case "TYPE" or "TYPE BODY":
                if (mark?.SubKind == "VARRAY")
                {
                    badges.Add(new(BadgeShape.Square, Teal, BadgeCorner.BottomRight, L("VARRAY 타입")));
                }
                else if (mark?.SubKind == "NESTED_TABLE")
                {
                    badges.Add(new(BadgeShape.Bars, Blue, BadgeCorner.BottomRight, L("중첩 테이블 타입")));
                }

                break;
            case "JOB" or "SCHEDULER JOB":
                JobBadges(mark, badges);
                break;
        }

        if (mark?.StateCode == "DISABLED")
        {
            dimmed = true;
            lines.Add(L("꺼짐(DISABLED)"));
        }

        if (invalid)
        {
            lines.Add(L("INVALID: 컴파일 오류가 있거나 다시 컴파일해야 한다"));
        }

        return Build(iconKey, badges, dimmed, lines);
    }

    private static void TableBadges(ObjectMark? mark, List<SchemaBadge> badges)
    {
        if (mark is null)
        {
            return;
        }

        if (mark.Has("IOT"))
        {
            badges.Add(new(BadgeShape.Diamond, Teal, BadgeCorner.BottomRight, L("인덱스 구성 테이블(IOT)")));
        }
        else if (mark.Has("CLUSTER"))
        {
            badges.Add(new(BadgeShape.Ring, Amber, BadgeCorner.BottomRight, L("클러스터 테이블")));
        }
        else if (mark.Has("GTT_SESSION"))
        {
            badges.Add(new(BadgeShape.Dot, Green, BadgeCorner.BottomRight, L("임시 테이블(세션 유지)")));
        }
        else if (mark.Has("GTT_TRANSACTION"))
        {
            badges.Add(new(BadgeShape.Ring, Green, BadgeCorner.BottomRight, L("임시 테이블(트랜잭션 유지)")));
        }
        else if (mark.Has("EXTERNAL"))
        {
            badges.Add(new(BadgeShape.Square, Blue, BadgeCorner.BottomRight, L("외부 테이블")));
        }
        else if (mark.Has("OBJECT"))
        {
            badges.Add(new(BadgeShape.Diamond, Purple, BadgeCorner.BottomRight, L("객체 테이블")));
        }

        if (mark.ValueOf("PART") is { } part)
        {
            badges.Add(new(BadgeShape.Bars, Purple, BadgeCorner.TopRight, L("파티션") + ": " + PartitionText(part)));
        }
    }

    /// <summary><c>RANGE-HASH+INTERVAL</c> → <c>RANGE-HASH, INTERVAL</c>(사전 표기 그대로, 밑줄은 공백).</summary>
    private static string PartitionText(string part) => part.Replace('_', ' ').Replace("+INTERVAL", ", INTERVAL", StringComparison.Ordinal);

    private static void TriggerBadges(ObjectMark? mark, List<SchemaBadge> badges)
    {
        if (mark is null)
        {
            return;
        }

        if (mark.Has("BEFORE"))
        {
            badges.Add(new(BadgeShape.Square, Blue, BadgeCorner.BottomRight, L("BEFORE 트리거")));
        }
        else if (mark.Has("AFTER"))
        {
            badges.Add(new(BadgeShape.Dot, Green, BadgeCorner.BottomRight, L("AFTER 트리거")));
        }
        else if (mark.Has("INSTEAD"))
        {
            badges.Add(new(BadgeShape.Diamond, Purple, BadgeCorner.BottomRight, L("INSTEAD OF 트리거")));
        }
        else if (mark.Has("COMPOUND"))
        {
            badges.Add(new(BadgeShape.Plus, Teal, BadgeCorner.BottomRight, L("복합(COMPOUND) 트리거")));
        }

        if (mark.Has("DDL"))
        {
            badges.Add(new(BadgeShape.Cross, Purple, BadgeCorner.TopRight, L("DDL 이벤트 트리거")));
        }
        else if (mark.Has("DB"))
        {
            badges.Add(new(BadgeShape.Ring, Teal, BadgeCorner.TopRight, L("DB 이벤트 트리거")));
        }
        else if (mark.Has("STATEMENT"))
        {
            badges.Add(new(BadgeShape.Bar, Blue, BadgeCorner.TopRight, L("문장 단위(FOR EACH STATEMENT)")));
        }
        else if (mark.Has("ROW"))
        {
            // 행 단위는 DML 트리거의 기본이라 뱃지를 붙이지 않고 툴팁 줄만 남긴다(주 뱃지 줄에 덧붙인다).
            if (badges.Count > 0 && badges[^1].Corner == BadgeCorner.BottomRight)
            {
                badges[^1] = badges[^1] with { Tip = badges[^1].Tip + " · " + L("행 단위(FOR EACH ROW)") };
            }
        }
    }

    private static void SynonymBadges(ObjectMark? mark, List<SchemaBadge> badges)
    {
        if (mark is null)
        {
            return;
        }

        if (mark.Has("REMOTE"))
        {
            badges.Add(new(BadgeShape.Plus, Green, BadgeCorner.BottomRight, L("원격 객체(DB 링크)")));
        }
        else if (mark.ValueOf("TARGET") is { } target)
        {
            var type = target.Replace('_', ' ');
            var tip = L("대상") + ": " + type;
            badges.Add(type switch
            {
                "TABLE" or "VIEW" or "MATERIALIZED VIEW" => new(BadgeShape.Square, Blue, BadgeCorner.BottomRight, tip),
                "PACKAGE" or "PROCEDURE" or "FUNCTION" or "TYPE" or "JAVA CLASS" or "JAVA SOURCE" => new(BadgeShape.Dot, Purple, BadgeCorner.BottomRight, tip),
                "SEQUENCE" => new(BadgeShape.Diamond, Amber, BadgeCorner.BottomRight, tip),
                "SYNONYM" => new(BadgeShape.Ring, Teal, BadgeCorner.BottomRight, tip),
                _ => new(BadgeShape.Dot, Teal, BadgeCorner.BottomRight, tip),
            });
        }

        if (mark.StateCode == "BROKEN")
        {
            badges.Add(new(BadgeShape.Warning, Orange, BadgeCorner.BottomLeft, L("대상이 없다(깨진 동의어, 볼 권한이 없을 수도 있다)")));
        }
    }

    private static void MViewBadges(ObjectMark? mark, List<SchemaBadge> badges)
    {
        if (mark is null)
        {
            return;
        }

        if (mark.Has("FAST"))
        {
            badges.Add(new(BadgeShape.Dot, Green, BadgeCorner.BottomRight, L("빠른 갱신(FAST)")));
        }
        else if (mark.Has("COMPLETE"))
        {
            badges.Add(new(BadgeShape.Square, Blue, BadgeCorner.BottomRight, L("전체 갱신(COMPLETE)")));
        }
        else if (mark.Has("FORCE"))
        {
            badges.Add(new(BadgeShape.Diamond, Purple, BadgeCorner.BottomRight, L("갱신 방식 자동(FORCE)")));
        }
        else if (mark.Has("NEVER"))
        {
            badges.Add(new(BadgeShape.Cross, Teal, BadgeCorner.BottomRight, L("갱신 안 함(NEVER)")));
        }

        if (mark.Has("COMMIT"))
        {
            badges.Add(new(BadgeShape.Plus, Amber, BadgeCorner.TopRight, L("커밋 때 갱신(ON COMMIT)")));
        }

        if (mark.StateCode == "STALE")
        {
            badges.Add(new(BadgeShape.Warning, Orange, BadgeCorner.BottomLeft, L("갱신이 필요하다(STALE)") + (mark.StateDetail is { } d ? " · " + d : string.Empty)));
        }
    }

    private static void JobBadges(ObjectMark? mark, List<SchemaBadge> badges)
    {
        switch (mark?.StateCode)
        {
            case "RUNNING":
                badges.Add(new(BadgeShape.Play, Green, BadgeCorner.BottomRight, L("실행 중")));
                break;
            case "BROKEN":
                badges.Add(new(BadgeShape.Warning, Orange, BadgeCorner.BottomLeft, L("고장(BROKEN)")));
                break;
            case "FAILED":
                badges.Add(new(BadgeShape.Warning, Orange, BadgeCorner.BottomLeft, L("실패") + (mark.StateDetail is { } d ? " · " + d : string.Empty)));
                break;
        }
    }

    /// <summary>테이블 펼침의 열. <paramref name="isPrimaryKey"/> = 기본키 열(제약에서 안다).</summary>
    public static SchemaLook ForColumn(ColumnInfo c, bool isPrimaryKey)
    {
        ArgumentNullException.ThrowIfNull(c);
        var badges = new List<SchemaBadge>();
        var lines = new List<string>();
        if (isPrimaryKey)
        {
            badges.Add(new(BadgeShape.Diamond, Amber, BadgeCorner.BottomRight, L("기본키 열")));
        }
        else if (!c.IsNullable)
        {
            badges.Add(new(BadgeShape.Bar, Blue, BadgeCorner.TopRight, L("NOT NULL")));
        }
        else
        {
            lines.Add(L("NULL 허용"));
        }

        if (c.IsIdentity)
        {
            badges.Add(new(BadgeShape.Plus, Teal, BadgeCorner.TopLeft, L("IDENTITY 열")));
        }
        else if (c.IsVirtual)
        {
            badges.Add(new(BadgeShape.Ring, Purple, BadgeCorner.TopLeft, L("가상 열")));
        }
        else if (IsLob(c.DataType))
        {
            badges.Add(new(BadgeShape.Square, Green, BadgeCorner.TopLeft, L("LOB 열")));
        }

        if (c.IsInvisible)
        {
            lines.Add(L("숨은 열(INVISIBLE)"));
        }

        return Build("column", badges, c.IsInvisible, lines);
    }

    private static bool IsLob(string dataType) => dataType is "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW";

    /// <summary>테이블 펼침의 인덱스.</summary>
    public static SchemaLook ForIndex(IndexInfo i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var badges = new List<SchemaBadge>();
        var type = i.IndexType ?? string.Empty;
        if (type.Contains("BITMAP", StringComparison.Ordinal))
        {
            badges.Add(new(BadgeShape.Square, Purple, BadgeCorner.BottomRight, L("비트맵 인덱스")));
        }

        if (i.Expressions.Any(e => e is not null))
        {
            badges.Add(new(BadgeShape.Diamond, Teal, badges.Count == 0 ? BadgeCorner.BottomRight : BadgeCorner.TopLeft, L("함수 기반 인덱스")));
        }
        else if (type.EndsWith("/REV", StringComparison.Ordinal))
        {
            badges.Add(new(BadgeShape.Play, Amber, BadgeCorner.BottomRight, L("역방향(REVERSE) 인덱스")));
        }

        if (i.IsUnique)
        {
            badges.Add(new(BadgeShape.Dot, Blue, badges.Any(b => b.Corner == BadgeCorner.TopLeft) ? BadgeCorner.BottomRight : BadgeCorner.TopLeft, L("유니크 인덱스")));
        }

        if (i.IsPartitioned)
        {
            badges.Add(new(BadgeShape.Bars, Purple, BadgeCorner.TopRight, L("파티션 인덱스")));
        }

        if (i.IsUnusable)
        {
            badges.Add(new(BadgeShape.Warning, Orange, BadgeCorner.BottomLeft, L("쓸 수 없음(UNUSABLE)")));
        }

        return Build("objIndex", badges, false, []);
    }

    /// <summary>테이블 펼침의 제약 조건. NOT NULL 은 열 정보에 있어 제약 목록에서 빠지지만(meta.constraints), 들어오면 CHECK 와 따로 보인다.</summary>
    public static SchemaLook ForConstraint(ConstraintInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var badges = new List<SchemaBadge>();
        var lines = new List<string>();
        badges.Add(c.Kind switch
        {
            ConstraintKind.PrimaryKey => new(BadgeShape.Diamond, Blue, BadgeCorner.BottomRight, L("기본키(PK)")),
            ConstraintKind.Unique => new(BadgeShape.Dot, Teal, BadgeCorner.BottomRight, L("유니크(UK)")),
            ConstraintKind.ForeignKey => new(BadgeShape.Play, Purple, BadgeCorner.BottomRight, L("외래키(FK)")),
            _ when IsNotNull(c.Condition) => new(BadgeShape.Bar, Neutral, BadgeCorner.BottomRight, L("NOT NULL")),
            _ => new(BadgeShape.Square, Green, BadgeCorner.BottomRight, L("검사(CHECK)")),
        });
        if (!c.IsValidated)
        {
            badges.Add(new(BadgeShape.Ring, Neutral, BadgeCorner.TopRight, L("기존 행을 검사하지 않음(NOVALIDATE)")));
        }

        if (!c.IsEnabled)
        {
            lines.Add(L("꺼짐(DISABLED)"));
        }

        return Build("key", badges, !c.IsEnabled, lines);
    }

    /// <summary>NOT NULL 검사 조건(<c>"C" IS NOT NULL</c>, 앞뒤 괄호·공백은 무시).</summary>
    private static bool IsNotNull(string? condition)
        => condition is not null && condition.Trim().Trim('(', ')').Trim() is var s
            && s.EndsWith(" IS NOT NULL", StringComparison.OrdinalIgnoreCase) && s.Count(ch => ch == '"') == 2 && s.StartsWith('"');

    private static SchemaLook Build(string? iconKey, List<SchemaBadge> badges, bool dimmed, List<string> lines)
    {
        // 같은 자리에 둘이 오면 뒤의 것은 툴팁에만 남긴다(겹쳐 그리지 않는다).
        var shown = new List<SchemaBadge>();
        foreach (var b in badges)
        {
            if (shown.All(s => s.Corner != b.Corner))
            {
                shown.Add(b);
            }
        }

        var tip = badges.Select(b => b.Tip).Concat(lines).ToList();
        return new SchemaLook(iconKey, shown, dimmed, tip.Count == 0 ? null : string.Join('\n', tip));
    }
}
