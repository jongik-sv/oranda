using Oranda.Core.Connection;
using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer.Copy.CrossDbms;

/// <summary>열 하나의 교차 판정 갈래.</summary>
public enum CrossMapKind
{
    /// <summary>대상 DBMS 에 같은 형식이 있다(또는 같은 DBMS).</summary>
    Exact,

    /// <summary>가장 가까운 형식으로 바꿔 옮긴다(<see cref="CrossColumnMapping.Note"/> 가 이유).</summary>
    Converted,

    /// <summary>옮길 형식이 없어 그 열을 뺀다(<see cref="CrossColumnMapping.DropReason"/> 이 이유). 사전 점검 <c>ColumnDropped</c> Warning + 실행 로그.</summary>
    Dropped,
}

/// <summary><see cref="CrossMapKind.Converted"/> 의 이유(사전 점검 안내 문구의 열쇠).</summary>
public enum CrossNote
{
    None,

    /// <summary>BINARY_FLOAT·BINARY_DOUBLE → NUMBER 글자 바인드(Oracle→Tibero, D6). NaN·±Inf 값은 행 실패.</summary>
    BinaryAsNumber,

    /// <summary>TIMESTAMP WITH TIME ZONE 의 지역 이름이 그 시각의 오프셋으로 바뀐다(Oracle→Tibero, D7).</summary>
    TimeZoneRegionToOffset,

    /// <summary>RAW(2000 바이트 넘는 EXTENDED) → BLOB.</summary>
    RawToBlob,

    /// <summary>VARCHAR2(4000 바이트 넘음) → CLOB(Tibero→Oracle, D9).</summary>
    LongStringToClob,

    /// <summary>NVARCHAR2·NCHAR(Oracle 한도 넘음) → NCLOB(Tibero→Oracle).</summary>
    LongNationalStringToNClob,

    /// <summary>LONG → CLOB(Tibero→Oracle, D10).</summary>
    LongToClob,

    /// <summary>LONG RAW → BLOB(Tibero→Oracle, D10).</summary>
    LongRawToBlob,

    /// <summary>IDENTITY 열이 일반 열이 된다(Tibero 에 IDENTITY 없음). 표 만들 때만 해당.</summary>
    IdentityToPlain,
}

/// <summary><see cref="CrossMapKind.Dropped"/> 의 이유.</summary>
public enum CrossDropReason
{
    None,

    /// <summary>Oracle LONG·LONG RAW: 대량 읽기 미지원(D10).</summary>
    LongSource,

    /// <summary>ROWID·UROWID: 다른 DB 에서 뜻이 없다.</summary>
    RowId,

    /// <summary>TIMESTAMP WITH LOCAL TIME ZONE: 양방향 제외(D8).</summary>
    LocalTimeZone,

    /// <summary>대량 읽기 미지원 형식(BFILE·XMLTYPE·JSON·VECTOR·BOOLEAN·객체·컬렉션·SDO_GEOMETRY 등) 또는 모르는 형식.</summary>
    Unreadable,
}

/// <summary>
/// 열 하나의 교차 판정 결과.
/// </summary>
/// <param name="Kind">판정 갈래.</param>
/// <param name="Target">
/// 대상 DBMS 에서의 열 정의(표 만들기 DDL 용). 이름·위치·널 허용·기본값은 원본 그대로이고 <c>DataType</c>·길이·정밀도·IDENTITY 만 바뀐다(<see cref="CrossMapKind.Dropped"/> 면 null).
/// </param>
/// <param name="BindType">
/// 값 바인드 형식 = <b>원본 형식을 대상 DBMS 로 옮긴 형식</b>(정밀도·길이 괄호 없는 대문자 이름, 예 <c>NUMBER</c>·<c>CLOB</c>). 대상 열 형식이 아니다 — 차이는 서버가 바꾼다(D3).
/// <see cref="CrossMapKind.Dropped"/> 면 null.
/// </param>
/// <param name="Note"><see cref="CrossMapKind.Converted"/> 의 이유. 그 밖에는 <see cref="CrossNote.None"/>.</param>
/// <param name="DropReason"><see cref="CrossMapKind.Dropped"/> 의 이유. 그 밖에는 <see cref="CrossDropReason.None"/>.</param>
public sealed record CrossColumnMapping(CrossMapKind Kind, ColumnInfo? Target, string? BindType, CrossNote Note, CrossDropReason DropReason)
{
    public bool IsDropped => Kind == CrossMapKind.Dropped;
}

/// <summary>
/// 교차 복사(Oracle↔Tibero)의 형식 지도. 순수 함수 — 사전 점검·실행기·DDL 이 같은 표를 쓴다(설계 docs/design/cross-dbms-copy.md 형식 지도).
/// 같은 DBMS 는 아무것도 바꾸지 않는다(<see cref="CrossMapKind.Exact"/>, 열 그대로).
/// 값 단위 실패(NaN·Inf·기원전 날짜·모르는 지역)는 여기가 아니라 값 변환(<see cref="PortableConversionException"/>)에서 난다.
/// </summary>
public static class CrossTypeMap
{
    /// <summary>Tibero VARCHAR2 → Oracle 로 갈 때 CLOB 으로 바꾸는 길이 경계(Oracle VARCHAR2 4000 바이트). 길이 단위를 가를 수 없어 숫자 그대로 비교한다(D9).</summary>
    public const int OracleVarcharMaxLength = 4000;

    /// <summary>Oracle NVARCHAR2 한도(4000 바이트, AL16UTF16 = 문자당 2바이트 → 2000자).</summary>
    public const int OracleNVarcharMaxLength = 2000;

    /// <summary>Oracle NCHAR 한도(2000 바이트 → 1000자).</summary>
    public const int OracleNCharMaxLength = 1000;

    /// <summary>Tibero RAW 한도(2000 바이트). 넘는 Oracle EXTENDED RAW 는 BLOB.</summary>
    public const int TiberoRawMaxLength = 2000;

    /// <summary>
    /// 열 하나의 판정. <paramref name="source"/> 와 <paramref name="target"/> 이 같으면 항상 Exact(변환 없음).
    /// </summary>
    public static CrossColumnMapping Map(Dbms source, Dbms target, ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var name = Normalize(column.DataType);
        if (source == target)
        {
            return new CrossColumnMapping(CrossMapKind.Exact, column, name, CrossNote.None, CrossDropReason.None);
        }

        var mapping = source == Dbms.Oracle ? FromOracle(column, name) : FromTibero(column, name);

        // IDENTITY → 일반 열(Tibero 에는 IDENTITY 가 없다). 형식 변환 이유가 이미 있으면 그쪽을 먼저 알린다.
        return column.IsIdentity && target == Dbms.Tibero && mapping is { Kind: CrossMapKind.Exact }
            ? mapping with { Kind = CrossMapKind.Converted, Note = CrossNote.IdentityToPlain }
            : mapping;
    }

    /// <summary>
    /// 정밀도·길이 괄호(<c>TIMESTAMP(6)</c>, <c>INTERVAL DAY(2) TO SECOND(6)</c>)를 지우고 공백을 하나로, 대문자로. <c>VARCHAR</c>·<c>NVARCHAR</c>(Tibero 사전 이름)는
    /// <c>VARCHAR2</c>·<c>NVARCHAR2</c> 로 통일한다.
    /// </summary>
    public static string Normalize(string serverType)
    {
        ArgumentNullException.ThrowIfNull(serverType);
        var sb = new System.Text.StringBuilder(serverType.Length);
        var depth = 0;
        var lastSpace = true;
        foreach (var ch in serverType)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (depth == 0)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastSpace)
                    {
                        sb.Append(' ');
                        lastSpace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToUpperInvariant(ch));
                    lastSpace = false;
                }
            }
        }

        var name = sb.ToString().TrimEnd();
        return name switch
        {
            "VARCHAR" => "VARCHAR2",
            "NVARCHAR" => "NVARCHAR2",
            _ => name,
        };
    }

    private static CrossColumnMapping FromOracle(ColumnInfo c, string name)
    {
        switch (name)
        {
            case "NUMBER" or "FLOAT" or "VARCHAR2" or "CHAR" or "NVARCHAR2" or "NCHAR" or "CLOB" or "NCLOB" or "BLOB" or "DATE" or "TIMESTAMP"
                or "INTERVAL YEAR TO MONTH" or "INTERVAL DAY TO SECOND":
                return Exact(c, name);
            case "BINARY_FLOAT" or "BINARY_DOUBLE":
                // DDL 은 같은 형식, 바인드는 NUMBER 글자(Tibero BINARY 쓰기를 켜지 않는다, D6).
                return new CrossColumnMapping(CrossMapKind.Converted, Keep(c), "NUMBER", CrossNote.BinaryAsNumber, CrossDropReason.None);
            case "RAW":
                return c.Length > TiberoRawMaxLength
                    ? new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "BLOB"), "BLOB", CrossNote.RawToBlob, CrossDropReason.None)
                    : Exact(c, name);
            case "TIMESTAMP WITH TIME ZONE":
                // 지역 이름은 그 시각의 오프셋으로(D7). 실측(XC5)에서 Tibero TZ 쓰기가 안 되면 Dropped 로 바꾼다.
                return new CrossColumnMapping(CrossMapKind.Converted, Keep(c), name, CrossNote.TimeZoneRegionToOffset, CrossDropReason.None);
            case "TIMESTAMP WITH LOCAL TIME ZONE":
                return Dropped(CrossDropReason.LocalTimeZone);
            case "LONG" or "LONG RAW":
                return Dropped(CrossDropReason.LongSource);
            case "ROWID" or "UROWID":
                return Dropped(CrossDropReason.RowId);
            default:
                return Dropped(CrossDropReason.Unreadable);
        }
    }

    private static CrossColumnMapping FromTibero(ColumnInfo c, string name)
    {
        switch (name)
        {
            case "NUMBER" or "FLOAT" or "CHAR" or "CLOB" or "NCLOB" or "BLOB" or "RAW" or "DATE" or "TIMESTAMP"
                or "BINARY_FLOAT" or "BINARY_DOUBLE" or "TIMESTAMP WITH TIME ZONE" or "INTERVAL YEAR TO MONTH" or "INTERVAL DAY TO SECOND":
                return Exact(c, name);
            case "VARCHAR2":
                return c.Length > OracleVarcharMaxLength
                    ? new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "CLOB"), "CLOB", CrossNote.LongStringToClob, CrossDropReason.None)
                    : Exact(c, name);
            case "NVARCHAR2":
                return c.Length > OracleNVarcharMaxLength
                    ? new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "NCLOB"), "NCLOB", CrossNote.LongNationalStringToNClob, CrossDropReason.None)
                    : Exact(c, name);
            case "NCHAR":
                return c.Length > OracleNCharMaxLength
                    ? new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "NCLOB"), "NCLOB", CrossNote.LongNationalStringToNClob, CrossDropReason.None)
                    : Exact(c, name);
            case "LONG":
                return new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "CLOB"), "CLOB", CrossNote.LongToClob, CrossDropReason.None);
            case "LONG RAW":
                return new CrossColumnMapping(CrossMapKind.Converted, Retype(c, "BLOB"), "BLOB", CrossNote.LongRawToBlob, CrossDropReason.None);
            case "TIMESTAMP WITH LOCAL TIME ZONE":
                return Dropped(CrossDropReason.LocalTimeZone);
            case "ROWID" or "UROWID":
                return Dropped(CrossDropReason.RowId);
            default:
                return Dropped(CrossDropReason.Unreadable);
        }
    }

    private static CrossColumnMapping Exact(ColumnInfo c, string name) =>
        new(CrossMapKind.Exact, Keep(c), name, CrossNote.None, CrossDropReason.None);

    private static CrossColumnMapping Dropped(CrossDropReason reason) => new(CrossMapKind.Dropped, null, null, CrossNote.None, reason);

    /// <summary>
    /// 대상 열 정의(형식 이름 그대로): IDENTITY·도메인을 지운다(교차 DDL 옵션 — Tibero 에 IDENTITY·도메인 없음, Oracle 도메인은 어느 쪽 대상으로도 옮기지 않는다).
    /// <c>DataType</c> 에 든 괄호(<c>TIMESTAMP(9)</c>)와 길이·정밀도는 그대로 둔다.
    /// </summary>
    private static ColumnInfo Keep(ColumnInfo c) => c with { Domain = null, IsIdentity = false };

    /// <summary>대상 열 정의(형식 바꿈): <see cref="Keep"/> + 형식 이름 교체, 길이·정밀도 버림(LOB 는 길이가 없다).</summary>
    private static ColumnInfo Retype(ColumnInfo c, string dataType) =>
        Keep(c) with { DataType = dataType, Length = null, Precision = null, Scale = null };
}
