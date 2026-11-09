using System.Data;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

public class TiberoColumnTypesTests
{
    [Theory]
    [InlineData(126, "VARCHAR2", ColumnDataKind.Text)]
    [InlineData(104, "CHAR", ColumnDataKind.Text)]
    [InlineData(119, "NVARCHAR2", ColumnDataKind.Text)]
    [InlineData(117, "NCHAR", ColumnDataKind.Text)]
    [InlineData(107, "NUMBER", ColumnDataKind.Number)]
    [InlineData(113, "NUMBER", ColumnDataKind.Number)]
    [InlineData(112, "NUMBER", ColumnDataKind.Number)]
    [InlineData(133, "BINARY_FLOAT", ColumnDataKind.BinaryFloat)]
    [InlineData(132, "BINARY_DOUBLE", ColumnDataKind.BinaryFloat)]
    [InlineData(106, "DATE", ColumnDataKind.Date)]
    [InlineData(123, "TIMESTAMP", ColumnDataKind.Timestamp)]
    [InlineData(124, "TIMESTAMP WITH LOCAL TIME ZONE", ColumnDataKind.Timestamp)]
    [InlineData(125, "TIMESTAMP WITH TIME ZONE", ColumnDataKind.TimestampTz)]
    [InlineData(115, "INTERVAL YEAR TO MONTH", ColumnDataKind.IntervalYearMonth)]
    [InlineData(114, "INTERVAL DAY TO SECOND", ColumnDataKind.IntervalDaySecond)]
    [InlineData(105, "CLOB", ColumnDataKind.Clob)]
    [InlineData(116, "NCLOB", ColumnDataKind.Clob)]
    [InlineData(102, "BLOB", ColumnDataKind.Blob)]
    [InlineData(120, "RAW", ColumnDataKind.Raw)]
    [InlineData(121, "REF CURSOR", ColumnDataKind.RefCursor)]
    [InlineData(134, "BOOLEAN", ColumnDataKind.Boolean)]
    public void Describe_maps_provider_type(int providerType, string name, ColumnDataKind kind)
    {
        Assert.Equal((name, kind), TiberoColumnTypes.Describe(providerType));
    }

    [Fact]
    public void Unknown_provider_type_is_other()
    {
        var (name, kind) = TiberoColumnTypes.Describe(999);
        Assert.Equal(ColumnDataKind.Other, kind);
        Assert.Contains("999", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(103, true)]
    [InlineData(113, true)]
    [InlineData(122, true)]
    [InlineData(107, false)]
    [InlineData(126, false)]
    public void Native_numeric_types_are_read_with_GetValue(int providerType, bool native)
    {
        Assert.Equal(native, TiberoColumnTypes.IsNativeNumeric(providerType));
    }
}

public class TiberoBindPlanTests
{
    [Theory]
    [InlineData(BindDataType.Varchar2, 126)]
    [InlineData(BindDataType.NVarchar2, 119)]
    [InlineData(BindDataType.FixedChar, 104)]
    [InlineData(BindDataType.Number, 107)]
    [InlineData(BindDataType.Date, 106)]
    [InlineData(BindDataType.Timestamp, 123)]
    [InlineData(BindDataType.TimestampLtz, 124)]
    [InlineData(BindDataType.Clob, 105)]
    [InlineData(BindDataType.Blob, 102)]
    [InlineData(BindDataType.Raw, 120)]
    [InlineData(BindDataType.Json, 105)]
    [InlineData(BindDataType.RefCursor, 121)]
    public void In_bind_maps_type(BindDataType type, int providerType)
    {
        var plan = TiberoBindPlan.For(new BindValue(type, null));
        Assert.Equal(providerType, plan.ProviderType);
        Assert.Equal(ParameterDirection.Input, plan.Direction);
        Assert.Equal(DBNull.Value, plan.Value);
    }

    [Fact]
    public void Boolean_in_is_sent_as_number()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Boolean, true));
        Assert.Equal(107, plan.ProviderType);
        Assert.Equal(1m, plan.Value);
        Assert.Equal(0m, TiberoBindPlan.For(new BindValue(BindDataType.Boolean, false)).Value);
    }

    [Fact]
    public void Timestamp_value_is_wrapped_to_keep_nanoseconds()
    {
        var dt = new DateTime(2024, 1, 2, 3, 4, 5);
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Timestamp, dt));
        Assert.Equal(TiberoBindWrapper.TimeStamp, plan.Wrapper);
        Assert.Equal(dt, plan.Value);
    }

    [Fact]
    public void TimestampTz_value_carries_offset_text()
    {
        var dto = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.TimestampTz, dto));
        Assert.Equal(TiberoBindWrapper.TimeStampTz, plan.Wrapper);
        Assert.Equal("+09:00", plan.TzOffset);
        Assert.Equal(dto, plan.Value);
    }

    [Fact]
    public void Out_string_gets_default_size_and_null_value()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Varchar2, "ignored", BindDirection.Out));
        Assert.Equal(ParameterDirection.Output, plan.Direction);
        Assert.Equal(TiberoBindPlan.DefaultOutSize, plan.Size);
        Assert.Equal(DBNull.Value, plan.Value);
        Assert.False(plan.OutAsText);
    }

    [Fact]
    public void Explicit_size_wins()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Raw, null, BindDirection.Out) { Size = 16 });
        Assert.Equal(16, plan.Size);
    }

    [Fact]
    public void InOut_keeps_input_value()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Varchar2, "in", BindDirection.InOut));
        Assert.Equal(ParameterDirection.InputOutput, plan.Direction);
        Assert.Equal("in", plan.Value);
    }

    [Theory]
    [InlineData(BindDataType.TimestampTz)]
    [InlineData(BindDataType.TimestampLtz)]
    [InlineData(BindDataType.Boolean)]
    [InlineData(BindDataType.BinaryDouble)]
    [InlineData(BindDataType.Number)] // 드라이버가 OUT NUMBER 를 decimal 로 바꾸다 28자리를 넘으면 OverflowException
    public void Out_types_the_driver_rejects_are_received_as_text(BindDataType type)
    {
        var plan = TiberoBindPlan.For(new BindValue(type, null, BindDirection.Out));
        Assert.Equal(126, plan.ProviderType);
        Assert.True(plan.OutAsText);
        Assert.Equal(TiberoBindPlan.TextOutSize, plan.Size);
        Assert.Equal(TiberoBindWrapper.None, plan.Wrapper);
    }

    [Fact]
    public void InOut_number_input_is_sent_as_invariant_text_and_in_number_stays_a_number()
    {
        var inout = TiberoBindPlan.For(new BindValue(BindDataType.Number, 1234.5m, BindDirection.InOut));
        var input = TiberoBindPlan.For(new BindValue(BindDataType.Number, 1234.5m));

        Assert.True(inout.OutAsText);
        Assert.Equal(126, inout.ProviderType);
        Assert.Equal("1234.5", inout.Value);
        Assert.False(input.OutAsText);
        Assert.Equal(107, input.ProviderType);
        Assert.Equal(1234.5m, input.Value);
    }

    [Fact]
    public void InOut_boolean_input_is_converted_to_text()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Boolean, true, BindDirection.InOut));
        Assert.True(plan.OutAsText);
        Assert.Equal("1", plan.Value);
    }

    [Fact]
    public void RefCursor_out_is_supported()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.RefCursor, null, BindDirection.Out));
        Assert.Equal(121, plan.ProviderType);
        Assert.False(plan.OutAsText);
    }

    [Fact]
    public void Null_boolean_in_is_a_number_null()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.Boolean, null));
        Assert.Equal(107, plan.ProviderType);
        Assert.Equal(DBNull.Value, plan.Value);
    }

    [Fact]
    public void TimestampTz_from_DateTime_uses_local_offset()
    {
        var dt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.TimestampTz, dt));
        Assert.Equal(TiberoBindWrapper.TimeStampTz, plan.Wrapper);
        Assert.Equal("+00:00", plan.TzOffset);
    }

    [Fact]
    public void TimestampTz_with_unsupported_value_is_passed_through()
    {
        var plan = TiberoBindPlan.For(new BindValue(BindDataType.TimestampTz, "text"));
        Assert.Equal(TiberoBindWrapper.None, plan.Wrapper);
        Assert.Equal("text", plan.Value);
    }

    [Fact]
    public void Text_out_size_is_at_least_text_out_size()
    {
        var small = TiberoBindPlan.For(new BindValue(BindDataType.BinaryDouble, null, BindDirection.Out) { Size = 8 });
        Assert.Equal(TiberoBindPlan.TextOutSize, small.Size);
        var large = TiberoBindPlan.For(new BindValue(BindDataType.BinaryDouble, null, BindDirection.Out) { Size = 500 });
        Assert.Equal(500, large.Size);
    }
}
