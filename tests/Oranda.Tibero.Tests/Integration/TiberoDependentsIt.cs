using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Plans;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Support;
using Oranda.Tibero.Tuning;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// TSK-15-09: get_dependents 의 Tibero 카탈로그(<c>tuning.object_dependents</c>: parent_obj_* · org_object_* 열)를 실서버로 읽는다.
/// 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c>)일 때만: ORT_AE_ 부모 표 · FK 자식 표 · 뷰 · 트리거 · 동의어를 만들고 읽은 뒤 모두 지운다.
/// 접속 값은 단언 메시지·출력에 넣지 않는다(객체 이름·건수만).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoDependentsIt(ITestOutputHelper output)
{
    private static readonly string Parent = TiberoItSessions.ObjectName("AE_DEP_P");
    private static readonly string Child = TiberoItSessions.ObjectName("AE_DEP_C");
    private static readonly string View = TiberoItSessions.ObjectName("AE_DEP_V");
    private static readonly string Trigger = TiberoItSessions.ObjectName("AE_DEP_T");
    private static readonly string Synonym = TiberoItSessions.ObjectName("AE_DEP_S");

    [TiberoWriteFact]
    public async Task Dependents_foreign_keys_triggers_and_synonyms_of_a_table_come_back()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        await DropAsync(session); // 지난 실행이 끊겨 남은 것
        try
        {
            await ExecAsync(session, $"CREATE TABLE {Parent} (ID NUMBER PRIMARY KEY, NM VARCHAR2(20))");
            await ExecAsync(session, $"CREATE TABLE {Child} (ID NUMBER PRIMARY KEY, PARENT_ID NUMBER CONSTRAINT {Child}_FK REFERENCES {Parent} (ID))");
            await ExecAsync(session, $"CREATE TRIGGER {Trigger} BEFORE INSERT OR UPDATE ON {Parent} FOR EACH ROW BEGIN NULL; END;");
            // 시험 계정에 CREATE VIEW·CREATE SYNONYM 이 없을 수 있다(TBR-17004) → 만들 수 있을 때만 확인한다. 트리거는 늘 의존 객체다.
            var hasView = await CreateOrNoPrivilegeAsync(session, $"CREATE VIEW {View} AS SELECT ID, NM FROM {Parent}");
            var hasSynonym = await CreateOrNoPrivilegeAsync(session, $"CREATE SYNONYM {Synonym} FOR {Parent}");
            output.WriteLine($"[dependents] view={hasView} synonym={hasSynonym}");

            var monitor = await it.OpenAsync(SessionPurpose.Monitor);
            var rows = await NewService(it).GetObjectDependentsAsync(monitor, owner, Parent, CancellationToken.None);
            output.WriteLine("[dependents] " + string.Join(", ", rows.Select(r => $"{r.Kind}:{r.Name}:{r.Detail}:{r.Status}")));

            Assert.True(rows.Any(r => r is { Kind: ObjectDependentKind.Dependent, Detail: "TRIGGER" } && r.Name == Trigger), "트리거가 의존 객체로 나오지 않는다");
            Assert.True(!hasView || rows.Any(r => r is { Kind: ObjectDependentKind.Dependent, Detail: "VIEW" } && r.Name == View), "뷰가 의존 객체로 나오지 않는다");
            Assert.True(rows.Any(r => r.Kind == ObjectDependentKind.ForeignKey && r.Name == Child && r.Detail == Child + "_FK"), "자식 표의 FK 가 나오지 않는다");
            Assert.True(rows.Any(r => r.Kind == ObjectDependentKind.Trigger && r.Name == Trigger && r.Detail is { } e && e.Contains("INSERT", StringComparison.OrdinalIgnoreCase)), "트리거가 나오지 않는다");
            Assert.True(!hasSynonym || rows.Any(r => r.Kind == ObjectDependentKind.Synonym && r.Name == Synonym), "동의어가 나오지 않는다");
            Assert.False(rows.Any(r => r.Kind == ObjectDependentKind.Dependent && r.Name == Parent), "자기 자신이 의존 객체로 나온다");
        }
        finally
        {
            await DropAsync(session);
        }
    }

    private static TiberoTuningService NewService(TiberoItSessions it)
    {
        var exec = new TiberoExecutionService(new AllowAll(), ExecutionFakes.NoQuestions(), it.Events, null, dbmsOutput: false);
        return new TiberoTuningService(exec, new TiberoExecutionPlanService(exec), NewLanguage());
    }

    // LanguageService 는 Oranda.Language 의 internal 형식이다(이 시험 어셈블리에 보이지 않는다) → 이름으로 만든다.
    private static ILanguageService NewLanguage()
    {
        var type = Type.GetType("Oranda.Language.LanguageService, Oranda.Language", throwOnError: true)!;
        return (ILanguageService)Activator.CreateInstance(type)!;
    }

    private static async Task DropAsync(TiberoSession session)
    {
        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_synonyms WHERE synonym_name = '{Synonym}'") > 0)
        {
            await ExecAsync(session, $"DROP SYNONYM {Synonym}");
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_views WHERE view_name = '{View}'") > 0)
        {
            await ExecAsync(session, $"DROP VIEW {View}");
        }

        foreach (var table in new[] { Child, Parent })
        {
            if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table}'") > 0)
            {
                await ExecAsync(session, $"DROP TABLE {table}"); // 트리거는 표와 함께 지워진다
            }
        }
    }

    /// <summary>객체를 만든다. 권한이 없으면(TBR-17004) false, 다른 오류는 그대로 던진다.</summary>
    private static async Task<bool> CreateOrNoPrivilegeAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("17004", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
