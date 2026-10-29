using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.References;

namespace Oranda.Language.Ai.Handlers;

/// <summary>
/// <see cref="IStatementReferenceExtractor"/> 구현(AgentGate 의 범위 검사 기준). 문장 분류는 언어 서비스, 참조 객체는
/// <see cref="StatementReferenceExtractor"/>, 동의어는 <see cref="SynonymResolver"/> 로 푼다.
/// 「상대」 중 FK 부모 표와 뷰의 기반 표(뷰 DDL 을 분석)는 찾는다. FK 자식 표(이 표를 가리키는 쪽)는 기존 조회 서비스에 목록이 없어 이번 회차에 포함하지 않는다.
/// </summary>
[OrandaService(typeof(IStatementReferenceExtractor))]
internal sealed partial class AgentStatementReferenceExtractor(ILanguageService language, IMetadataProvider metadata) : IStatementReferenceExtractor
{
    /// <summary>사용자 함수로 보지 않는 흔한 내장 함수·구문 이름. 여기에 없는 호출은 사용자 함수일 수 있다고 본다(보수적).</summary>
    private static readonly FrozenSet<string> BuiltIns = (
        "ABS ACOS ADD_MONTHS ASCII ASIN ATAN ATAN2 AVG BITAND CAST CEIL CHR COALESCE CONCAT CORR COS COSH COUNT COVAR_POP COVAR_SAMP " +
        "CUME_DIST CURRENT_DATE CURRENT_TIMESTAMP DECODE DENSE_RANK EXP EXTRACT FIRST_VALUE FLOOR GREATEST GROUPING HEXTORAW INITCAP " +
        "INSTR LAG LAST_DAY LAST_VALUE LEAD LEAST LENGTH LENGTHB LISTAGG LN LNNVL LOCALTIMESTAMP LOG LOWER LPAD LTRIM MAX MEDIAN MIN MOD " +
        "MONTHS_BETWEEN NEXT_DAY NTILE NULLIF NUMTODSINTERVAL NUMTOYMINTERVAL NVL NVL2 PERCENT_RANK PERCENTILE_CONT PERCENTILE_DISC " +
        "POWER RANK RATIO_TO_REPORT RAWTOHEX REGEXP_COUNT REGEXP_INSTR REGEXP_LIKE REGEXP_REPLACE REGEXP_SUBSTR REPLACE ROUND " +
        "ROW_NUMBER RPAD RTRIM SIGN SIN SINH SQRT STDDEV STDDEV_POP STDDEV_SAMP SUBSTR SUBSTRB SUM SYS_CONTEXT SYS_GUID SYSDATE " +
        "SYSTIMESTAMP TAN TANH TO_CHAR TO_CLOB TO_DATE TO_DSINTERVAL TO_NUMBER TO_TIMESTAMP TO_TIMESTAMP_TZ TO_YMINTERVAL TRANSLATE TRIM " +
        "TRUNC UPPER USER VAR_POP VAR_SAMP VARIANCE XMLAGG XMLELEMENT JSON_VALUE JSON_QUERY JSON_EXISTS JSON_OBJECT JSON_ARRAY " +
        "JSON_ARRAYAGG JSON_OBJECTAGG JSON_TABLE FIRST LAST NEXT_VALUE " +
        // 구문 자리에서 괄호가 붙는 낱말(GROUP BY ROLLUP/CUBE/GROUPING SETS, PIVOT/UNPIVOT)과 그 밖의 흔한 내장 함수.
        "ROLLUP CUBE SETS PIVOT UNPIVOT NTH_VALUE TO_NCHAR TO_NCLOB TO_BINARY_DOUBLE TO_BINARY_FLOAT TO_LOB NANVL REMAINDER " +
        "INSTRB INSTRC SUBSTRC LENGTHC WIDTH_BUCKET ANY_VALUE APPROX_COUNT_DISTINCT STANDARD_HASH ORA_HASH CARDINALITY COLLECT " +
        "NLS_UPPER NLS_LOWER NLS_INITCAP NLSSORT SOUNDEX DUMP VSIZE TZ_OFFSET FROM_TZ NEW_TIME SYS_EXTRACT_UTC SYS_CONNECT_BY_PATH " +
        "UNISTR ASCIISTR COMPOSE DECOMPOSE CONVERT ROWIDTOCHAR CHARTOROWID EMPTY_CLOB EMPTY_BLOB BIN_TO_NUM STATS_MODE " +
        "XMLSERIALIZE XMLPARSE XMLQUERY XMLTABLE XMLCAST JSON_MERGEPATCH JSON_SERIALIZE JSON_TRANSFORM").Split(' ').ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\bAS\s+(?=\(?\s*(SELECT|WITH)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ViewBodyStart();

    public async ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sql);
        var stmts = language.Split(sql, SplitOptions.Default);
        if (stmts.Count == 0)
        {
            return new AgentStatementInfo(StatementKind.Other, true, [], 0);
        }

        var owner = OwnerOf(db, defaultSchema);
        var objects = new List<AgentObjectRef>();
        var remote = false;
        var calls = false;
        var forbidden = false;
        var userFunctions = new List<string>();
        var locking = false;

        // 닫는 쪽으로 판정한다: 객체를 다 찾았다고 확신하지 못하면(해석 실패·모르는 구문·대소문자를 구분하는 이름) 분류 불확실로 돌려
        // 게이트가 범위 검사를 통과시키지 않게 한다.
        var uncertain = stmts.Any(x => x.IsClassificationUncertain);
        foreach (var stmt in stmts)
        {
            var refs = StatementReferenceExtractor.Extract(stmt.Text);
            locking |= refs.HasLockingClause;
            remote |= refs.HasRemoteLink || refs.Objects.Any(o => o.IsRemote);
            foreach (var call in refs.Calls.Where(IsUserFunction))
            {
                calls = true;
                if (IsForbiddenCall(call))
                {
                    forbidden = true;
                }
                else if (call.ToUpperInvariant() is var name && !userFunctions.Contains(name))
                {
                    userFunctions.Add(name);
                }
            }

            uncertain |= refs.Uncertain;
            var resolved = await StatementReferenceExtractor
                .ResolveAsync(refs, owner, (o, n) => metadata.GetNameStepAsync(db, o, n, ct))
                .ConfigureAwait(false);
            foreach (var r in resolved)
            {
                if (r.Target is null)
                {
                    uncertain = true;
                    remote |= r.Problem?.Contains("DB 링크", StringComparison.Ordinal) == true;
                    continue;
                }

                // AgentObjectRef 는 대문자로 정규화되므로 소문자가 든 이름("tb_order")은 다른 객체와 구분되지 않는다.
                uncertain |= HasLowercase(r.Target.Owner) || HasLowercase(r.Target.Name);
                var obj = new AgentObjectRef(r.Target.Owner.ToUpperInvariant(), r.Target.Name.ToUpperInvariant());
                if (!objects.Exists(x => x.Key == obj.Key))
                {
                    objects.Add(obj);
                }
            }
        }

        return new AgentStatementInfo(stmts[0].Kind, uncertain, objects, stmts.Count, locking, remote, calls, HasForbiddenCall: forbidden, UserFunctions: userFunctions);
    }

    public async ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Trim().Split('.', 2);
        var written = parts.Length == 2;
        var written1 = Unquote(parts[0]);
        var target = written ? new DbObjectRef(written1, Unquote(parts[1]), "TABLE") : new DbObjectRef(OwnerOf(db, defaultSchema), written1, "TABLE");
        DbObjectRef resolved;
        try
        {
            resolved = await SynonymResolver
                .ResolveAsync((o, n) => metadata.GetNameStepAsync(db, o, n, ct), target, written)
                .ConfigureAwait(false);
        }
        catch (SynonymResolutionException)
        {
            return null;
        }

        // 소문자가 든 이름은 대문자 정규화 키와 구분되지 않으므로 풀지 않는다(범위 검사가 다른 객체를 허용하는 일을 막는다).
        if (HasLowercase(resolved.Owner) || HasLowercase(resolved.Name))
        {
            return null;
        }

        // 풀린 대상이 사전에 있는지: 없으면(없는 이름·권한 없음) null.
        var exists = await metadata.GetNameStepAsync(db, resolved.Owner, resolved.Name, ct).ConfigureAwait(false);
        return exists is null ? null : new AgentObjectRef(resolved.Owner.ToUpperInvariant(), resolved.Name.ToUpperInvariant());
    }

    public async ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(objects);
        var input = objects.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var related = new List<AgentObjectRef>();
        foreach (var o in objects)
        {
            foreach (var r in await RelatedOfAsync(db, o, ct).ConfigureAwait(false))
            {
                if (!input.Contains(r.Key) && !related.Exists(x => x.Key == r.Key))
                {
                    related.Add(r);
                }
            }
        }

        return related;
    }

    private async Task<IReadOnlyList<AgentObjectRef>> RelatedOfAsync(ISession db, AgentObjectRef o, CancellationToken ct)
    {
        var result = new List<AgentObjectRef>();
        TableInfo info;
        try
        {
            var kind = string.IsNullOrEmpty(o.Kind) ? await KindOfAsync(db, o, ct).ConfigureAwait(false) : o.Kind;
            info = await metadata.GetTableAsync(db, new DbObjectRef(o.Owner, o.Name, kind), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            return result; // 읽지 못하면 상대를 넓히지 않는다
        }

        // FK 로 이어진 부모 표.
        foreach (var c in info.Constraints.Where(c => c.ReferencedTable is not null))
        {
            var p = c.ReferencedTable!;
            result.Add(new AgentObjectRef(p.Owner.ToUpperInvariant(), p.Name.ToUpperInvariant()));
        }

        // 뷰면 기반 표: 뷰 DDL 의 질의를 분석한다.
        if (string.Equals(info.Table.ObjectType, "VIEW", StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(await ViewBaseObjectsAsync(db, o, ct).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<IReadOnlyList<AgentObjectRef>> ViewBaseObjectsAsync(ISession db, AgentObjectRef view, CancellationToken ct)
    {
        string ddl;
        try
        {
            ddl = await metadata.GetDdlAsync(db, new DbObjectRef(view.Owner, view.Name, "VIEW"), new DdlOptions { SqlTerminator = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }

        var start = ViewBodyStart().Match(ddl);
        if (!start.Success)
        {
            return [];
        }

        var refs = StatementReferenceExtractor.Extract(ddl[(start.Index + start.Length)..]);
        var resolved = await StatementReferenceExtractor
            .ResolveAsync(refs, view.Owner, (o, n) => metadata.GetNameStepAsync(db, o, n, ct))
            .ConfigureAwait(false);
        return [.. resolved.Where(r => r.Target is not null).Select(r => new AgentObjectRef(r.Target!.Owner.ToUpperInvariant(), r.Target.Name.ToUpperInvariant()))];
    }

    /// <summary>종류를 모르는 객체가 뷰인지 사전에서 확인한다(분석·해석 결과는 종류를 비워 둔다). 뷰가 아니면 TABLE.</summary>
    private async Task<string> KindOfAsync(ISession db, AgentObjectRef o, CancellationToken ct)
    {
        var views = await metadata
            .ListObjectsAsync(db, new ObjectQuery { Owner = o.Owner, ObjectTypes = ["VIEW"], NamePattern = o.Name }, ct)
            .ConfigureAwait(false);
        return views.Any(v => string.Equals(v.Name, o.Name, StringComparison.OrdinalIgnoreCase)) ? "VIEW" : "TABLE";
    }

    private static bool HasLowercase(string s) => !string.Equals(s, s.ToUpperInvariant(), StringComparison.Ordinal);

    /// <summary>시스템 스키마(소유자로 쓰였을 때 SYS 패키지 호출).</summary>
    private static readonly FrozenSet<string> SystemSchemas = (
        "SYS SYSTEM XDB CTXSYS MDSYS ORDSYS ORDDATA OUTLN DBSNMP WMSYS LBACSYS DVSYS OJVMSYS APPQOSSYS GSMADMIN_INTERNAL")
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>URI 형·XML 형 생성자와 메서드(네트워크·파일을 읽을 수 있다).</summary>
    private static readonly FrozenSet<string> ForbiddenTypes = "HTTPURITYPE DBURITYPE XDBURITYPE URITYPE URIFACTORY XMLTYPE SYS_DBURIGEN"
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 어떤 도구에서도 허용하지 않는 호출: 시퀀스 값(<c>NEXTVAL</c>·<c>CURRVAL</c>), 오라클 제공 패키지(<c>DBMS_*</c>·<c>UTL_*</c>·<c>CTX_*</c>·시스템 스키마 소유),
    /// URI·XML 형. 나머지 사용자 함수는 호출자가 후보로 모아 도구에 따라 확인한다.
    /// </summary>
    internal static bool IsForbiddenCall(string call)
    {
        var parts = call.ToUpperInvariant().Split('.');
        if (parts[^1] is "NEXTVAL" or "CURRVAL")
        {
            return true;
        }

        if (parts.Length >= 2 && SystemSchemas.Contains(parts[0]))
        {
            return true;
        }

        return parts.Any(p => p.StartsWith("DBMS_", StringComparison.Ordinal) || p.StartsWith("UTL_", StringComparison.Ordinal)
            || p.StartsWith("CTX_", StringComparison.Ordinal) || ForbiddenTypes.Contains(p));
    }

    private static bool IsUserFunction(string call) => call.Contains('.', StringComparison.Ordinal) || !BuiltIns.Contains(call);

    private static string OwnerOf(ISession db, string? defaultSchema) =>
        (string.IsNullOrWhiteSpace(defaultSchema) ? db.Profile.User : defaultSchema).ToUpperInvariant();

    private static string Unquote(string part) => part.Length >= 2 && part[0] == '"' && part[^1] == '"' ? part[1..^1] : part.ToUpperInvariant();
}
