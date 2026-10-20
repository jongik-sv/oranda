using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Language;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Lexing;

// 소유: TSK-13-07. 분류기가 뽑는 변경 일지 대상(소유자·이름·종류·원인).
public class ChangeTargetTests
{
    static SqlStatement First(string sql) => StatementSplitter.Split(sql, SplitOptions.Default)[0];

    [Theory]
    [InlineData("alter table emp add (x number);", null, "EMP", "TABLE", ChangeOperation.Alter)]
    [InlineData("ALTER TABLE scott.emp MODIFY sal NUMBER(10);", "SCOTT", "EMP", "TABLE", ChangeOperation.Alter)]
    [InlineData("alter table \"Mixed Case\".\"My T\" add c number;", "Mixed Case", "My T", "TABLE", ChangeOperation.Alter)]
    [InlineData("drop index idx_emp;", null, "IDX_EMP", "INDEX", ChangeOperation.Drop)]
    [InlineData("drop table if exists hr.emp purge;", "HR", "EMP", "TABLE", ChangeOperation.Drop)]
    [InlineData("drop materialized view mv1;", null, "MV1", "MATERIALIZED VIEW", ChangeOperation.Drop)]
    [InlineData("drop public synonym syn;", "PUBLIC", "SYN", "SYNONYM", ChangeOperation.Drop)]
    [InlineData("drop database link sales.world;", null, "SALES.WORLD", "DATABASE LINK", ChangeOperation.Drop)]
    [InlineData("drop package pkg;", null, "PKG", "PACKAGE", ChangeOperation.Drop)]
    [InlineData("drop type body app.t;", "APP", "T", "TYPE BODY", ChangeOperation.Drop)]
    [InlineData("create or replace view v as select 1 a from dual;", null, "V", "VIEW", ChangeOperation.CreateOrReplace)]
    [InlineData("create or replace force editionable view hr.v as select 1 a from dual;", "HR", "V", "VIEW", ChangeOperation.CreateOrReplace)]
    [InlineData("create or replace public synonym syn for emp;", "PUBLIC", "SYN", "SYNONYM", ChangeOperation.CreateOrReplace)]
    [InlineData("rename emp to emp2;", null, "EMP", null, ChangeOperation.Rename)]
    [InlineData("alter package pkg compile body;", null, "PKG", "PACKAGE", ChangeOperation.Alter)]
    [InlineData("create or replace no force view v as select 1 a from dual;", null, "V", "VIEW", ChangeOperation.CreateOrReplace)]
    [InlineData("alter table if exists t add c number;", null, "T", "TABLE", ChangeOperation.Alter)]
    public void Extracts_target(string sql, string? owner, string name, string? type, ChangeOperation operation)
    {
        var t = First(sql).ChangeTarget;
        Assert.NotNull(t);
        Assert.Equal(owner, t.Owner);
        Assert.Equal(name, t.Name);
        Assert.Equal(type, t.ObjectType);
        Assert.Equal(operation, t.Operation);
    }

    [Fact]
    public void Extracts_plsql_source_targets_from_block_statements()
    {
        var body = First("create or replace package body app.pkg is\n  procedure p is begin null; end;\nend;\n/");
        Assert.Equal(new ChangeTarget("APP", "PKG", "PACKAGE BODY", ChangeOperation.CreateOrReplace), body.ChangeTarget);

        var proc = First("create or replace editionable procedure p is begin null; end;\n/");
        Assert.Equal(new ChangeTarget(null, "P", "PROCEDURE", ChangeOperation.CreateOrReplace), proc.ChangeTarget);
    }

    [Theory]
    [InlineData("create table t (id number);")]
    [InlineData("create unique index i on t(id);")]
    [InlineData("create view v as select 1 a from dual;")]
    [InlineData("truncate table emp;")]
    [InlineData("comment on table emp is 'x';")]
    [InlineData("alter session set nls_date_format = 'YYYY';")]
    [InlineData("alter system flush shared_pool;")]
    [InlineData("alter user scott identified by x;")]
    [InlineData("drop user scott cascade;")]
    [InlineData("grant select on emp to hr;")]
    [InlineData("select * from emp;")]
    [InlineData("update emp set a = 1;")]
    [InlineData("drop public table x;")]
    [InlineData("drop materialized view log on t;")]
    [InlineData("alter table scott.&t add c number;")]
    [InlineData("drop table &t;")]
    public void Has_no_target_for_non_journal_statements(string sql)
        => Assert.Null(First(sql).ChangeTarget);

    [Fact]
    public void Existing_classification_is_unchanged()
    {
        var drop = First("drop table scott.emp purge;");
        Assert.Equal(StatementKind.Ddl, drop.Kind);
        Assert.Equal("scott.emp", drop.DangerousTarget);
        Assert.Equal("DROP", drop.Verb);
    }
}
