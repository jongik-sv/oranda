using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// Java 저장 객체(JAVA SOURCE·JAVA CLASS·JAVA RESOURCE) 읽기 — 목록·상세 구역·소스 DDL. 여기는 읽기만 한다(컴파일 문장은 <c>SchemaStatements.Compile</c>, TSK-14-23). 소유: TSK-14-22.
/// <para>
/// 객체 목록 캐시(<see cref="ListObjectsAsync"/>)에는 넣지 않는다: SYS 쪽 Java 클래스가 수만 개라 전체 목록 갱신이 무거워진다.
/// 스키마를 펼칠 때 그 소유자만 따로 묻고(<c>meta.java_objects</c>, 행 수 제한), 서버에 JVM 이 없으면(<c>SYS.DBMS_JAVA</c> 없음) 접속마다 한 번만 보고 묻지 않는다.
/// </para>
/// </summary>
internal sealed partial class OracleMetadataProvider
{
    /// <summary>Java 객체 유형(사전 OBJECT_TYPE).</summary>
    internal static readonly IReadOnlySet<string> JavaTypes = new HashSet<string>(StringComparer.Ordinal) { "JAVA SOURCE", "JAVA CLASS", "JAVA RESOURCE" };

    /// <summary>접속 대상(DbKey)별 JVM 유무. 알아낸 것만 둔다(조회 실패는 다음에 다시 본다).</summary>
    private readonly ConcurrentDictionary<string, bool> _javaAvailable = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<JavaObjectInfo>> ListJavaObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);
        if (q.Owner is null || (q.ObjectTypes.Count > 0 && !q.ObjectTypes.Any(JavaTypes.Contains)) || !await IsJavaAvailableAsync(s, ct))
        {
            return [];
        }

        void Bind(OracleCommand c)
        {
            c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = q.Owner });
            c.Parameters.Add(new OracleParameter("types", OracleDbType.Varchar2)
            {
                Value = q.ObjectTypes.Count == 0 ? DBNull.Value : "|" + string.Join('|', q.ObjectTypes) + "|",
            });
            c.Parameters.Add(new OracleParameter("name_pattern", OracleDbType.Varchar2) { Value = (object?)q.NamePattern ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("invalid_only", OracleDbType.Int32) { Value = q.InvalidOnly ? 1 : 0 });
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = q.Limit ?? int.MaxValue });
        }

        List<object?[]> rows;
        try
        {
            rows = await QueryAsync(s, "meta.java_objects", Bind, ct, sql => sql.Replace("@LONGNAME@", "DBMS_JAVA.LONGNAME(j.object_name)", StringComparison.Ordinal));
        }
        catch (OracleException)
        {
            // DBMS_JAVA 를 부를 권한이 없다: 짧은 이름 그대로 보인다.
            rows = await QueryAsync(s, "meta.java_objects", Bind, ct, sql => sql.Replace("@LONGNAME@", "j.object_name", StringComparison.Ordinal));
        }

        return [.. rows.Select(r =>
        {
            var name = Str(r[1]) ?? string.Empty;
            return new JavaObjectInfo(new DbObjectRef(Str(r[0]) ?? string.Empty, name, Str(r[2]) ?? string.Empty), Str(r[4]) ?? name, Str(r[3]));
        })];
    }

    /// <summary>서버에 JVM 이 있나(<c>SYS.DBMS_JAVA</c> 가 보이나). 접속 대상마다 한 번 묻는다.</summary>
    public async Task<bool> IsJavaAvailableAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_javaAvailable.TryGetValue(s.DbKey, out var known))
        {
            return known;
        }

        var rows = await TryQueryAsync(s, "meta.java_available", _ => { }, ct);
        if (rows is null)
        {
            return false;
        }

        var available = rows.Count > 0 && (Int(rows[0][0]) ?? 0) > 0;
        _javaAvailable[s.DbKey] = available;
        return available;
    }

    /// <summary>짧은 이름 → 긴 이름(<c>DBMS_JAVA.LONGNAME</c>). 풀지 못하면 짧은 이름.</summary>
    private static async Task<string> JavaLongNameAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        var rows = await TryQueryAsync(s, "meta.java_longname", c => c.Parameters.Add("name", obj.Name), ct);
        return rows is { Count: > 0 } && Str(rows[0][0]) is { Length: > 0 } longName ? longName : obj.Name;
    }

    /// <summary>
    /// Java 객체의 상세 구역(클래스 정보·메서드·필드·구현·내부 클래스, 소스에서 나온 클래스, 컴파일 오류, 리소스 안내)을 <paramref name="sections"/> 에 더한다.
    /// 클래스가 같은 스키마의 JAVA SOURCE 에서 왔으면 그 소스 객체를 돌려준다(상세 화면의 「소스 열기」).
    /// 뷰가 없거나(JVM 없음) 권한이 없으면 그 구역에 안내 한 줄을 둔다.
    /// </summary>
    private static async Task<DbObjectRef?> AddJavaSectionsAsync(ISession s, DbObjectRef obj, List<DetailSection> sections, CancellationToken ct)
    {
        var longName = await JavaLongNameAsync(s, obj, ct);
        if (!string.Equals(longName, obj.Name, StringComparison.Ordinal))
        {
            sections.Add(new DetailSection("이름", ["항목", "값"], [["긴 이름", longName], ["짧은 이름(사전)", obj.Name]]));
        }

        void Bind(OracleCommand c)
        {
            c.Parameters.Add("owner", obj.Owner);
            c.Parameters.Add("name", longName);
        }

        DbObjectRef? source = null;
        switch (obj.ObjectType)
        {
            case "JAVA CLASS":
            {
                var cls = await TryQueryAsync(s, "meta.java_class", Bind, ct);
                if (cls is null)
                {
                    sections.Add(Unavailable("Java 클래스"));
                    break;
                }

                if (cls.Count > 0)
                {
                    var r = cls[0];
                    // all_java_classes.source 는 소스 파일 표시(긴 이름의 마지막 조각, 예 Hello)라 소스 객체는 all_java_derivations 로 찾는다.
                    var sourceName = Dash(Str(r[8]));
                    var found = await TryQueryAsync(s, "meta.java_source_object", Bind, ct);
                    if (found is { Count: > 0 } && Str(found[0][0]) is { Length: > 0 } sourceObject)
                    {
                        source = new DbObjectRef(obj.Owner, sourceObject, "JAVA SOURCE");
                    }

                    sections.Add(new DetailSection("Java 클래스", ["항목", "값"],
                    [
                        ["종류", Str(r[0])],
                        ["접근", Str(r[1])],
                        ["추상(abstract)", Str(r[2])],
                        ["final", Str(r[3])],
                        ["static", Str(r[4])],
                        ["내부 클래스", Str(r[5])],
                        ["상위 클래스", JavaTypeName(Str(r[6]))],
                        ["바깥 클래스", JavaTypeName(Str(r[7]))],
                        ["소스 파일", sourceName],
                        ["소스 객체", source is null ? null : $"{source.Owner}.{source.Name}"],
                    ]));
                }

                var methods = await TryQueryAsync(s, "meta.java_methods", Bind, ct);
                var args = methods is { Count: > 0 } ? await TryQueryAsync(s, "meta.java_arguments", Bind, ct) : null;
                sections.Add(methods is null ? Unavailable("메서드") : new DetailSection("메서드", ["#", "이름", "서명", "반환형", "접근", "static", "abstract", "final"],
                    [.. methods.Where(m => Dash(Str(m[1])) is not null).Select(m => (IReadOnlyList<object?>)
                    [
                        Int(m[0]), Str(m[1]), JavaSignature(Str(m[1]), Int(m[0]), Int(m[6]), args),
                        JavaType(Int(m[7]), Str(m[8]), Str(m[9])), Str(m[2]), Str(m[3]), Str(m[4]), Str(m[5]),
                    ])]));

                var fields = await TryQueryAsync(s, "meta.java_fields", Bind, ct);
                sections.Add(fields is null ? Unavailable("필드") : new DetailSection("필드", ["#", "이름", "형식", "접근", "static", "final"],
                    [.. fields.Where(f => Dash(Str(f[1])) is not null).Select(f => (IReadOnlyList<object?>)[Int(f[0]), Str(f[1]), JavaType(Int(f[5]), Str(f[6]), Str(f[7])), Str(f[2]), Str(f[3]), Str(f[4])])]));

                var implements = await TryQueryAsync(s, "meta.java_implements", Bind, ct);
                if (implements is { Count: > 0 })
                {
                    sections.Add(new DetailSection("구현 인터페이스", ["#", "인터페이스"],
                        [.. implements.Select(i => (IReadOnlyList<object?>)[Int(i[0]), JavaTypeName(Str(i[1]))])]));
                }

                var inners = await TryQueryAsync(s, "meta.java_inners", Bind, ct);
                if (inners is { Count: > 0 })
                {
                    sections.Add(new DetailSection("내부 클래스", ["#", "이름", "전체 이름", "접근", "static"],
                        [.. inners.Select(n => (IReadOnlyList<object?>)[Int(n[0]), Str(n[1]), JavaTypeName(Str(n[2])), Str(n[3]), Str(n[4])])]));
                }

                break;
            }

            case "JAVA SOURCE":
            {
                var classes = await TryQueryAsync(s, "meta.java_source_classes", Bind, ct);
                sections.Add(classes is null ? Unavailable("Java 클래스") : new DetailSection("Java 클래스", ["클래스", "종류", "접근", "abstract", "final", "내부"],
                    [.. classes.Select(c => (IReadOnlyList<object?>)[JavaTypeName(Str(c[0])), Str(c[1]), Str(c[2]), Str(c[3]), Str(c[4]), Str(c[5])])]));
                break;
            }

            case "JAVA RESOURCE":
                sections.Add(new DetailSection("Java 리소스", ["항목", "값"],
                [
                    ["이름", longName],
                    ["내용", "리소스 내용(바이트)은 읽지 않는다 — 내보내기(DBMS_JAVA.EXPORT_RESOURCE)는 무거워 이번 판에는 없다"],
                ]));
                break;
        }

        if (obj.ObjectType is "JAVA SOURCE" or "JAVA CLASS")
        {
            IReadOnlyList<CompileError>? errors;
            try
            {
                errors = await ReadJavaCompileErrorsAsync(s, obj, ct);
            }
            catch (OracleException)
            {
                errors = null;
            }

            if (errors is { Count: > 0 })
            {
                sections.Add(new DetailSection("컴파일 오류", ["줄", "열", "종류", "메시지"],
                    [.. errors.Select(e => (IReadOnlyList<object?>)[e.Line, e.Column, e.IsWarning ? "WARNING" : "ERROR", e.Message])]));
            }
        }

        return source;
    }

    /// <summary>JAVA SOURCE 의 DDL: 소스 줄(all_source)을 이어 <c>CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED "소유자"."긴 이름" AS …</c>. 종결자는 <c>/</c> 줄.</summary>
    private static async Task<string> GetJavaSourceDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var longName = await JavaLongNameAsync(s, obj, ct);
        var rows = await QueryAsync(s, "meta.source", c => BindSourceObject(c, obj), ct);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"소스를 찾을 수 없다: {obj.Owner}.{longName} ({obj.ObjectType})");
        }

        return JavaSourceDdl(obj.Owner, longName, rows.Select(r => Str(r[0])), o.SqlTerminator);
    }

    /// <summary>소스 줄을 이어 DDL 글을 만든다. 줄 끝(LF)이 없는 줄에는 붙인다(Java 소스 줄은 줄 끝을 가지지 않을 수 있다).</summary>
    internal static string JavaSourceDdl(string owner, string longName, IEnumerable<string?> lines, bool terminator)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED ")
            .Append(Quote(owner)).Append('.').Append(Quote(longName)).Append(" AS\n");
        foreach (var line in lines)
        {
            var text = line ?? string.Empty;
            sb.Append(text);
            if (!text.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        var ddl = sb.ToString().TrimEnd('\r', '\n', ' ', '\t');
        return terminator ? ddl + "\n/" : ddl;

        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>JAVA CLASS·JAVA RESOURCE 의 DDL 자리 안내(바이트코드·바이트라 글로 된 DDL 이 없다).</summary>
    internal static string JavaBinaryDdlNote(DbObjectRef obj) => obj.ObjectType == "JAVA CLASS"
        ? $"-- {obj.Owner}.{obj.Name}: JAVA CLASS 는 컴파일된 바이트코드라 보일 소스·DDL 이 없다.\n-- 같은 스키마의 JAVA SOURCE 에서 나온 클래스면 상세 탭의 「소스 열기」로 그 소스를 본다."
        : $"-- {obj.Owner}.{obj.Name}: JAVA RESOURCE 는 바이트 내용이라 DDL 로 보이지 않는다(내용 읽기는 이번 판에 없다).";

    /// <summary>사전의 형식 이름(<c>java/lang/String</c>)을 Java 표기(<c>java.lang.String</c>)로.</summary>
    /// 사전은 없는 값(상위·바깥 클래스 없음 등)을 <c>-</c> 로 둔다 — null.
    internal static string? JavaTypeName(string? name) => Dash(name)?.Replace('/', '.');

    /// <summary>사전의 「없음」 표시 <c>-</c> 를 null 로.</summary>
    private static string? Dash(string? value) => value is null || value.Trim() is "-" or "" ? null : value;

    /// <summary>
    /// 원시형(base_type) 또는 클래스(class_name)에 배열 차수만큼 <c>[]</c>. 실제 사전: 원시형이면 클래스 열이 <c>-</c>,
    /// 클래스면 base_type 이 <c>class</c> 다(JVM 서버 확인).
    /// </summary>
    internal static string? JavaType(int? arrayDepth, string? baseType, string? className)
    {
        var name = JavaTypeName(className) ?? Dash(baseType);
        if (name is null)
        {
            return null;
        }

        var depth = Math.Max(0, arrayDepth ?? 0);
        return depth == 0 ? name : name + string.Concat(Enumerable.Repeat("[]", depth));
    }

    /// <summary>메서드 서명 <c>이름(인자형, …)</c>. 인자를 읽지 못했으면 인자 수만 보인다.</summary>
    internal static string? JavaSignature(string? method, int? methodIndex, int? argumentCount, IReadOnlyList<object?[]>? args)
    {
        if (method is null)
        {
            return null;
        }

        if (args is null)
        {
            return argumentCount is > 0 ? $"{method}(… {argumentCount}개)" : $"{method}()";
        }

        var types = args.Where(a => Int(a[0]) == methodIndex).Select(a => JavaType(Int(a[2]), Str(a[3]), Str(a[4])) ?? "?");
        return $"{method}({string.Join(", ", types)})";
    }

    /// <summary>Java 객체의 컴파일 오류(ALL_ERRORS). 사전 이름이 줄었으면 SHORTNAME 으로 찾고, DBMS_JAVA 를 못 부르면 그 이름 그대로 찾는다.</summary>
    private static async Task<IReadOnlyList<CompileError>> ReadJavaCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        var rows = await TryQueryAsync(s, "meta.java_errors", c => BindSourceObject(c, obj), ct)
            ?? await QueryAsync(s, "meta.compile_errors", c => BindSourceObject(c, obj), ct);
        return JavaCompileErrors(rows);
    }

    private static readonly Regex JavacHeader = new(@"^.*?:(?<line>\d+): (?<kind>error|warning): (?<msg>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex JavacCaret = new(@"^\s*\^\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex JavacSummary = new(@"^\d+ (?:error|errors|warning|warnings)$", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Java 객체의 ALL_ERRORS 행을 오류 목록으로 묶는다. 실제 모양(JVM 서버 확인): 줄·열이 0 이고 javac 출력 한 줄이 한 행이다 —
    /// <c>이름:줄: error: 메시지</c>, 문제의 소스 줄, <c>^</c>(열 자리), 설명(<c>symbol: …</c>·<c>location: …</c>), 끝의 <c>1 error</c>.
    /// 줄은 AS 다음 줄이 1줄. 머리 행마다 오류 하나(설명은 괄호로 붙이고 소스 줄·^·요약은 뺀다). <c>Note:</c> 행은 경고(정상 컴파일에도 남는다).
    /// 줄 번호가 있는 행(줄 &gt; 0)은 그대로 쓴다.
    /// </summary>
    internal static List<CompileError> JavaCompileErrors(IReadOnlyList<object?[]> rows)
    {
        var result = new List<CompileError>();
        JavacEntry? current = null;
        string? pending = null; // 머리 다음 행: ^ 가 뒤따르면 소스 줄, 아니면 설명

        void Flush()
        {
            if (current is not null)
            {
                if (pending is { Length: > 0 })
                {
                    current.Details.Add(pending);
                }

                var message = current.Details.Count == 0 ? current.Message : $"{current.Message} ({string.Join("; ", current.Details)})";
                result.Add(new CompileError(current.Line, current.Column, message, current.Warning));
            }

            pending = null;
            current = null;
        }

        foreach (var r in rows)
        {
            var raw = (Str(r[2]) ?? string.Empty).TrimEnd('\r', '\n');
            var text = Collapse(raw);
            var isWarning = string.Equals(Str(r[3]), "WARNING", StringComparison.Ordinal);
            if (Int(r[0]) is > 0 and var line)
            {
                Flush();
                result.Add(new CompileError(line, Math.Max(1, Int(r[1]) ?? 1), text, isWarning) { MessageNumber = Int(r[4]) ?? 0 });
                continue;
            }

            if (JavacHeader.Match(raw) is { Success: true } m)
            {
                Flush();
                current = new JavacEntry(int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture), Collapse(m.Groups["msg"].Value),
                    isWarning || m.Groups["kind"].Value == "warning");
                pending = string.Empty; // 다음 행을 기다린다
                continue;
            }

            if (current is not null && JavacCaret.IsMatch(raw))
            {
                current.Column = raw.IndexOf('^', StringComparison.Ordinal) + 1;
                pending = null; // 앞 행은 소스 줄이었다
                continue;
            }

            if (text.Length == 0 || JavacSummary.IsMatch(text))
            {
                continue;
            }

            if (text.StartsWith("Note:", StringComparison.Ordinal))
            {
                Flush();
                result.Add(new CompileError(1, 1, text, true));
                continue;
            }

            if (current is not null)
            {
                if (pending is { Length: 0 })
                {
                    pending = text; // 소스 줄인지 아직 모른다
                    continue;
                }

                if (pending is not null)
                {
                    current.Details.Add(pending);
                    pending = null;
                }

                current.Details.Add(text);
                continue;
            }

            result.Add(new CompileError(1, 1, text, isWarning) { MessageNumber = Int(r[4]) ?? 0 });
        }

        Flush();
        return result;

        static string Collapse(string value) => Spaces.Replace(value.Trim(), " ");
    }

    /// <summary>묶는 중인 javac 오류 하나.</summary>
    private sealed class JavacEntry(int line, string message, bool warning)
    {
        public int Line { get; } = line;

        public int Column { get; set; } = 1;

        public string Message { get; } = message;

        public bool Warning { get; } = warning;

        public List<string> Details { get; } = [];
    }

    private static DetailSection Unavailable(string title) =>
        new(title, ["항목", "값"], [["안내", "읽지 못했다 — 서버에 Java 사전 뷰(ALL_JAVA_*)가 없거나 볼 권한이 없다"]]);
}
