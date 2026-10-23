using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

// OGNL OgnlOps 를 옮긴 값 연산(MyBatis 3.5.19 에 든 판): 수 형 정하기·형 바꾸기·같음·크기·더하기·부호·참 판정·글자 표현.
// 원칙: 계산하면 MyBatis 와 같은 값·같은 형을 낸다. Java 형으로만 낼 수 있는 결과(BigInteger)는 계산 못 함으로 돌려준다.
// 정답 = tests/Oranda.Language.Tests/MyBatis/Golden/Ognl.golden.json·Bind.golden.json.
internal static partial class OgnlLite
{
    /// <summary>OgnlOps 의 수 형 번호(작을수록 좁다). 순서가 규칙이라 바꾸지 않는다.</summary>
    private enum NType { Bool, Byte, Char, Short, Int, Long, BigInt, Float, Double, BigDec, NonNumeric }

    private static NType TypeOf(object? v) => v switch
    {
        bool => NType.Bool,
        sbyte or byte => NType.Byte,
        char => NType.Char,
        short => NType.Short,
        int or ushort => NType.Int,
        long or uint => NType.Long,
        ulong => NType.BigInt,
        float => NType.Float,
        double => NType.Double,
        decimal => NType.BigDec,
        _ => NType.NonNumeric,
    };

    /// <summary>java.lang.Number 인가(bool·char 는 아니다).</summary>
    private static bool IsJavaNumber(object? v) => TypeOf(v) is not (NType.Bool or NType.Char or NType.NonNumeric);

    /// <summary>OgnlOps.getNumericType(t1, t2, canBeNonNumeric).</summary>
    private static NType NumericType(NType t1, NType t2, bool canBeNonNumeric)
    {
        if (t1 == t2)
        {
            return t1;
        }

        if (canBeNonNumeric && (t1 == NType.NonNumeric || t2 == NType.NonNumeric || t1 == NType.Char || t2 == NType.Char))
        {
            return NType.NonNumeric;
        }

        if (t1 == NType.NonNumeric)
        {
            t1 = NType.Double;
        }

        if (t2 == NType.NonNumeric)
        {
            t2 = NType.Double;
        }

        if (t1 >= NType.Float)
        {
            if (t2 >= NType.Float)
            {
                return (NType)Math.Max((int)t1, (int)t2);
            }

            if (t2 < NType.Int)
            {
                return t1;
            }

            return t2 == NType.BigInt ? NType.BigDec : (NType)Math.Max((int)NType.Double, (int)t1);
        }

        if (t2 >= NType.Float)
        {
            if (t1 < NType.Int)
            {
                return t2;
            }

            return t1 == NType.BigInt ? NType.BigDec : (NType)Math.Max((int)NType.Double, (int)t2);
        }

        return (NType)Math.Max((int)t1, (int)t2);
    }

    // ---- 형 바꾸기 ----

    private static long LongValue(object? v, TextRange at)
    {
        switch (v)
        {
            case null:
                return 0;
            case bool b:
                return b ? 1 : 0;
            case char c:
                return c;
            case sbyte or byte or short or ushort or int or uint or long:
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            case float f:
                return JavaDoubleToLong(f);
            case double d:
                return JavaDoubleToLong(d);
            case decimal m:
                {
                    // BigDecimal.longValue: 소수점 아래를 버리고 아래 64비트만 남긴다.
                    var t = decimal.Truncate(m);
                    return t is >= long.MinValue and <= long.MaxValue ? (long)t : unchecked((long)(ulong)((BigInteger)t & ulong.MaxValue));
                }

            case ulong:
                throw BigIntegerFailure(at);
        }

        var s = JavaTrim(JavaString(v));
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) ? l : throw NumberFormat(s, at);
    }

    // Java (long) 변환: NaN = 0, 넘치면 끝값.
    private static long JavaDoubleToLong(double d) =>
        double.IsNaN(d) ? 0 : d >= 9.2233720368547758E18 ? long.MaxValue : d <= -9.2233720368547758E18 ? long.MinValue : (long)d;

    private static double DoubleValue(object? v, TextRange at)
    {
        switch (v)
        {
            case null:
                return 0;
            case bool b:
                return b ? 1 : 0;
            case char c:
                return c;
            case float f:
                return f;
            case double d:
                return d;
            case decimal m:
                return (double)m;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        var s = JavaTrim(JavaString(v));
        return s.Length == 0 ? 0 : JavaParseDouble(s, at);
    }

    [GeneratedRegex(@"^[+-]?(NaN|Infinity|((\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)[fFdD]?)$")]
    private static partial Regex JavaDoubleRegex();

    // Double.parseDouble: 앞뒤 공백을 떼고, 끝의 f·F·d·D 접미사와 NaN·Infinity 를 받는다.
    private static double JavaParseDouble(string s, TextRange at)
    {
        var t = JavaTrim(s);
        if (!JavaDoubleRegex().IsMatch(t))
        {
            throw NumberFormat(s, at);
        }

        var body = t[^1] is 'f' or 'F' or 'd' or 'D' && !t.EndsWith("Infinity", StringComparison.Ordinal) ? t[..^1] : t;
        bool negative = body.StartsWith('-');
        var unsigned = body.TrimStart('+', '-');
        double value = unsigned switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            _ => double.Parse(unsigned, NumberStyles.Float, CultureInfo.InvariantCulture),
        };
        if (t[^1] is 'f' or 'F')
        {
            value = (float)value;
        }

        return negative ? -value : value;
    }

    private static decimal BigDecValue(object? v, TextRange at)
    {
        switch (v)
        {
            case null:
                return 0m;
            case decimal m:
                return m;
            case bool b:
                return b ? 1m : 0m;
            case char c:
                return c;
            case ulong:
                throw BigIntegerFailure(at);
        }

        // new BigDecimal(stringValue(v, true)): 숫자 글자만 받는다(NaN·Infinity·접미사는 NumberFormatException).
        var s = JavaTrim(JavaString(v));
        if (!BigDecimalRegex().IsMatch(s))
        {
            throw NumberFormat(s, at);
        }

        try
        {
            return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw Err($"BigDecimal {s} 은 decimal 범위를 넘어 계산하지 않는다", at);
        }
    }

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex BigDecimalRegex();

    // ---- 참 판정 ----

    /// <summary>OgnlOps.booleanValue: !·not·and·or 가 쓴다. 글자는 Boolean.parseBoolean("true" 만 참, 대소문자 무시).</summary>
    private static bool OgnlBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
        char c => c != '\0',
        _ when IsJavaNumber(v) => DoubleValue(v, default) != 0,
        _ => true,
    };

    /// <summary>MyBatis ExpressionEvaluator.evaluateBoolean: bool 은 그대로, 수는 0 이 아니면 참, 그 밖은 null 이 아니면 참.</summary>
    private static bool MyBatisBool(object? v, TextRange at)
    {
        if (v is bool b)
        {
            return b;
        }

        if (IsJavaNumber(v))
        {
            // new BigDecimal(String.valueOf(value)) — NaN·Infinity 는 NumberFormatException.
            return BigDecValue(JavaString(v), at) != 0;
        }

        return v is not null;
    }

    // ---- 같음·크기 ----

    /// <summary>OgnlOps.equal(==, eq). != 는 이것의 부정이다.</summary>
    private static bool Equal(object? v1, object? v2, TextRange at)
    {
        if (v1 is null)
        {
            return v2 is null;
        }

        if (ReferenceEquals(v1, v2) || IsEqual(v1, v2, at))
        {
            return true;
        }

        return IsJavaNumber(v1) && IsJavaNumber(v2) && DoubleValue(v1, at) == DoubleValue(v2, at);
    }

    private static bool IsEqual(object? v1, object? v2, TextRange at)
    {
        if (ReferenceEquals(v1, v2))
        {
            return true;
        }

        var t1 = TypeOf(v1);
        var t2 = TypeOf(v2);
        if (t1 == NType.NonNumeric && t2 == NType.NonNumeric && (v1 is not string || v2 is not string))
        {
            // 비교할 수 없는(Comparable 아닌) 값은 equals 만 쓴다.
            return v1 is not null && JavaEquals(v1, v2);
        }

        return v1 is not null && v2 is not null && (JavaEquals(v1, v2) || CompareWithConversion(v1, v2, at) == 0);
    }

    /// <summary>OgnlOps.compareWithConversion(&lt; &lt;= &gt; &gt;= 와 같음이 쓴다).</summary>
    private static int CompareWithConversion(object? v1, object? v2, TextRange at)
    {
        if (ReferenceEquals(v1, v2))
        {
            return 0;
        }

        var t1 = TypeOf(v1);
        var t2 = TypeOf(v2);
        switch (NumericType(t1, t2, canBeNonNumeric: true))
        {
            case NType.BigInt:
                throw BigIntegerFailure(at);
            case NType.BigDec:
                return BigDecValue(v1, at).CompareTo(BigDecValue(v2, at));
            case NType.NonNumeric when t1 == NType.NonNumeric && t2 == NType.NonNumeric:
                if (v1 is null || v2 is null)
                {
                    throw Err($"MyBatis 3.5 도 실행 때 예외(NullPointerException): null 과 크기를 비교할 수 없다({(v1 is null ? "왼쪽" : "오른쪽")} 값이 null)", at);
                }

                if (v1 is string s1 && v2 is string s2)
                {
                    return Math.Sign(string.CompareOrdinal(s1, s2));
                }

                throw Err($"MyBatis 3.5 도 실행 때 예외(IllegalArgumentException): {TypeName(v1)} 과 {TypeName(v2)} 은 크기를 비교할 수 없다", at);
            case NType.NonNumeric:
            case NType.Float:
            case NType.Double:
                {
                    double d1 = DoubleValue(v1, at);
                    double d2 = DoubleValue(v2, at);
                    return d1 == d2 ? 0 : d1 < d2 ? -1 : 1;
                }

            default:
                {
                    long l1 = LongValue(v1, at);
                    long l2 = LongValue(v2, at);
                    return l1 == l2 ? 0 : l1 < l2 ? -1 : 1;
                }
        }
    }

    /// <summary>Java <c>equals()</c>: 형까지 같아야 참(int 와 long, string 과 char, 1.5 와 1.50(BigDecimal) 은 다름). 목록·사전은 항목별로.</summary>
    private static bool JavaEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        switch (a)
        {
            case string sa:
                return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case decimal da:
                return b is decimal db && da == db && da.Scale == db.Scale;
            case double fa:
                return b is double fb && fa.Equals(fb) && double.IsNegative(fa) == double.IsNegative(fb);
            case float ga:
                return b is float gb && ga.Equals(gb) && float.IsNegative(ga) == float.IsNegative(gb);
        }

        if (IsDict(a))
        {
            if (!IsDict(b) || CountOf(a) != CountOf(b))
            {
                return false;
            }

            foreach (var kv in Entries(a))
            {
                if (!TryDictGet(b, kv.Key, out var other) || !JavaEquals(kv.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        if (IsList(a))
        {
            if (!IsList(b))
            {
                return false;
            }

            var ea = ((IEnumerable)a).GetEnumerator();
            var eb = ((IEnumerable)b).GetEnumerator();
            while (true)
            {
                bool ha = ea.MoveNext();
                bool hb = eb.MoveNext();
                if (ha != hb)
                {
                    return false;
                }

                if (!ha)
                {
                    return true;
                }

                if (!JavaEquals(ea.Current, eb.Current))
                {
                    return false;
                }
            }
        }

        return a.GetType() == b.GetType() && a.Equals(b);
    }

    // ---- 더하기·부호 ----

    /// <summary>OgnlOps.add: 글자가 끼면 잇고(null 은 "null"), 수·bool·char 와 null 은 NullPointerException, 수는 넓은 형으로 더한다(int 는 넘치면 돈다).</summary>
    private static object Add(object? v1, object? v2, TextRange at)
    {
        var t1 = TypeOf(v1);
        var t2 = TypeOf(v2);
        var type = NumericType(t1, t2, canBeNonNumeric: true);
        switch (type)
        {
            case NType.BigInt:
                throw BigIntegerFailure(at);
            case NType.BigDec:
                try
                {
                    return BigDecValue(v1, at) + BigDecValue(v2, at);
                }
                catch (OverflowException)
                {
                    throw Err("BigDecimal 합이 decimal 범위를 넘어 계산하지 않는다", at);
                }

            case NType.Float:
            case NType.Double:
                return NewReal(type, DoubleValue(v1, at) + DoubleValue(v2, at));
            case NType.NonNumeric:
                if ((t1 != NType.NonNumeric && v2 is null) || (t2 != NType.NonNumeric && v1 is null))
                {
                    throw Err($"MyBatis 3.5 도 실행 때 예외(NullPointerException): Can't add values {JavaString(v1)} , {JavaString(v2)}", at);
                }

                return JavaString(v1) + JavaString(v2);
            default:
                return NewInteger(type, unchecked(LongValue(v1, at) + LongValue(v2, at)), at);
        }
    }

    /// <summary>OgnlOps.negate.</summary>
    private static object Negate(object? v, TextRange at)
    {
        var type = TypeOf(v);
        return type switch
        {
            NType.BigInt => throw BigIntegerFailure(at),
            NType.BigDec => -BigDecValue(v, at),
            NType.Float or NType.Double => NewReal(type, -DoubleValue(v, at)),
            _ => NewInteger(type, unchecked(-LongValue(v, at)), at),
        };
    }

    // OgnlOps.newInteger: bool·char·int 는 int, byte·short 는 그 형, long 은 long, 그 밖(글자·null 의 부호)은 BigInteger.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Java 의 박싱된 수처럼 형이 갈린다(int·sbyte·short·long, float·double)")]
    private static object NewInteger(NType type, long value, TextRange at) => type switch
    {
        // 갈래마다 object 로: C# switch 식은 공통 형(long)으로 묶어 버린다.
        NType.Bool or NType.Char or NType.Int => (object)unchecked((int)value),
        NType.Byte => (object)unchecked((sbyte)value),
        NType.Short => (object)unchecked((short)value),
        NType.Long => (object)value,
        _ => throw BigIntegerFailure(at),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Java 의 박싱된 수처럼 형이 갈린다(int·sbyte·short·long, float·double)")]
    private static object NewReal(NType type, double value) => type == NType.Float ? (object)(float)value : (object)value;

    private static OgnlEvalException BigIntegerFailure(TextRange at) =>
        Err("MyBatis 는 이 값을 BigInteger 로 계산한다(Oranda 값 형에 없어 계산하지 않는다)", at);

    private static OgnlEvalException NumberFormat(string s, TextRange at) =>
        s.Length == 1
            ? Err($"MyBatis 3.5 도 실행 때 예외(NumberFormatException): For input string: \"{s}\"(한 글자 '{s}' 는 char 일 수 있다 — 글자와 char 는 수로 바꿔 비교한다)", at)
            : Err($"MyBatis 3.5 도 실행 때 예외(NumberFormatException): For input string: \"{s}\"", at);

    // ---- 글자 표현(String.valueOf) ----

    /// <summary>Java <c>String.valueOf</c> 와 같은 글자. null = "null", double·float 는 Java 표기(1.0, 1.0E-4), 목록 [a, b], 사전 {k=v}.</summary>
    internal static string JavaString(object? v)
    {
        switch (v)
        {
            case null:
                return "null";
            case string s:
                return s;
            case char c:
                return c.ToString();
            case bool b:
                return b ? "true" : "false";
            case float f:
                return JavaReal(f, f.ToString("R", CultureInfo.InvariantCulture));
            case double d:
                return JavaReal(d, d.ToString("R", CultureInfo.InvariantCulture));
            case decimal m:
                return JavaBigDecimal(m);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture);
        }

        if (IsDict(v))
        {
            return "{" + string.Join(", ", Entries(v).Select(e => e.Key + "=" + JavaString(e.Value))) + "}";
        }

        if (IsList(v))
        {
            return "[" + string.Join(", ", ((IEnumerable)v).Cast<object?>().Select(JavaString)) + "]";
        }

        return v.ToString() ?? string.Empty;
    }

    // Double.toString·Float.toString: 10^-3 이상 10^7 미만은 소수(끝에 .0), 그 밖은 d.dddE±n. 숫자는 가장 짧은 왕복 표기.
    private static string JavaReal(double value, string shortest)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var (digits, exponent) = Digits(shortest.TrimStart('-'));
        var sign = value < 0 ? "-" : string.Empty;
        double abs = Math.Abs(value);
        if (abs >= 1e-3 && abs < 1e7)
        {
            if (exponent <= 0)
            {
                return sign + "0." + new string('0', -exponent) + digits;
            }

            var whole = digits.Length <= exponent ? digits + new string('0', exponent - digits.Length) : digits[..exponent];
            var fraction = digits.Length <= exponent ? "0" : digits[exponent..];
            return sign + whole + "." + fraction;
        }

        return sign + digits[0] + "." + (digits.Length > 1 ? digits[1..] : "0") + "E" + (exponent - 1).ToString(CultureInfo.InvariantCulture);
    }

    // "1.2345E+20"·"0.00015"·"123.4" → 유효 숫자(앞뒤 0 없음)와 지수(값 = 0.digits × 10^exponent).
    private static (string Digits, int Exponent) Digits(string text)
    {
        int e = text.IndexOfAny(['E', 'e']);
        int exp10 = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? text : text[..e];
        int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var raw = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int point = dot < 0 ? mantissa.Length : dot;
        int lead = 0;
        while (lead < raw.Length - 1 && raw[lead] == '0')
        {
            lead++;
        }

        var digits = raw[lead..].TrimEnd('0');
        return (digits.Length == 0 ? "0" : digits, point - lead + exp10);
    }

    // BigDecimal.toString: 조정 지수가 -6 미만이면 과학 표기(1E-7), 아니면 소수(자릿수 그대로).
    private static string JavaBigDecimal(decimal m)
    {
        int scale = m.Scale;
        var unscaled = (new BigInteger(Math.Abs(m) * Pow10(scale))).ToString(CultureInfo.InvariantCulture);
        int adjusted = -scale + (unscaled.Length - 1);
        if (scale == 0 || adjusted >= -6)
        {
            return m.ToString(CultureInfo.InvariantCulture);
        }

        var sign = m < 0 ? "-" : string.Empty;
        return sign + unscaled[0] + (unscaled.Length > 1 ? "." + unscaled[1..] : string.Empty) + "E" + adjusted.ToString(CultureInfo.InvariantCulture);
    }

    private static decimal Pow10(int n)
    {
        decimal p = 1m;
        for (int i = 0; i < n; i++)
        {
            p *= 10m;
        }

        return p;
    }
}
