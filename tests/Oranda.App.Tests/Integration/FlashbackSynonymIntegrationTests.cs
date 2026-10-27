using Oranda.App.Features.Execution;
using Oranda.App.Features.Flashback;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;
using Oranda.Oracle.Catalog;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-13-44 실제 서버(26ai 기본, ORANDA_IT_PORT=1522·ORANDA_IT_SERVICE=XE 로 11.2): 앱 소유 계정(표 주인)과 조회 계정(시험 접속)을 따로 만들고,
/// 조회 계정의 개인 시노님·PUBLIC 시노님 이름으로 조회한 결과의 플래시백 대상이 소유자.표로 풀리며, 변경 추적 질의(최근 N일·가능한 전체 기간)가
/// ORA-08180·ORA-01466 없이 돌아 시노님 너머 표의 변경 이력을 읽는지 본다. 픽스처 사용자·PUBLIC 시노님은 끝에 지운다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class FlashbackSynonymIntegrationTests(ItDb db) : IClassFixture<ItDb>
{
    private static SqlStatement Select(string sql) => new(sql, default, StatementKind.Query) { Verb = "SELECT" };

    [Fact]
    public Task Synonym_result_targets_the_owning_table_and_the_history_query_runs() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var owner = new ItDb(); // 앱 소유 계정(APP_OWNER 역할). 시험 접속(db.User)은 개인 조회 계정(APP_USER 역할)
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var table = "FB_ORDERS_" + suffix;
        var mine = "FB_MINE_" + suffix;
        var pub = "FB_PUB_" + suffix;
        owner.Exec($"create table {table} (id number primary key, v varchar2(20))");
        owner.Exec($"insert into {table} values (1, 'a')");
        owner.Exec($"grant select, update, flashback on {table} to {db.User}");
        db.Admin($"grant create synonym to {db.User}");
        db.Exec($"create synonym {mine} for {owner.User}.{table}");
        db.Admin($"create public synonym {pub} for {owner.User}.{table}");
        try
        {
            // 표 생성·GRANT(DDL 시각이 바뀐다) 뒤 10초 이후부터 읽으므로 그 뒤에 값을 바꾼다.
            await Task.Delay(TimeSpan.FromSeconds(FlashbackTrackSql.DdlMarginSeconds + 3));
            owner.Exec($"update {table} set v = 'b' where id = 1");
            owner.Exec($"update {table} set v = 'c' where id = 1");

            using var rig = new FlowRig(db);
            await rig.ConnectAsync();
            var editor = rig.Container.GetRequiredService<IRowEditor>();
            var metadata = rig.Container.GetRequiredService<IMetadataProvider>();
            ResultColumn[] cols = [new(0, "ID", "NUMBER", ColumnDataKind.Number), new(1, "V", "VARCHAR2", ColumnDataKind.Text)];
            var expected = new DbObjectRef(owner.User, table, "TABLE");

            // 개인 시노님(별칭·소문자), PUBLIC 시노님 모두 소유자.표로 풀린다. 접속 계정 스키마에는 그 이름의 표가 없다.
            foreach (var sql in new[] { $"SELECT s.id, s.v FROM {mine.ToLowerInvariant()} s", $"SELECT id, v FROM {pub}" })
            {
                var (target, why) = await FlashbackCommandHandler.PlainTargetAsync(editor, metadata, rig.Session, Select(sql), cols, default);
                Assert.True(target is not null, sql + " → " + why);
                Assert.Equal(expected, target!.Table);
                Assert.Equal(["ID"], target.KeyNames);
            }

            // DB 링크 시노님은 지원 밖이라 이유를 보인다(링크가 실제로 없어도 사전에는 만들어진다).
            db.Exec($"create synonym FB_REMOTE_{suffix} for {owner.User}.{table}@NO_SUCH_LINK_{suffix}");
            var (remote, remoteWhy) = await FlashbackCommandHandler.PlainTargetAsync(editor, metadata, rig.Session, Select($"SELECT id, v FROM FB_REMOTE_{suffix}"), cols, default);
            Assert.Null(remote);
            Assert.Contains("DB 링크", remoteWhy, StringComparison.Ordinal);

            var runner = new FlashbackRunner(rig.State, () =>
            {
                ExecutionCommands.EnsureEngineServices(rig.Container, rig.State);
                return rig.Container.GetRequiredService<IExecutionService>();
            });
            var ddl = await runner.LastDdlAsync(rig.Session, EmbeddedCatalog.Load(), expected, default);
            Assert.NotNull(ddl);

            foreach (var days in new decimal?[] { FlashbackTrackSql.DefaultDays, null })
            {
                var text = FlashbackTrackSql.Build(expected, ["ID", "V"], ["ID"], [1m], db.User, days, ddl);
                Assert.Contains("다른 스키마 표는 FLASHBACK 권한", text.Split('\n')[0], StringComparison.Ordinal);
                var body = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
                var rs = await runner.QueryAsync(rig.Session, new FlashbackQuery(Select(body), new Dictionary<string, BindValue>()), default);
                var rows = await FlashbackRunner.ReadAllAsync(rs, default);
                var values = rows.Select(r => Convert.ToString(r[5], System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Assert.True(values.Contains("c") && values.Contains("b"), $"기간 {days?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "전체"}: [{string.Join(", ", values)}]");
            }
        }
        finally
        {
            db.Admin($"drop public synonym {pub}");
        }
    });
}
