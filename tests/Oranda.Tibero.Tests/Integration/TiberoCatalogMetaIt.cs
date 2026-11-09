using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 카탈로그 <c>meta.objects_by_prefix</c> 와 <c>meta.procedures</c>·<c>meta.arguments</c> 의 overload 규칙을 실제 서버로 확인한다(읽기만).
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 대상은 SYS 패키지(<c>DBMS_LOB</c>·<c>STANDARD</c>)와 접속 사용자가 볼 수 있는 단독 프로시저·함수 하나다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCatalogMetaIt
{
    // meta.procedures 열: procedure_name, overload, subprogram_id, object_type
    // meta.arguments 열: procedure_name, overload, position, argument_name, in_out, data_type, defaulted, type_owner, type_name, type_subname, subprogram_id
    private const int ProcName = 0;
    private const int Overload = 1;
    private const int ProcSubprogramId = 2;
    private const int Position = 2;
    private const int ArgSubprogramId = 10;

    [TiberoItFact]
    public async Task Objects_by_prefix_is_ordered_limited_prefix_only_and_escapes_underscore()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var all = await ByPrefixAsync(session, @"DBMS\_O%", 1000);
        Assert.NotEmpty(all);
        Assert.All(all, r => Assert.StartsWith("DBMS_O", Name(r), StringComparison.Ordinal));
        Assert.Equal(all.Select(Name).Order(StringComparer.Ordinal), all.Select(Name)); // 이름 Ordinal 순

        var limited = await ByPrefixAsync(session, @"DBMS\_O%", 3);
        Assert.Equal(Math.Min(3, all.Count), limited.Count);
        Assert.Equal(all.Take(limited.Count).Select(Name), limited.Select(Name));

        // '_' 는 문자 그대로: 이스케이프 없이 DBMS_O 를 주면 DBMSXO… 도 걸릴 수 있지만 이스케이프하면 안 걸린다.
        Assert.Empty(await ByPrefixAsync(session, @"DBMS\_O%", 1000, name => !name.StartsWith("DBMS_O", StringComparison.Ordinal)));

        // 대소문자 구분, 이 서버에 없는 접두어는 0행
        Assert.Empty(await ByPrefixAsync(session, "dbms%", 10));
        Assert.Empty(await ByPrefixAsync(session, "ORT\\_NO\\_SUCH\\_PREFIX\\_XYZ%", 10));
    }

    [TiberoItFact]
    public async Task Package_members_and_arguments_share_overload_keys()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        foreach (var package in new[] { "DBMS_LOB", "STANDARD" })
        {
            var members = await RunAsync(session, "meta.procedures", "SYS", package);
            var arguments = await RunAsync(session, "meta.arguments", "SYS", package);
            if (members.Count == 0)
            {
                continue; // 이 서버에서 보이지 않는 패키지
            }

            AssertOverloadRule(members);
            AssertArgumentsAttach(members, arguments);
            Assert.NotEmpty(arguments); // DBMS_LOB·STANDARD 에는 인자 있는 멤버가 있다
        }
    }

    [TiberoItFact]
    public async Task Standalone_routine_arguments_attach_and_arguments_are_ordered()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var routines = (await TiberoSessionQuery.RunAsync(session,
            "SELECT owner, object_name FROM (SELECT p.owner, p.object_name FROM all_procedures p JOIN all_arguments a " +
            "ON a.owner = p.owner AND a.object_name = p.object_name AND a.package_name IS NULL " +
            "WHERE p.procedure_name IS NULL ORDER BY p.owner, p.object_name) WHERE ROWNUM <= 1")).Rows;
        if (routines.Count == 0)
        {
            return; // 인자 있는 단독 프로시저·함수가 하나도 안 보이는 서버
        }

        var routine = routines[0];
        var owner = TiberoSessionQuery.Str(routine[0])!;
        var name = TiberoSessionQuery.Str(routine[1])!;
        var members = await RunAsync(session, "meta.procedures", owner, name);
        var arguments = await RunAsync(session, "meta.arguments", owner, name);

        Assert.NotEmpty(members);
        AssertOverloadRule(members);
        AssertArgumentsAttach(members, arguments);

        // 정렬: procedure_name, subprogram_id, position
        var keys = arguments.Select(r => (Str(r[ProcName]), Long(r[ArgSubprogramId]), Long(r[Position]))).ToList();
        Assert.Equal(keys.Order(), keys);
    }

    /// <summary>같은 이름이 둘 이상이면 overload 가 선언 순서대로 1..n, 하나면 NULL. subprogram_id 는 서브프로그램마다 고유하다.</summary>
    private static void AssertOverloadRule(List<object?[]> members)
    {
        Assert.Equal(members.Count, members.Select(r => Long(r[ProcSubprogramId])).Distinct().Count());
        foreach (var group in members.GroupBy(r => Str(r[ProcName])))
        {
            var ordered = group.OrderBy(r => Long(r[ProcSubprogramId])).ToList();
            if (ordered.Count == 1)
            {
                Assert.Null(ordered[0][Overload]);
            }
            else
            {
                Assert.Equal(Enumerable.Range(1, ordered.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ordered.Select(r => Str(r[Overload])));
            }
        }
    }

    /// <summary>인자 행의 (procedure_name, overload) 가 멤버 행에 모두 있고 subprogram_id 도 같은 멤버를 가리킨다.</summary>
    private static void AssertArgumentsAttach(List<object?[]> members, List<object?[]> arguments)
    {
        var bySubprogram = members.ToDictionary(r => Long(r[ProcSubprogramId])!.Value, r => (Name: Str(r[ProcName]), Overload: Str(r[Overload])));
        foreach (var argument in arguments)
        {
            var id = Long(argument[ArgSubprogramId]);
            Assert.True(id is not null, "인자 행에 멤버 순번이 없다");
            Assert.True(bySubprogram.ContainsKey(id.Value), "인자 행이 가리키는 멤버 순번이 멤버 목록에 없다");
            var member = bySubprogram[id.Value];
            Assert.Equal(member.Overload, Str(argument[Overload]));
            if (member.Name is not null)
            {
                Assert.Equal(member.Name, Str(argument[ProcName]));
            }
        }
    }

    private static async Task<List<object?[]>> RunAsync(ISession session, string id, string owner, string name)
    {
        var table = await TiberoCatalogQuery.RunAsync(session, id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = owner, ["name"] = name }, CancellationToken.None);
        return [.. table.Rows];
    }

    private static async Task<List<object?[]>> ByPrefixAsync(ISession session, string pattern, int maxRows, Func<string, bool>? only = null)
    {
        var table = await TiberoCatalogQuery.RunAsync(session, "meta.objects_by_prefix",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["pattern"] = pattern, ["max_rows"] = maxRows }, CancellationToken.None);
        return [.. table.Rows.Where(r => only is null || only(Name(r)))];
    }

    private static string Name(object?[] row) => Str(row[1]) ?? string.Empty;

    private static string? Str(object? value) => TiberoSessionQuery.Str(value);

    private static long? Long(object? value) => TiberoSessionQuery.Long(value);
}
