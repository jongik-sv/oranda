using System.Data;
using System.Data.Common;
using Oranda.Core.Execution;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Results;

[Collection("FakeDriver")]
public class TiberoBindBuilderTests
{
    private static TiberoDriver Driver => FakeDriverHost.Driver;

    private static DbCommand NewCommand() => Driver.CreateConnection("Data Source=x").CreateCommand();

    private static SqlStatement Stmt(params string[] names) =>
        new("select 1 from dual", default, StatementKind.Query) { BindNames = names };

    private static string KindOf(DbParameter p) => FakeDriverHost.Get(p, "TiberoDbType")!.ToString()!;

    [Fact]
    public void Binds_by_name_in_statement_order_and_returns_only_outputs()
    {
        var cmd = NewCommand();
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(BindDataType.Varchar2, "x"),
            ["B"] = new(BindDataType.Number, 5m, BindDirection.InOut),
            ["c"] = new(BindDataType.Varchar2, null, BindDirection.Out),
        };

        var outs = TiberoBindBuilder.Build(Driver, cmd, Stmt("a", "b", "c"), binds);

        Assert.True((bool)FakeDriverHost.Get(cmd, "BindByName")!);
        Assert.Equal(["a", "b", "c"], cmd.Parameters.Cast<DbParameter>().Select(p => p.ParameterName));
        Assert.Equal(["b", "c"], outs.Select(o => o.Name));
        Assert.Same(cmd.Parameters[1], outs[0].Parameter);
        Assert.Equal(ParameterDirection.InputOutput, cmd.Parameters[1].Direction);
        Assert.Equal(ParameterDirection.Output, cmd.Parameters[2].Direction);
    }

    [Fact]
    public void Missing_value_for_a_bind_name_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TiberoBindBuilder.Build(Driver, NewCommand(), Stmt("x"), new Dictionary<string, BindValue>()));

        Assert.Contains(":x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_statement_names_all_bind_keys_are_used()
    {
        var cmd = NewCommand();
        var binds = new Dictionary<string, BindValue> { ["p1"] = new(BindDataType.Varchar2, "x"), ["p2"] = new(BindDataType.Number, 1m) };

        TiberoBindBuilder.Build(Driver, cmd, Stmt(), binds);

        Assert.Equal(2, cmd.Parameters.Count);
    }

    [Fact]
    public void Plain_values_set_kind_value_and_size()
    {
        var cmd = NewCommand();
        var p = TiberoBindBuilder.Create(Driver, cmd, "v", new BindValue(BindDataType.Varchar2, "안녕") { Size = 10 });

        Assert.Equal("Varchar2", KindOf(p));
        Assert.Equal("안녕", p.Value);
        Assert.Equal(10, p.Size);
        Assert.Equal(ParameterDirection.Input, p.Direction);
    }

    [Fact]
    public void Null_value_is_db_null_and_out_strings_get_the_default_size()
    {
        var cmd = NewCommand();
        var input = TiberoBindBuilder.Create(Driver, cmd, "v", new BindValue(BindDataType.Number, null));
        var output = TiberoBindBuilder.Create(Driver, cmd, "o", new BindValue(BindDataType.Varchar2, "ignored", BindDirection.Out));

        Assert.Same(DBNull.Value, input.Value);
        Assert.Same(DBNull.Value, output.Value);
        Assert.Equal(TiberoBindPlan.DefaultOutSize, output.Size);
    }

    [Fact]
    public void Timestamp_is_wrapped_with_nanosecond_precision()
    {
        var cmd = NewCommand();
        var when = new DateTime(2026, 11, 9, 1, 2, 3).AddTicks(1_234_567);

        var p = TiberoBindBuilder.Create(Driver, cmd, "t", new BindValue(BindDataType.Timestamp, when));

        Assert.Equal("TimeStamp", KindOf(p));
        Assert.Equal("TiberoTimeStamp", p.Value!.GetType().Name);
        Assert.Equal(123_456_700, FakeDriverHost.Get(p.Value, "Nanosecond"));
        Assert.Equal(new DateTime(2026, 11, 9, 1, 2, 3), FakeDriverHost.Get(p.Value, "Value"));
    }

    [Fact]
    public void Timestamp_tz_is_wrapped_with_the_offset_text()
    {
        var cmd = NewCommand();
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5).Add(TimeSpan.FromMinutes(-30)));

        var p = TiberoBindBuilder.Create(Driver, cmd, "t", new BindValue(BindDataType.TimestampTz, when));

        Assert.Equal("TimeStampTZ", KindOf(p));
        Assert.Equal("TiberoTimeStampTZ", p.Value!.GetType().Name);
        Assert.Equal("-05:30", FakeDriverHost.Get(p.Value, "TimeZone"));
        Assert.Equal(when.DateTime, FakeDriverHost.Get(p.Value, "Value"));
    }

    [Fact]
    public void Boolean_is_sent_as_number()
    {
        var cmd = NewCommand();

        var yes = TiberoBindBuilder.Create(Driver, cmd, "b", new BindValue(BindDataType.Boolean, true));
        var none = TiberoBindBuilder.Create(Driver, cmd, "n", new BindValue(BindDataType.Boolean, null));

        Assert.Equal("Decimal", KindOf(yes));
        Assert.Equal(1m, yes.Value);
        Assert.Equal("Decimal", KindOf(none));
        Assert.Same(DBNull.Value, none.Value);
    }

    [Fact]
    public void Output_of_a_type_the_driver_cannot_return_is_received_as_text()
    {
        var cmd = NewCommand();

        var p = TiberoBindBuilder.Create(Driver, cmd, "t", new BindValue(BindDataType.TimestampTz, null, BindDirection.Out));

        Assert.Equal("Varchar2", KindOf(p));
        Assert.Equal(ParameterDirection.Output, p.Direction);
        Assert.True(p.Size >= TiberoBindPlan.TextOutSize);
    }

    [Fact]
    public void Ref_cursor_output_and_lob_types_map_to_their_kinds()
    {
        var cmd = NewCommand();

        Assert.Equal("RefCursor", KindOf(TiberoBindBuilder.Create(Driver, cmd, "c", new BindValue(BindDataType.RefCursor, null, BindDirection.Out))));
        Assert.Equal("Clob", KindOf(TiberoBindBuilder.Create(Driver, cmd, "l", new BindValue(BindDataType.Json, "{}"))));
        Assert.Equal("Blob", KindOf(TiberoBindBuilder.Create(Driver, cmd, "b", new BindValue(BindDataType.Blob, new byte[] { 1 }))));
    }
}
