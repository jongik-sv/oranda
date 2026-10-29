using System.Text.RegularExpressions;
using Oranda.Core.Ai.Gate;
using Oranda.Language.Ai.Handlers;

namespace Oranda.Language.Tests.Ai.Handlers;

/// <summary>
/// 실행 도구 앞의 사용자 함수 확인(c7): 가짜 딕셔너리(ALL_OBJECTS·ALL_DEPENDENCIES·ALL_SOURCE)를 걸으며 자치 트랜잭션·외부 패키지·확인 불가를 닫힌 쪽으로 판정한다.
/// </summary>
public partial class UserFunctionGraphWalkerTests
{
    /// <summary>객체(소유자, 이름) → 종류·소스 줄·의존. 질의 글의 (owner = 'X' AND name = 'Y') 쌍만 보고 답한다.</summary>
    private sealed partial class FakeDictionary
    {
        public Dictionary<(string Owner, string Name), (string Type, string[] Source, (string Owner, string Name, string Type)[] Deps)> Objects { get; } = [];

        /// <summary>동의어(소유자, 이름) → 가리키는 객체(소유자, 이름, DB 링크).</summary>
        public Dictionary<(string Owner, string Name), (string Owner, string Name, string Link)> Synonyms { get; } = [];

        public bool Fail { get; set; }

        public void Add(string owner, string name, string type, string[] source, params (string, string, string)[] deps) =>
            Objects[(owner, name)] = (type, source, deps);

        public Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("ORA-00942");
            }

            var keys = Pair().Matches(sql).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
            var rows = new List<object?[]>();
            if (sql.Contains("FROM all_synonyms", StringComparison.Ordinal))
            {
                rows.AddRange(keys.Where(Synonyms.ContainsKey).Select(k => new object?[] { k.Item1, k.Item2, Synonyms[k].Owner, Synonyms[k].Name, Synonyms[k].Link }));
                return Task.FromResult<IReadOnlyList<object?[]>>(rows);
            }

            foreach (var key in keys.Where(Objects.ContainsKey))
            {
                var o = Objects[key];
                if (sql.Contains("FROM all_objects", StringComparison.Ordinal))
                {
                    rows.Add([key.Item1, key.Item2, o.Type]);
                }
                else if (sql.Contains("FROM all_dependencies", StringComparison.Ordinal))
                {
                    // 종류 "TABLE@REMOTE" = DB 링크 너머의 의존(referenced_link_name).
                    rows.AddRange(o.Deps.Select(d => d.Type.Split('@') is var t ? new object?[] { d.Owner, d.Name, t[0], t.Length > 1 ? t[1] : null } : null!));
                }
                else
                {
                    var bodyType = o.Type == "PACKAGE" ? "PACKAGE BODY" : o.Type;
                    rows.AddRange(o.Source.Select((text, i) => new object?[] { key.Item1, key.Item2, bodyType, (long)(i + 1), text })
                        .Where(r => (long)r[3]! == 1 || SourceFilter().IsMatch((string)r[4]!)));
                }
            }

            return Task.FromResult<IReadOnlyList<object?[]>>(rows);
        }

        [GeneratedRegex(@"\((?:owner) = '([A-Z0-9_$#]+)' AND (?:object_name|name|synonym_name) = '([A-Z0-9_$#]+)'\)")]
        private static partial Regex Pair();

        // 실제 질의의 REGEXP_LIKE 와 같은 낱말.
        [GeneratedRegex("AUTONOMOUS_TRANSACTION|LANGUAGE|EXTERNAL|MLE|SQL_MACRO|POLYMORPHIC", RegexOptions.IgnoreCase)]
        private static partial Regex SourceFilter();
    }

    private static Task<UserFunctionVerdict> Check(FakeDictionary d, params string[] functions) => CheckIn(d, "APP", functions);

    private static Task<UserFunctionVerdict> CheckIn(FakeDictionary d, string schema, params string[] functions) =>
        new UserFunctionGraphWalker(d.QueryAsync).CheckAsync(functions, schema, CancellationToken.None).AsTask();

    [Fact]
    public async Task Oracle_supplied_dependencies_are_not_read_but_functions_made_in_a_system_schema_are()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_STD", "FUNCTION", ["FUNCTION fn_std RETURN NUMBER IS"], ("SYS", "STANDARD", "PACKAGE"));
        d.Add("SYSTEM", "FN_A", "FUNCTION", ["FUNCTION fn_a RETURN NUMBER IS"], ("SYSTEM", "PR_LOG", "PROCEDURE"));
        d.Add("SYSTEM", "PR_LOG", "PROCEDURE", ["PROCEDURE pr_log IS", "  PRAGMA AUTONOMOUS_TRANSACTION;"]);

        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "FN_STD")); // SYS.STANDARD 는 소스가 없어도 따라가지 않는다
        Assert.Equal(UserFunctionVerdict.Autonomous, await CheckIn(d, "SYSTEM", "FN_A"));
    }

    [Fact]
    public async Task A_plain_function_and_a_package_function_are_safe()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_NAME", "FUNCTION", ["FUNCTION fn_name(p NUMBER) RETURN VARCHAR2 IS", "BEGIN RETURN 'x'; END;"], ("APP", "TB_CODE", "TABLE"));
        d.Add("APP", "PKG_UTIL", "PACKAGE", ["PACKAGE BODY pkg_util IS"]);

        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "FN_NAME", "PKG_UTIL.FMT"));
    }

    [Fact]
    public async Task An_autonomous_transaction_two_calls_deep_is_found()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_A", "FUNCTION", ["FUNCTION fn_a RETURN NUMBER IS"], ("APP", "FN_B", "FUNCTION"));
        d.Add("APP", "FN_B", "FUNCTION", ["FUNCTION fn_b RETURN NUMBER IS"], ("APP", "PR_LOG", "PROCEDURE"));
        d.Add("APP", "PR_LOG", "PROCEDURE", ["PROCEDURE pr_log IS", "  PRAGMA AUTONOMOUS_TRANSACTION;"]);

        Assert.Equal(UserFunctionVerdict.Autonomous, await Check(d, "FN_A"));
    }

    [Fact]
    public async Task A_dependency_on_a_network_package_is_external()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_CALL", "FUNCTION", ["FUNCTION fn_call RETURN CLOB IS"], ("PUBLIC", "UTL_HTTP", "SYNONYM"), ("SYS", "UTL_HTTP", "PACKAGE"));

        Assert.Equal(UserFunctionVerdict.External, await Check(d, "FN_CALL"));
    }

    [Theory]
    [InlineData("FN_MISSING")]
    [InlineData("FN_WRAPPED")]
    [InlineData("FN_NO_SOURCE")]
    [InlineData("bad name")]
    public async Task Anything_it_cannot_read_is_unverified(string function)
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_WRAPPED", "FUNCTION", ["FUNCTION fn_wrapped wrapped\na000000\n367\nabcd"]);
        d.Add("APP", "FN_NO_SOURCE", "FUNCTION", []);

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, function));
    }

    [Fact]
    public async Task A_failing_dictionary_query_is_unverified()
    {
        var d = new FakeDictionary { Fail = true };

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "FN_A"));
    }

    [Fact]
    public async Task A_chain_deeper_than_the_limit_is_unverified()
    {
        var d = new FakeDictionary();
        for (var i = 0; i <= UserFunctionGraphWalker.MaxDepth + 1; i++)
        {
            d.Add("APP", $"FN_{i}", "FUNCTION", [$"FUNCTION fn_{i} RETURN NUMBER IS"], ("APP", $"FN_{i + 1}", "FUNCTION"));
        }

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "FN_0"));
    }

    [Theory]
    [InlineData("SYS", "UTL_INADDR", "PACKAGE")] // DNS 로 값을 내보낼 수 있다
    [InlineData("SYS", "HTTPURITYPE", "TYPE")]
    [InlineData("SYS", "DBMS_LDAP", "PACKAGE")]
    [InlineData("SYS", "DBMS_SQL", "PACKAGE")]
    [InlineData("APP", "JV_NET", "JAVA CLASS")]
    [InlineData("APP", "LIB_NATIVE", "LIBRARY")]
    [InlineData("APP", "SEQ_ORDER", "SEQUENCE")]
    public async Task Oracle_supplied_objects_outside_the_allow_list_and_unknown_dependency_kinds_are_unverified(string owner, string name, string type)
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_X", "FUNCTION", ["FUNCTION fn_x RETURN NUMBER IS"], (owner, name, type));

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "FN_X"));
    }

    [Fact]
    public async Task Allowed_oracle_packages_and_dictionary_views_through_public_synonyms_are_safe()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_X", "FUNCTION", ["FUNCTION fn_x RETURN NUMBER IS"],
            ("SYS", "STANDARD", "PACKAGE"), ("SYS", "DBMS_OUTPUT", "PACKAGE"), ("PUBLIC", "ALL_TABLES", "SYNONYM"), ("SYS", "DUAL", "TABLE"));
        d.Synonyms[("PUBLIC", "ALL_TABLES")] = ("SYS", "ALL_TABLES", string.Empty);
        d.Add("SYS", "ALL_TABLES", "VIEW", []);

        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "FN_X"));
    }

    [Theory]
    [InlineData("  LANGUAGE JAVA NAME 'Net.get(java.lang.String) return java.lang.String';")]
    [InlineData("AS LANGUAGE C LIBRARY lib_native NAME \"get\";")]
    [InlineData("AS EXTERNAL LIBRARY lib_native;")]
    [InlineData("RETURN VARCHAR2 SQL_MACRO IS")]
    [InlineData("PIPELINED ROW POLYMORPHIC USING pkg_ptf;")]
    [InlineData("AS MLE MODULE net_mod SIGNATURE 'get(string)';")]
    public async Task Call_specs_sql_macros_and_polymorphic_table_functions_are_unverified(string line)
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_X", "FUNCTION", ["FUNCTION fn_x(p VARCHAR2) RETURN VARCHAR2", line]);

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "FN_X"));
    }

    [Fact]
    public async Task A_database_link_inside_a_function_is_external()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_X", "FUNCTION", ["FUNCTION fn_x RETURN NUMBER IS"], ("APP", "TB_REMOTE", "TABLE@REMOTE_DB"));

        Assert.Equal(UserFunctionVerdict.External, await Check(d, "FN_X"));
    }

    [Fact]
    public async Task Synonyms_are_followed_to_what_they_point_at()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_TABLE", "FUNCTION", ["FUNCTION fn_table RETURN NUMBER IS"], ("APP", "SYN_CODE", "SYNONYM"));
        d.Add("APP", "FN_AUTO", "FUNCTION", ["FUNCTION fn_auto RETURN NUMBER IS"], ("APP", "SYN_LOG", "SYNONYM"));
        d.Add("APP", "FN_REMOTE", "FUNCTION", ["FUNCTION fn_remote RETURN NUMBER IS"], ("APP", "SYN_REMOTE", "SYNONYM"));
        d.Add("APP", "FN_DANGLING", "FUNCTION", ["FUNCTION fn_dangling RETURN NUMBER IS"], ("APP", "SYN_GONE", "SYNONYM"));
        d.Synonyms[("APP", "SYN_CODE")] = ("MES", "TB_CODE", string.Empty);
        d.Synonyms[("APP", "SYN_LOG")] = ("MES", "PR_LOG", string.Empty);
        d.Synonyms[("APP", "SYN_REMOTE")] = ("MES", "TB_CODE", "REMOTE_DB");
        d.Synonyms[("APP", "SYN_GONE")] = ("MES", "TB_DROPPED", string.Empty);
        d.Add("MES", "TB_CODE", "TABLE", []);
        d.Add("MES", "PR_LOG", "PROCEDURE", ["PROCEDURE pr_log IS", "  PRAGMA AUTONOMOUS_TRANSACTION;"]);

        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "FN_TABLE"));
        Assert.Equal(UserFunctionVerdict.Autonomous, await Check(d, "FN_AUTO"));
        Assert.Equal(UserFunctionVerdict.External, await Check(d, "FN_REMOTE"));
        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "FN_DANGLING"));
    }

    [Fact]
    public async Task Keyword_names_like_rollup_are_fine_when_no_such_function_exists_but_are_checked_when_one_does()
    {
        var d = new FakeDictionary();
        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "ROLLUP", "JSON_VALUE"));
        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "ROLLUP", "FN_MISSING"));

        d.Add("APP", "CUBE", "FUNCTION", ["FUNCTION cube(p NUMBER) RETURN NUMBER IS", "  PRAGMA AUTONOMOUS_TRANSACTION;"]);
        Assert.Equal(UserFunctionVerdict.Autonomous, await Check(d, "CUBE"));
    }

    [Fact]
    public async Task Keyword_names_that_are_synonyms_are_not_taken_as_syntax()
    {
        var d = new FakeDictionary();
        d.Synonyms[("APP", "ROLLUP")] = ("LOG", "FN_AUTO_LOG", string.Empty);
        d.Synonyms[("PUBLIC", "JSON_VALUE")] = ("LOG", "FN_AUTO_LOG", string.Empty);

        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "ROLLUP"));
        Assert.Equal(UserFunctionVerdict.Unverified, await Check(d, "JSON_VALUE"));
        Assert.Equal(UserFunctionVerdict.Safe, await Check(d, "CUBE"));
    }
}
