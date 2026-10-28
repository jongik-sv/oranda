using System.Collections.Concurrent;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>
/// Java 저장 객체(JAVA SOURCE·JAVA CLASS·JAVA RESOURCE) 읽기 — 목록·상세 구역·소스 DDL. 읽기만 한다(컴파일·DROP 없음). 소유: TSK-14-22.
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
    internal async Task<bool> IsJavaAvailableAsync(ISession s, CancellationToken ct)
    {
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
                    var sourceName = Str(r[8]);
                    if (sourceName is { Length: > 0 })
                    {
                        var found = await TryQueryAsync(s, "meta.java_source_object", c =>
                        {
                            c.Parameters.Add("owner", obj.Owner);
                            c.Parameters.Add("source", sourceName);
                        }, ct);
                        if (found is { Count: > 0 } && Str(found[0][0]) is { Length: > 0 } sourceObject)
                        {
                            source = new DbObjectRef(obj.Owner, sourceObject, "JAVA SOURCE");
                        }
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
                        ["소스", sourceName],
                        ["소스 객체", source is null ? null : $"{source.Owner}.{source.Name}"],
                    ]));
                }

                var methods = await TryQueryAsync(s, "meta.java_methods", Bind, ct);
                var args = methods is { Count: > 0 } ? await TryQueryAsync(s, "meta.java_arguments", Bind, ct) : null;
                sections.Add(methods is null ? Unavailable("메서드") : new DetailSection("메서드", ["#", "이름", "서명", "반환형", "접근", "static", "abstract", "final"],
                    [.. methods.Select(m => (IReadOnlyList<object?>)
                    [
                        Int(m[0]), Str(m[1]), JavaSignature(Str(m[1]), Int(m[0]), Int(m[6]), args),
                        JavaType(Int(m[7]), Str(m[8]), Str(m[9])), Str(m[2]), Str(m[3]), Str(m[4]), Str(m[5]),
                    ])]));

                var fields = await TryQueryAsync(s, "meta.java_fields", Bind, ct);
                sections.Add(fields is null ? Unavailable("필드") : new DetailSection("필드", ["#", "이름", "형식", "접근", "static", "final"],
                    [.. fields.Select(f => (IReadOnlyList<object?>)[Int(f[0]), Str(f[1]), JavaType(Int(f[5]), Str(f[6]), Str(f[7])), Str(f[2]), Str(f[3]), Str(f[4])])]));

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
            var errors = await TryQueryAsync(s, "meta.compile_errors", c => BindSourceObject(c, obj), ct);
            if (errors is { Count: > 0 })
            {
                sections.Add(new DetailSection("컴파일 오류", ["줄", "열", "종류", "메시지"],
                    [.. errors.Select(e => (IReadOnlyList<object?>)[Int(e[0]), Int(e[1]), Str(e[3]), (Str(e[2]) ?? string.Empty).Trim()])]));
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
    internal static string? JavaTypeName(string? name) => name?.Replace('/', '.');

    /// <summary>원시형(base_type) 또는 클래스(class_name)에 배열 차수만큼 <c>[]</c>.</summary>
    internal static string? JavaType(int? arrayDepth, string? baseType, string? className)
    {
        var name = className is { Length: > 0 } ? JavaTypeName(className) : baseType;
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

    private static DetailSection Unavailable(string title) =>
        new(title, ["항목", "값"], [["안내", "읽지 못했다 — 서버에 Java 사전 뷰(ALL_JAVA_*)가 없거나 볼 권한이 없다"]]);
}
