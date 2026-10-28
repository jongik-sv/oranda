using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Features;
using Oranda.Core.History;

namespace Oranda.Core.Execution;

/// <summary>
/// 정책 관문 규칙(TRD 4.9, SAFE-02·03·07·08). 앱 조립이 등록한다(Core 는 스캔 대상이 아니다). 소유: TSK-06-01.
/// <list type="number">
/// <item>(규칙 0) 에이전트 출처: 실효 분류가 Query 가 아니면 거부</item>
/// <item>읽기 전용 프로필: 질의·트랜잭션 제어·SQL*Plus·EXPLAIN 외 거부</item>
/// <item>조회 전용 Role(D-006, TSK-07-05): 게이트의 <see cref="IFeatureGate.ReadOnlyRole"/> 가 참이면 1번과 같은 기준으로 거부</item>
/// <item>라이선스: 요구 팩이 프로필 선언(서버 <c>control_management_pack_access</c> 를 알면 그것과 둘 중 낮은 값)보다 높으면 거부</item>
/// <item>운영 라벨: DML·DDL·PL/SQL 확인(데이터·객체·다른 세션에 영향이 없는 ALTER 는 묻지 않는다, <see cref="IsHarmlessAlter"/>)</item>
/// <item>WHERE 없는 UPDATE/DELETE: 모든 접속에서 확인</item>
/// <item>위험 명령(DROP·TRUNCATE·ALTER SYSTEM): 대상 이름 재입력 확인(<see cref="RetypeDangerousNames"/>)</item>
/// </list>
/// 거부 규칙은 처음 걸린 것으로 끝난다. 확인 규칙은 모두 모아 메시지를 합치고, <see cref="PolicyDecision.Rule"/> 은 먼저 걸린 규칙이다.
/// 분류에 확신이 없는 문장은 DML 로 본다.
/// </summary>
public sealed class ExecutionPolicy : IExecutionPolicy
{
    /// <summary><see cref="IsHarmlessAlter"/> 의 ALTER INDEX … [NO]MONITORING USAGE.</summary>
    private static readonly Regex IndexMonitoring = new(
        @"\A\s*ALTER\s+INDEX\s+[\w$#.""]+\s+(NO)?MONITORING\s+USAGE\s*;?\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>조회 전용 Role 거부 문구(TRD 10.7.4). <c>{0}</c> = 문장 종류.</summary>
    private static readonly CompositeFormat ReadOnlyRoleDenied = CompositeFormat.Parse(FeatureTexts.ReadOnlyRoleDeniedMessage);

    private readonly IFeatureGate? _features;

    /// <param name="features">기능 게이트(TRD 10.7.4). null 이면 조회 전용 Role 규칙을 끈다. 앱 조립은 등록된 게이트를 넘긴다.</param>
    public ExecutionPolicy(IFeatureGate? features = null)
    {
        _features = features;
    }

    /// <summary>SAFE-08 옵션. 끄면 위험 명령 규칙을 건너뛴다(운영 확인 등 다른 규칙은 그대로).</summary>
    public bool RetypeDangerousNames { get; init; } = true;

    public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(stmt);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(ctx, stmt));
    }

    /// <summary>분류 실패·불확실 문장은 DML 로 본다(TRD 4.9 보수 원칙).</summary>
    public static StatementKind EffectiveKind(SqlStatement stmt)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        return stmt.IsClassificationUncertain ? StatementKind.Dml : stmt.Kind;
    }

    /// <summary>서버 <c>control_management_pack_access</c> 값을 팩 수준으로. 모르면 null.</summary>
    public static LicensePack? ParseServerPackAccess(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim().ToUpperInvariant();
        if (v.Contains("TUNING", StringComparison.Ordinal))
        {
            return LicensePack.DiagnosticsAndTuning;
        }

        if (v.Contains("DIAGNOSTIC", StringComparison.Ordinal))
        {
            return LicensePack.Diagnostics;
        }

        return LicensePack.None;
    }

    private PolicyDecision Evaluate(PolicyContext ctx, SqlStatement stmt)
    {
        var kind = EffectiveKind(stmt);

        // 0. 에이전트 출처(연구서 §6.1): 실효 분류가 Query 가 아니면 거부. 불확실 분류는 EffectiveKind 가 DML 로 보므로 함께 거부된다.
        if (ctx.Source == HistorySource.Agent && kind != StatementKind.Query)
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.AgentSource,
                $"AI 에이전트는 조회(SELECT)만 실행할 수 있습니다. {Describe(stmt, kind)} 은(는) 편집기 제안으로 보내세요.");
        }

        // 1. 읽기 전용(SAFE-02)
        if (ctx.Profile.ReadOnly && !IsReadOnlySafe(stmt, kind))
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile,
                $"읽기 전용 접속 '{ctx.Profile.Name}' 에서는 {Describe(stmt, kind)} 을(를) 실행할 수 없습니다.");
        }

        // 1-2. 조회 전용 Role(D-006). 게이트 값은 문장마다 다시 읽는다(Role 을 바꾸면 다음 문장부터).
        if (_features?.ReadOnlyRole == true && !IsReadOnlySafe(stmt, kind))
        {
            return new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyRole,
                string.Format(CultureInfo.InvariantCulture, ReadOnlyRoleDenied, Describe(stmt, kind)));
        }

        // 2. 라이선스 태그(SAFE-07)
        if (ctx.RequiredLicense > LicensePack.None)
        {
            var held = ctx.Profile.LicensePack;
            var server = ParseServerPackAccess(ctx.Server.ManagementPackAccess);
            if (server is { } s && s < held)
            {
                held = s;
            }

            if (ctx.RequiredLicense > held)
            {
                return new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.License,
                    $"이 쿼리는 {PackName(ctx.RequiredLicense)} 팩이 필요합니다. 접속 '{ctx.Profile.Name}' 의 보유 선언(또는 서버 설정)이 없어 보내지 않았습니다.");
            }
        }

        var rule = PolicyRule.None;
        var messages = new List<string>();
        string? retype = null;

        // 3. 운영 라벨(SAFE-03)
        if (ctx.Profile.Environment == ConnectionEnvironment.Production
            && kind is StatementKind.Dml or StatementKind.Ddl or StatementKind.PlSql
            && !IsHarmlessAlter(stmt))
        {
            rule = PolicyRule.ProductionChange;
            messages.Add($"운영 접속 '{ctx.Profile.Name}' 에서 {Describe(stmt, kind)} 을(를) 실행합니다.");
        }

        // 4. WHERE 없는 UPDATE/DELETE(SAFE-03, 모든 접속)
        if (stmt.IsUnfilteredDml)
        {
            if (rule == PolicyRule.None)
            {
                rule = PolicyRule.UnfilteredDml;
            }

            messages.Add($"WHERE 절이 없는 {stmt.Verb ?? "DML"} 입니다. 테이블의 모든 행이 바뀝니다.");
        }

        // 5. 위험 명령(SAFE-08)
        if (RetypeDangerousNames && IsDangerous(stmt))
        {
            if (rule == PolicyRule.None)
            {
                rule = PolicyRule.DangerousCommand;
            }

            retype = stmt.DangerousTarget ?? stmt.Verb;
            messages.Add($"위험 명령 {stmt.Verb} 입니다. 계속하려면 대상 이름 '{retype}' 을(를) 다시 입력하세요.");
        }

        return rule == PolicyRule.None
            ? PolicyDecision.Allowed
            : new PolicyDecision(PolicyDecisionKind.Confirm, rule, string.Join(Environment.NewLine, messages)) { RetypeText = retype };
    }

    private static bool IsReadOnlySafe(SqlStatement stmt, StatementKind kind) => kind switch
    {
        StatementKind.Query or StatementKind.TransactionControl or StatementKind.SqlPlus => true,
        StatementKind.Other => string.Equals(stmt.Verb, "EXPLAIN", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// 운영에서도 묻지 않는 ALTER: ALTER SESSION(통계 수준·NLS·현재 스키마 등, 이 세션에만 걸린다),
    /// ALTER INDEX … [NO]MONITORING USAGE(사용 여부 기록만 켜고 끈다). COMPILE·REBUILD·VISIBLE 등은 다른 세션·실행 계획에 영향이 있어 계속 묻는다.
    /// </summary>
    internal static bool IsHarmlessAlter(SqlStatement stmt) =>
        stmt.Verb == "ALTER SESSION"
        || (stmt.Verb == "ALTER" && IndexMonitoring.IsMatch(stmt.Text));

    private static bool IsDangerous(SqlStatement stmt) =>
        stmt.DangerousTarget is not null
        || stmt.Verb is "DROP" or "TRUNCATE" or "ALTER SYSTEM";

    private static string Describe(SqlStatement stmt, StatementKind kind) =>
        stmt.Verb ?? kind switch
        {
            StatementKind.Dml => "DML",
            StatementKind.Ddl => "DDL",
            StatementKind.PlSql => "PL/SQL",
            _ => "문장",
        };

    private static string PackName(LicensePack pack) => pack switch
    {
        LicensePack.Diagnostics => "Diagnostics",
        _ => "Diagnostics + Tuning",
    };
}
