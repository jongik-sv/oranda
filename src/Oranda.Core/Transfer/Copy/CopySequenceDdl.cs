using System.Globalization;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 원본 시퀀스 속성(<c>copy.sequence_attrs</c>, ALL_SEQUENCES). 숫자는 서버가 낸 글 그대로다(28자리 MAXVALUE 는 long 을 넘는다).
/// </summary>
/// <param name="MinValue">MIN_VALUE. null 이면 절을 빼고 대상 기본값을 쓴다</param>
/// <param name="MaxValue">MAX_VALUE. null 이면 절을 빼고 대상 기본값을 쓴다</param>
/// <param name="Increment">INCREMENT_BY</param>
/// <param name="Cache">CACHE_SIZE. 0·1·null 이면 NOCACHE(Oracle 은 CACHE 1 을 받지 않는다, ORA-04010)</param>
/// <param name="Cycle">CYCLE_FLAG = Y</param>
/// <param name="Order">ORDER_FLAG = Y</param>
/// <param name="StartWith">START WITH = 원본 LAST_NUMBER(캐시가 있으면 실제 다음 값보다 크거나 같다)</param>
public sealed record SequenceSpec(string? MinValue, string? MaxValue, string Increment, string? Cache, bool Cycle, bool Order, string StartWith)
{
    /// <summary>CACHE 절을 쓰지 않고 대상 DBMS 기본 캐시를 쓴다(원본에도 없는 시퀀스를 기본 속성으로 만들 때, SQ3 결정 84). 참이면 <see cref="Cache"/> 는 보지 않는다.</summary>
    public bool DefaultCache { get; init; }

    /// <summary>
    /// 원본에도 없는 시퀀스의 기본 속성(SQ3, 결정 84): START WITH <paramref name="startWith"/>, INCREMENT BY 1, 최소·최대·캐시는 대상 DBMS 기본, NOORDER·NOCYCLE.
    /// </summary>
    public static SequenceSpec Defaults(string startWith) => new(null, null, "1", null, false, false, startWith) { DefaultCache = true };
}

/// <summary>
/// 「없으면 만들기」·「다시 만들기」가 테이블보다 먼저 대상에 만드는 시퀀스 하나(조정 결정 76). 사전 점검이 정하고 실행기가 같은 글을 보낸다.
/// </summary>
/// <param name="Owner">대상 소유자(만들 스키마)</param>
/// <param name="Name">시퀀스 이름(원본과 같다)</param>
/// <param name="Spec">원본 속성</param>
public sealed record CopySequencePlan(string Owner, string Name, SequenceSpec Spec)
{
    /// <summary>
    /// 적재 뒤 시퀀스를 맞출 때 최댓값을 보는 이 테이블의 열. 열 기본값이 이 시퀀스를 쓰는 열이고, 트리거만 쓰는 시퀀스는 숫자 단일 PK 열이다(없으면 비어 맞추지 않는다).
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>열 기본값이 이 시퀀스를 쓰는 열(만들지 못하면 이 열들의 DEFAULT 를 빼고 테이블을 만든다).</summary>
    public IReadOnlyList<string> DefaultColumns { get; init; } = [];

    /// <summary><c>CREATE SEQUENCE</c> 글(<see cref="CopySequenceDdl.Create"/>).</summary>
    public string CreateDdl => CopySequenceDdl.Create(Owner, Name, Spec);
}

/// <summary>
/// 복사가 대상에 만드는 시퀀스의 문장(순수 함수). Oracle·Tibero 가 같은 글을 받는다
/// (Tibero 메타데이터의 <c>TiberoDdlAssembler.Sequence</c> 와 같은 절 순서: MINVALUE·MAXVALUE·INCREMENT BY·START WITH·CACHE·ORDER·CYCLE).
/// </summary>
public static class CopySequenceDdl
{
    /// <summary>Oracle 오름차순 시퀀스의 기본 MAXVALUE(10^28 − 1). 이 값이면 <c>NOMAXVALUE</c> 로 써서 대상 DBMS 의 기본 한도를 쓴다.</summary>
    internal const string OracleDefaultMax = "9999999999999999999999999999";

    /// <summary>Oracle 내림차순 시퀀스의 기본 MINVALUE(−(10^27 − 1)). 이 값이면 <c>NOMINVALUE</c>.</summary>
    internal const string OracleDefaultMin = "-999999999999999999999999999";

    /// <summary>
    /// <c>CREATE SEQUENCE "O"."N" MINVALUE … MAXVALUE … INCREMENT BY … START WITH … CACHE n|NOCACHE ORDER|NOORDER CYCLE|NOCYCLE</c>. <see cref="SequenceSpec.DefaultCache"/> 면 CACHE 절을 쓰지 않는다.
    /// </summary>
    public static string Create(string owner, string name, SequenceSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(spec);
        var parts = new List<string> { "CREATE SEQUENCE " + CopyCommonStatements.Qualified(owner, name) };
        if (Clean(spec.MinValue) is { } min)
        {
            parts.Add(min == OracleDefaultMin ? "NOMINVALUE" : "MINVALUE " + min);
        }

        if (Clean(spec.MaxValue) is { } max)
        {
            parts.Add(max == OracleDefaultMax ? "NOMAXVALUE" : "MAXVALUE " + max);
        }

        parts.Add("INCREMENT BY " + (Clean(spec.Increment) ?? "1"));
        parts.Add("START WITH " + (Clean(spec.StartWith) ?? "1"));
        if (!spec.DefaultCache)
        {
            var cache = Clean(spec.Cache);
            parts.Add(cache is null or "0" or "1" ? "NOCACHE" : "CACHE " + cache);
        }
        parts.Add(spec.Order ? "ORDER" : "NOORDER");
        parts.Add(spec.Cycle ? "CYCLE" : "NOCYCLE");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// <c>copy.sequence_attrs</c> 행(min_value, max_value, increment_by, cycle_flag, order_flag, cache_size, last_number — 숫자는 글)을 속성으로. 증가값이나 다음 값이 없으면 null.
    /// </summary>
    public static SequenceSpec? FromRow(object?[] row, Func<object?, string?> text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        if (row.Length < 7 || Clean(text(row[2])) is not { } increment || Clean(text(row[6])) is not { } last)
        {
            return null;
        }

        return new SequenceSpec(Clean(text(row[0])), Clean(text(row[1])), increment, Clean(text(row[5])), Yes(text(row[3])), Yes(text(row[4])), last);
    }

    /// <summary><c>ALTER SEQUENCE "O"."N" INCREMENT BY n</c>.</summary>
    public static string AlterIncrement(string owner, string name, decimal increment)
        => $"ALTER SEQUENCE {CopyCommonStatements.Qualified(owner, name)} INCREMENT BY {increment.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 적재 뒤 맞추기 계획. 갓 만든(아직 쓰지 않은) 시퀀스의 첫 NEXTVAL 은 <paramref name="start"/> 다. 키 최댓값 <paramref name="maxKey"/> 가 그보다 작으면 맞출 것이 없다(null).
    /// 아니면: NEXTVAL 한 번(= start) → 차이가 있으면 INCREMENT BY (max − start) 로 바꿔 NEXTVAL 한 번(= max) → INCREMENT BY 원래값. 다음 값은 max + 증가값이다.
    /// 내림차순(증가값 ≤ 0)이거나 숫자가 아니면 null(맞추지 않는다). <c>Gap</c> 이 0 이면 INCREMENT BY 를 바꾸지 않는다.
    /// </summary>
    public static (decimal Gap, decimal Next)? Adjustment(SequenceSpec spec, decimal maxKey)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!TryNumber(spec.StartWith, out var start) || !TryNumber(spec.Increment, out var increment) || increment <= 0 || maxKey < start)
        {
            return null;
        }

        return (maxKey - start, maxKey + increment);
    }

    /// <summary>원본 MAXVALUE 를 넘는가(맞추면 다음 값이 한도를 넘어 NEXTVAL 이 실패한다). 한도를 모르면 false.</summary>
    public static bool Exceeds(SequenceSpec spec, decimal next)
        => TryNumber(spec?.MaxValue, out var max) && next > max;

    /// <summary>숫자 글을 decimal 로(지수 표기 포함). 못 읽으면 false.</summary>
    public static bool TryNumber(string? text, out decimal value)
        => decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool Yes(string? flag) => flag?.Trim() is "Y" or "YES";
}
