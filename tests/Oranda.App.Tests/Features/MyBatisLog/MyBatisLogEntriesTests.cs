using Oranda.App.Features.MyBatisLog;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.MyBatisLog;

public class MyBatisLogEntriesTests
{
    [Fact]
    public void Reads_logger_sql_and_params_of_each_pair()
    {
        var log = "2026-10-23 14:05:01.123 [main] DEBUG com.acme.hr.EmpMapper.selectEmpList - ==>  Preparing: SELECT * FROM EMP WHERE EMPNO = ? AND ENAME = ?\n"
            + "2026-10-23 14:05:01.130 [main] DEBUG com.acme.hr.EmpMapper.selectEmpList - ==> Parameters: 7499(Integer), ALLEN(String)\n"
            + "DEBUG com.acme.hr.EmpMapper.countEmp:110 ==>  Preparing: SELECT COUNT(*) FROM EMP\n"
            + "DEBUG com.acme.hr.EmpMapper.countEmp:110 ==> Parameters: \n";

        var entries = MyBatisLogEntries.Split(log);

        Assert.Equal(2, entries.Count);
        Assert.Equal("com.acme.hr.EmpMapper.selectEmpList", entries[0].Logger);
        Assert.Equal("SELECT * FROM EMP WHERE EMPNO = ? AND ENAME = ?", entries[0].RawSql);
        Assert.Equal(["7499", "ALLEN"], entries[0].Params.Select(p => p.Value));
        Assert.Equal(BindDataType.Number, entries[0].Params[0].Type);
        Assert.Equal("com.acme.hr.EmpMapper.countEmp", entries[1].Logger);
        Assert.Empty(entries[1].Params);
    }

    [Fact]
    public void Line_without_logger_name_has_null_logger()
    {
        var entries = MyBatisLogEntries.Split("DEBUG ==>  Preparing: SELECT 1 FROM dual\nDEBUG ==> Parameters: \n");

        Assert.Single(entries);
        Assert.Null(entries[0].Logger);
    }

    [Fact]
    public void Text_without_preparing_gives_no_entry()
    {
        Assert.Empty(MyBatisLogEntries.Split("select 1 from dual"));
    }
}
