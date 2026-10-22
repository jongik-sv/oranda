using Oranda.App.Features.ObjectDetail;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-13-34 D3 실제 서버(상주 컨테이너 26ai): 내림차순 인덱스·함수 기반 인덱스·복합 외래키의 열을 카탈로그로 읽는다.
/// 시험 표는 시험 사용자(<see cref="ItDb"/>, 끝나면 사용자째 지운다) 스키마에 만든다. ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
public sealed class ObjectDetailKeyColumnsIntegrationTests(ItDb db) : IClassFixture<ItDb>
{
    [Fact]
    public Task Index_and_constraint_columns_come_from_the_live_dictionary() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var parent = "KP_" + suffix;
        var child = "KC_" + suffix;
        db.Exec($"create table {parent} (id number, seq number, constraint {parent}_PK primary key (id, seq))");
        db.Exec($"create table {child} (a number, b number, ename varchar2(30), \"이름\" varchar2(30), constraint {child}_FK foreign key (a, b) references {parent} (id, seq))");
        db.Exec($"create index {child}_DESC_IX on {child} (ename desc, a)");
        db.Exec($"create index {child}_FN_IX on {child} (upper(ename))");
        db.Exec($"create index {child}_KO_IX on {child} (\"이름\" desc)");
        try
        {
            using var rig = new FlowRig(db);
            await rig.ConnectAsync();
            var keys = rig.Container.GetRequiredService<ITableKeyDetailProvider>();
            var table = new DbObjectRef(db.User, child, "TABLE");

            var desc = await keys.GetIndexColumnsAsync(rig.Session, table, $"{child}_DESC_IX", default);
            Assert.False(desc.Denied);
            Assert.Equal([new IndexColumnDetail(1, "ENAME", null, true), new IndexColumnDetail(2, "A", null, false)], desc.Items);

            var fn = Assert.Single((await keys.GetIndexColumnsAsync(rig.Session, table, $"{child}_FN_IX", default)).Items);
            Assert.StartsWith("SYS_NC", fn.Column, StringComparison.Ordinal);
            Assert.Equal("UPPER(\"ENAME\")", fn.Expression);

            var ko = Assert.Single((await keys.GetIndexColumnsAsync(rig.Session, table, $"{child}_KO_IX", default)).Items);
            Assert.Equal(new IndexColumnDetail(1, "이름", null, true), ko);

            var fk = await keys.GetConstraintColumnsAsync(rig.Session, table, $"{child}_FK", default);
            Assert.Equal(
                [
                    new ConstraintColumnDetail(1, "A") { ReferencedOwner = db.User, ReferencedTable = parent, ReferencedColumn = "ID" },
                    new ConstraintColumnDetail(2, "B") { ReferencedOwner = db.User, ReferencedTable = parent, ReferencedColumn = "SEQ" },
                ],
                fk.Items);

            var pk = await keys.GetConstraintColumnsAsync(rig.Session, new DbObjectRef(db.User, parent, "TABLE"), $"{parent}_PK", default);
            Assert.Equal(["ID", "SEQ"], pk.Items.Select(c => c.Column));
            Assert.All(pk.Items, c => Assert.Null(c.ReferencedTable));

            Assert.Empty((await keys.GetIndexColumnsAsync(rig.Session, table, "NO_SUCH_IX", default)).Items);

            // 화면 쪽: 상세를 열어 인덱스 줄을 고르면 열 표가 차고, 위 표의 숨은 열 이름(SYS_NC…)이 식·DESC 로 바뀐다.
            var vm = new ObjectDetailViewModel(rig.Container);
            await vm.OpenAsync(rig.Session, table, showDdl: false);
            vm.SelectedSection = vm.Sections.Single(s => s.Title == "인덱스");
            Assert.True(vm.HasKeyColumns);
            var row = vm.SelectedSection.Rows.Single(r => Equals(r[0], $"{child}_DESC_IX"));
            await vm.SelectKeyRowAsync(row);
            Assert.Equal([[1, "ENAME", "DESC"], [2, "A", "ASC"]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));
            Assert.Equal("ENAME DESC, A", vm.SelectedSection.Rows.Single(r => Equals(r[0], $"{child}_DESC_IX"))[3]);

            vm.SelectedSection = vm.Sections.Single(s => s.Title == "제약");
            await vm.SelectKeyRowAsync(vm.SelectedSection.Rows.Single(r => Equals(r[0], $"{child}_FK")));
            Assert.Equal([1, "A", $"{db.User}.{parent}", "ID"], vm.KeyColumns!.Rows[0].ToArray());
        }
        finally
        {
            db.Exec($"drop table {child} purge");
            db.Exec($"drop table {parent} purge");
        }
    });
}
