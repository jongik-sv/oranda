using System.Text.Json;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>적재 방식 판정(조정 결정 88·89, <see cref="CopyLoad"/>).</summary>
public class CopyLoadTests
{
    private static CopyJob Job(TableDefaults? defaults = null, CommitMode commit = CommitMode.PerTable, params TableRule[] rules) => new()
    {
        Id = "j", Name = "j",
        Source = new ConnectionRef("S", "SRC"), Target = new ConnectionRef("T", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==",
        Tables = rules,
        TableDefaults = defaults,
        Options = new CopyOptions { Commit = commit },
    };

    private static TableInfo Table(string owner, params (string Name, string Type)[] columns) =>
        new(new DbObjectRef(owner, "ORDERS", "TABLE"), [.. columns.Select((c, i) => new ColumnInfo(c.Name, i + 1, c.Type))]);

    private static CopyTablePlan Plan(TargetAction action, TableInfo source, TableInfo target, bool createMissing = false) =>
        new(new TableRule("ORDERS", RuleKind.All, action), source, target, [.. source.Columns.Select(c => c.Name)], ["ID"]) { CreateMissing = createMissing };

    [Fact]
    public void Requested_prefers_table_choice_then_defaults_then_standard()
    {
        var follow = new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert);
        var own = follow with { Load = LoadMethod.Standard };
        var external = new TableDefaults(null, null, false) { Load = LoadMethod.ExternalTool };

        Assert.Equal(LoadMethod.Standard, CopyLoad.Requested(Job(), follow));
        Assert.Equal(LoadMethod.ExternalTool, CopyLoad.Requested(Job(external), follow));
        Assert.Equal(LoadMethod.Standard, CopyLoad.Requested(Job(external), own));
        Assert.Equal(LoadMethod.ExternalTool, CopyLoad.Requested(Job(), follow with { Load = LoadMethod.ExternalTool }));
    }

    [Fact]
    public void Run_blocker_for_at_end_commit_and_non_default_auth()
    {
        var profile = new ConnectionProfile("T", "DST", "db:1521/svc");
        Assert.Null(CopyLoad.RunBlocker(Job(), Dbms.Oracle, profile));
        Assert.NotNull(CopyLoad.RunBlocker(Job(commit: CommitMode.AtEnd), Dbms.Oracle, profile));
        Assert.NotNull(CopyLoad.RunBlocker(Job(), Dbms.Oracle, profile with { Auth = AuthMode.SysDba }));
        Assert.NotNull(CopyLoad.RunBlocker(Job(), Dbms.Oracle, profile with { ProxyUser = "P" }));
        Assert.NotNull(CopyLoad.RunBlocker(Job(), Dbms.Oracle, profile with { DataSource = " " }));
    }

    [Fact]
    public void Merge_into_existing_table_falls_back_but_fresh_table_does_not()
    {
        var source = Table("SRC", ("ID", "NUMBER(10)"), ("NAME", "VARCHAR2(20)"));
        var target = Table("DST", ("ID", "NUMBER(10)"), ("NAME", "VARCHAR2(20)"));
        var merge = Plan(TargetAction.MergeByPk, source, target);
        var columns = CopyLoad.Columns(merge, merge.Columns, Dbms.Oracle, Dbms.Oracle);

        Assert.NotNull(CopyLoad.TableBlocker(merge, columns));
        Assert.Null(CopyLoad.TableBlocker(merge with { CreateMissing = true }, columns));
        Assert.Null(CopyLoad.TableBlocker(Plan(TargetAction.DeleteAllInsert, source, target), columns));

        // 키가 있는 「같은 범위 지우고 넣기」도 기본 방식은 MERGE 다(범위 밖 같은 키 행을 덮어쓴다).
        Assert.NotNull(CopyLoad.TableBlocker(Plan(TargetAction.DeleteRangeInsert, source, target), columns));
        Assert.Null(CopyLoad.TableBlocker(Plan(TargetAction.DeleteRangeInsert, source, target) with { KeyColumns = [] }, columns));
    }

    [Fact]
    public void Existing_table_with_different_type_family_falls_back()
    {
        var source = Table("SRC", ("ID", "NUMBER(10)"), ("D", "VARCHAR2(20)"));
        var target = Table("DST", ("ID", "NUMBER(10)"), ("D", "DATE"));
        var plan = Plan(TargetAction.DeleteAllInsert, source, target);

        var reason = CopyLoad.TableBlocker(plan, CopyLoad.Columns(plan, plan.Columns, Dbms.Oracle, Dbms.Oracle));

        Assert.NotNull(reason);
        Assert.Contains("D", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_use_target_types_for_existing_tables_and_created_types_for_fresh_ones()
    {
        var source = Table("SRC", ("ID", "NUMBER(10)"), ("NAME", "VARCHAR2(20)"));
        var target = Table("DST", ("ID", "NUMBER(12)"), ("NAME", "VARCHAR2(40)"));

        var existing = CopyLoad.Columns(Plan(TargetAction.DeleteAllInsert, source, target), ["ID", "NAME"], Dbms.Oracle, Dbms.Oracle);
        var fresh = CopyLoad.Columns(Plan(TargetAction.Recreate, source, target), ["ID", "NAME"], Dbms.Oracle, Dbms.Oracle);

        Assert.Equal(["NUMBER(12)", "VARCHAR2(40)"], existing.Select(c => c.TargetType));
        Assert.Equal(["NUMBER(10)", "VARCHAR2(20)"], fresh.Select(c => c.TargetType));
        Assert.True(CopyLoad.Fresh(Plan(TargetAction.Recreate, source, target)));
        Assert.True(CopyLoad.Fresh(Plan(TargetAction.AppendOnly, source, target, createMissing: true)));
        Assert.False(CopyLoad.Fresh(Plan(TargetAction.AppendOnly, source, target)));
    }

    [Fact]
    public void Tool_follows_target_dbms()
    {
        Assert.Equal(ExternalLoaderKind.SqlLoader, CopyLoad.ToolFor(Dbms.Oracle));
        Assert.Equal(ExternalLoaderKind.TbLoader, CopyLoad.ToolFor(Dbms.Tibero));
    }

    [Fact]
    public void Load_choices_are_left_out_of_the_job_file_when_default()
    {
        var job = Job(new TableDefaults(null, null, false), CommitMode.PerTable, new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert));
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        Assert.DoesNotContain("\"load\"", json, StringComparison.Ordinal);

        var chosen = job with
        {
            TableDefaults = new TableDefaults(null, null, false) { Load = LoadMethod.ExternalTool },
            Tables = [new TableRule("A", RuleKind.All, TargetAction.DeleteAllInsert) { Load = LoadMethod.Standard }],
        };
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(chosen, CopyJobJsonContext.Default.CopyJob), CopyJobJsonContext.Default.CopyJob)!;
        Assert.Equal(LoadMethod.ExternalTool, back.TableDefaults!.Load);
        Assert.Equal(LoadMethod.Standard, back.Tables[0].Load);
    }
}
