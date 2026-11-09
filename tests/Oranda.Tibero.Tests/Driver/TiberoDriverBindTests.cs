using System.Data;
using System.Data.Common;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Tests.Driver;

[Collection("FakeDriver")]
public class TiberoDriverBindTests
{
    private const string TypesNs = "Tibero.DataAccess.Types.";

    private static TiberoDriver Driver => FakeDriverHost.Driver;

    private static DbCommand NewCommand() => Driver.CreateConnection("Data Source=x").CreateCommand();

    private static DbParameter NewParameter() => NewCommand().CreateParameter();

    private static object Types(string name, params object?[] args) => FakeDriverHost.New(TypesNs + name, args);

    [Fact]
    public void Bind_exposes_version_location_and_factory()
    {
        Assert.Equal(new Version(1, 1, 35, 0), Driver.Version);
        Assert.EndsWith("Tibero.DataAccess.dll", Driver.Location, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TiberoClientFactory", Driver.Factory.GetType().Name);
    }

    [Fact]
    public void Fake_driver_is_not_in_the_test_output_root()
    {
        Assert.True(File.Exists(FakeDriverHost.DllPath));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Tibero.DataAccess.dll")));
    }

    [Fact]
    public void CreateConnection_sets_connection_string_and_stays_closed()
    {
        using var connection = Driver.CreateConnection("Data Source=abc");

        Assert.Equal("TiberoConnection", connection.GetType().Name);
        Assert.Equal("Data Source=abc", connection.ConnectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void CreateConnectionStringBuilder_returns_driver_builder()
    {
        Assert.Equal("TiberoConnectionStringBuilder", Driver.CreateConnectionStringBuilder().GetType().Name);
    }

    [Fact]
    public void Command_setters_write_the_real_properties()
    {
        var cmd = NewCommand();

        Driver.SetBindByName(cmd);
        Driver.SetInitialLobFetchSize(cmd, 4096);
        Driver.SetArrayBindCount(cmd, 7);

        Assert.Equal(true, FakeDriverHost.Get(cmd, "BindByName"));
        Assert.Equal(4096, FakeDriverHost.Get(cmd, "InitialLOBFetchSize"));
        Assert.Equal(7, FakeDriverHost.Get(cmd, "ArrayBindCount"));

        Driver.SetBindByName(cmd, false);
        Assert.Equal(false, FakeDriverHost.Get(cmd, "BindByName"));
    }

    [Fact]
    public void Command_setters_reject_foreign_commands_and_null()
    {
        using var other = new ForeignCommand();

        Assert.Throws<ArgumentException>(() => Driver.SetBindByName(other));
        Assert.Throws<ArgumentNullException>(() => Driver.SetArrayBindCount(null!, 1));
    }

    [Fact]
    public void SetParameterKind_maps_every_kind_by_name()
    {
        foreach (var kind in Enum.GetValues<TiberoParamKind>())
        {
            var p = NewParameter();

            Driver.SetParameterKind(p, kind);

            Assert.Equal(kind.ToString(), FakeDriverHost.Get(p, "TiberoDbType")!.ToString());
        }
    }

    [Fact]
    public void SetParameterKind_rejects_foreign_parameter()
    {
        Assert.Throws<ArgumentException>(() => Driver.SetParameterKind(new ForeignParameter(), TiberoParamKind.Clob));
    }

    [Fact]
    public void KindOfProviderType_maps_driver_integers()
    {
        Assert.Equal(TiberoParamKind.Decimal, Driver.KindOfProviderType(107));
        Assert.Equal(TiberoParamKind.Varchar2, Driver.KindOfProviderType(126));
        Assert.Null(Driver.KindOfProviderType(99999));
    }

    [Fact]
    public void SetCollectionType_maps_by_name()
    {
        var p = NewParameter();

        Driver.SetCollectionType(p, TiberoCollectionKind.AssociativeArray);
        Assert.Equal("PLSQLAssociativeArray", FakeDriverHost.Get(p, "CollectionType")!.ToString());

        Driver.SetCollectionType(p, TiberoCollectionKind.None);
        Assert.Equal("None", FakeDriverHost.Get(p, "CollectionType")!.ToString());
    }

    [Fact]
    public void CreateTimeStamp_keeps_nanoseconds_and_drops_datetime_fraction()
    {
        var value = Driver.CreateTimeStamp(new DateTime(2026, 11, 9, 1, 2, 3).AddTicks(1234567), 123_456_789);

        Assert.Equal("TiberoTimeStamp", value.GetType().Name);
        Assert.Equal(123_456_789, FakeDriverHost.Get(value, "Nanosecond"));
        Assert.Equal(new DateTime(2026, 11, 9, 1, 2, 3), FakeDriverHost.Get(value, "Value"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000_000)]
    public void CreateTimeStamp_rejects_out_of_range_nanoseconds(int nanosecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Driver.CreateTimeStamp(DateTime.UnixEpoch, nanosecond));
    }

    [Fact]
    public void CreateTimeStampTz_builds_driver_value()
    {
        var local = new DateTime(2026, 1, 2, 3, 4, 5);

        var value = Driver.CreateTimeStampTz(local, "+09:00");

        Assert.Equal("TiberoTimeStampTZ", value.GetType().Name);
        Assert.Equal(local, FakeDriverHost.Get(value, "Value"));
        Assert.Equal("+09:00", FakeDriverHost.Get(value, "TimeZone"));
    }

    [Theory]
    [InlineData("KST")]
    [InlineData("+9:00")]
    [InlineData("+09:60")]
    [InlineData("")]
    public void CreateTimeStampTz_rejects_bad_offsets(string offset)
    {
        Assert.Throws<ArgumentException>(() => Driver.CreateTimeStampTz(DateTime.UnixEpoch, offset));
    }

    private static DbDataReader ReaderOf(object value, Type type)
    {
        var table = new DataTable();
        table.Columns.Add("c", type);
        table.Rows.Add(value);
        var reader = FakeDriverHost.NewReader(table);
        Assert.True(reader.Read());
        return reader;
    }

    [Fact]
    public void ReadRawBytes_returns_bytes_or_null()
    {
        using var set = ReaderOf(new byte[] { 1, 2, 3 }, typeof(byte[]));
        using var unset = ReaderOf(DBNull.Value, typeof(byte[]));

        Assert.Equal(new byte[] { 1, 2, 3 }, Driver.ReadRawBytes(set, 0));
        Assert.Null(Driver.ReadRawBytes(unset, 0));
    }

    [Fact]
    public void Reader_methods_reject_foreign_readers()
    {
        using var other = new DataTable().CreateDataReader();

        Assert.Throws<ArgumentException>(() => Driver.ReadRawBytes(other, 0));
        Assert.Throws<ArgumentNullException>(() => Driver.ReadClob(null!, 0));
    }

    [Fact]
    public void ReadClob_reads_chars_with_ucs2_length()
    {
        using var reader = ReaderOf("가나다abc", typeof(string));

        using var lob = Driver.ReadClob(reader, 0)!;

        Assert.Equal(TiberoLobKind.Clob, lob.Kind);
        Assert.False(lob.IsNClob);
        Assert.Equal(12, lob.Length);
        var chars = new char[10];
        Assert.Equal(6, lob.Read(chars, 0, 10));
        Assert.Equal("가나다abc", new string(chars, 0, 6));
        Assert.Equal(0, lob.Read(chars, 0, 10));
    }

    [Fact]
    public void ReadClob_returns_null_for_null_cell()
    {
        using var reader = ReaderOf(DBNull.Value, typeof(string));

        Assert.Null(Driver.ReadClob(reader, 0));
    }

    [Fact]
    public void ReadClob_position_is_bytes_and_odd_is_rejected()
    {
        using var reader = ReaderOf("abcd", typeof(string));
        using var lob = Driver.ReadClob(reader, 0)!;

        lob.Position = 4;
        var chars = new char[2];
        Assert.Equal(2, lob.Read(chars, 0, 2));
        Assert.Equal("cd", new string(chars));
        Assert.Throws<ArgumentException>(() => lob.Position = 3);
    }

    [Fact]
    public void ReadClob_gives_a_new_locator_each_call()
    {
        using var reader = ReaderOf("xyz", typeof(string));
        using var first = Driver.ReadClob(reader, 0)!;
        using var second = Driver.ReadClob(reader, 0)!;

        first.Position = 4;

        Assert.Equal(0, second.Position);
    }

    [Fact]
    public void Clob_clone_has_independent_position()
    {
        using var reader = ReaderOf("hello", typeof(string));
        using var lob = Driver.ReadClob(reader, 0)!;
        lob.Position = 6;

        using var copy = lob.Clone();

        Assert.Equal(0, copy.Position);
        Assert.Equal(6, lob.Position);
        var chars = new char[5];
        Assert.Equal(5, copy.Read(chars, 0, 5));
        Assert.Equal(6, lob.Position);
    }

    [Fact]
    public void Clob_rejects_byte_reads_and_stream()
    {
        using var reader = ReaderOf("a", typeof(string));
        using var lob = Driver.ReadClob(reader, 0)!;

        Assert.Throws<NotSupportedException>(() => lob.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => lob.AsStream());
    }

    [Fact]
    public void ReadBlob_reads_bytes_and_exposes_stream()
    {
        using var reader = ReaderOf(new byte[] { 9, 8, 7, 6 }, typeof(byte[]));
        using var lob = Driver.ReadBlob(reader, 0)!;

        Assert.Equal(TiberoLobKind.Blob, lob.Kind);
        Assert.Equal(4, lob.Length);
        var bytes = new byte[8];
        Assert.Equal(4, lob.Read(bytes, 0, 8));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, bytes[..4]);
        Assert.Equal(4, lob.AsStream().Position);
        Assert.Throws<NotSupportedException>(() => lob.Read(new char[1], 0, 1));
    }

    [Fact]
    public void Blob_clone_restarts_at_zero_and_null_cell_is_null()
    {
        using var reader = ReaderOf(new byte[] { 1, 2 }, typeof(byte[]));
        using var lob = Driver.ReadBlob(reader, 0)!;
        lob.Position = 2;

        using var copy = lob.Clone();

        Assert.Equal(0, copy.Position);
        using var nullReader = ReaderOf(DBNull.Value, typeof(byte[]));
        Assert.Null(Driver.ReadBlob(nullReader, 0));
    }

    [Fact]
    public void ReadTimeStampLtz_returns_value_or_null()
    {
        var when = new DateTime(2026, 5, 6, 7, 8, 9);
        using var set = ReaderOf(when, typeof(DateTime));
        using var unset = ReaderOf(DBNull.Value, typeof(DateTime));

        Assert.Equal(when, Driver.ReadTimeStampLtz(set, 0));
        Assert.Null(Driver.ReadTimeStampLtz(unset, 0));
    }

    // ---- 공급자 값 ----

    [Fact]
    public void IsProviderNull_handles_clr_and_driver_values()
    {
        Assert.True(Driver.IsProviderNull(null));
        Assert.True(Driver.IsProviderNull(DBNull.Value));
        Assert.True(Driver.IsProviderNull(FakeDriverHost.Static(TypesNs + "TiberoString", "Null")));
        Assert.False(Driver.IsProviderNull(Types("TiberoString", "x")));
        Assert.False(Driver.IsProviderNull("x"));
    }

    [Fact]
    public void Convert_null_rows()
    {
        var clr = Driver.ConvertProviderValue(DBNull.Value);
        var driver = Driver.ConvertProviderValue(FakeDriverHost.Static(TypesNs + "TiberoString", "Null"));

        Assert.Equal(new TiberoOutValue(TiberoValueKind.Null, null, true, null), clr);
        Assert.Equal(TiberoValueKind.Null, driver.Kind);
        Assert.True(driver.IsNull);
        Assert.Equal("TiberoString", driver.ProviderTypeName);
        Assert.Null(driver.Value);
    }

    [Fact]
    public void Convert_string_decimal_date()
    {
        var text = Driver.ConvertProviderValue(Types("TiberoString", "안녕"));
        var number = Driver.ConvertProviderValue(Types("TiberoDecimal", 12.5m));
        var date = Driver.ConvertProviderValue(Types("TiberoDate", new DateTime(2026, 1, 2, 3, 4, 5)));

        Assert.Equal(TiberoValueKind.String, text.Kind);
        Assert.Equal("안녕", text.Value);
        Assert.Equal(TiberoValueKind.Decimal, number.Kind);
        Assert.Equal("12.5", number.Value);
        Assert.Equal(TiberoValueKind.Date, date.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), date.Value);
    }

    [Fact]
    public void Convert_decimal_text_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var number = Driver.ConvertProviderValue(Types("TiberoDecimal", 1.5m));

            Assert.Equal("1.5", number.Value);
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Convert_timestamp_keeps_seconds_and_nanoseconds()
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(999);
        var stamp = Driver.ConvertProviderValue(Types("TiberoTimeStamp", value, 123_456_789));
        var ltz = Driver.ConvertProviderValue(Types("TiberoTimeStampLTZ", value, 42));

        Assert.Equal(TiberoValueKind.TimeStamp, stamp.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), stamp.Value);
        Assert.Equal(123_456_789, stamp.Nanosecond);
        Assert.Equal(TiberoValueKind.TimeStampLtz, ltz.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), ltz.Value);
        Assert.Equal(42, ltz.Nanosecond);
    }

    [Fact]
    public void Convert_binary_and_other_driver_types()
    {
        var binary = Driver.ConvertProviderValue(Types("TiberoBinary", new byte[] { 1, 2 }));
        var tz = Driver.ConvertProviderValue(Types("TiberoTimeStampTZ", new DateTime(2026, 1, 1), "+09:00"));

        Assert.Equal(TiberoValueKind.Binary, binary.Kind);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(binary.Value));
        Assert.Equal(TiberoValueKind.Other, tz.Kind);
        Assert.IsType<string>(tz.Value);
        Assert.Equal("TiberoTimeStampTZ", tz.ProviderTypeName);
    }

    [Fact]
    public void Convert_lobs_become_independent_tibero_lobs()
    {
        var clob = (Stream)Types("TiberoClob", "abc", false);
        var blob = Types("TiberoBlob", new byte[] { 1, 2, 3 });

        var c = Driver.ConvertProviderValue(clob);
        var b = Driver.ConvertProviderValue(blob);

        using var clobLob = Assert.IsType<TiberoLob>(c.Value);
        using var blobLob = Assert.IsType<TiberoLob>(b.Value);
        Assert.Equal(TiberoValueKind.Lob, c.Kind);
        Assert.Equal(TiberoLobKind.Clob, clobLob.Kind);
        Assert.Equal(TiberoLobKind.Blob, blobLob.Kind);
        clobLob.Position = 2;
        Assert.Equal(0, clob.Position);
    }

    [Fact]
    public void Convert_nclob_reports_nclob_kind()
    {
        var c = Driver.ConvertProviderValue(Types("TiberoClob", "abc", true));

        using var lob = Assert.IsType<TiberoLob>(c.Value);
        Assert.True(lob.IsNClob);
        Assert.Equal(TiberoLobKind.NClob, lob.Kind);
    }

    [Fact]
    public void Convert_ref_cursor_gives_data_reader()
    {
        var table = new DataTable();
        table.Columns.Add("a", typeof(int));
        table.Rows.Add(5);
        var cursor = Types("TiberoRefCursor", table);

        var result = Driver.ConvertProviderValue(cursor);

        using var reader = Assert.IsAssignableFrom<DbDataReader>(result.Value);
        Assert.Equal(TiberoValueKind.RefCursor, result.Kind);
        Assert.True(reader.Read());
        Assert.Equal(5, reader.GetInt32(0));
    }

    [Fact]
    public void Convert_clr_values_pass_through_and_sbyte_array_is_copied()
    {
        var number = Driver.ConvertProviderValue(42L);
        var bytes = Driver.ConvertProviderValue(new sbyte[] { -1, 2 });

        Assert.Equal(TiberoValueKind.Clr, number.Kind);
        Assert.Equal(42L, number.Value);
        Assert.Null(number.ProviderTypeName);
        Assert.Equal(TiberoValueKind.Clr, bytes.Kind);
        Assert.Equal(new byte[] { 255, 2 }, Assert.IsType<byte[]>(bytes.Value));
    }

    [Fact]
    public void Convert_never_returns_a_driver_object()
    {
        var table = new DataTable();
        table.Columns.Add("a", typeof(int));
        object[] inputs =
        [
            Types("TiberoString", "x"), Types("TiberoDecimal", 1m), Types("TiberoDate", DateTime.UnixEpoch),
            Types("TiberoTimeStamp", DateTime.UnixEpoch, 1), Types("TiberoTimeStampLTZ", DateTime.UnixEpoch, 1),
            Types("TiberoTimeStampTZ", DateTime.UnixEpoch, "+00:00"), Types("TiberoBinary", new byte[1]),
            Types("TiberoClob", "x", false), Types("TiberoBlob", new byte[1]), Types("TiberoRefCursor", table),
        ];

        foreach (var input in inputs)
        {
            var value = Driver.ConvertProviderValue(input).Value;
            try
            {
                Assert.NotNull(value);

                // RefCursor 는 드라이버가 구현한 리더를 기본 형식(DbDataReader)으로 준다 — 계약상 허용(문서 2.5).
                if (value is not System.Data.Common.DbDataReader)
                {
                    Assert.NotSame(FakeDriverHost.Assembly, value.GetType().Assembly);
                }
            }
            finally
            {
                (value as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public void ReadOutValue_converts_the_parameter_value()
    {
        var p = NewParameter();
        p.Value = Types("TiberoString", "out");

        Assert.Equal("out", Driver.ReadOutValue(p).Value);
        Assert.Throws<ArgumentException>(() => Driver.ReadOutValue(new ForeignParameter()));
    }

    // ---- 예외 ----

    [Fact]
    public void TryReadError_reads_server_error()
    {
        var ex = FakeDriverHost.ServerError(-17002, "boom");

        Assert.True(Driver.TryReadError(ex, out var error));
        Assert.Equal(new TiberoErrorInfo(-17002, "boom", TiberoErrorOrigin.Server), error);
    }

    [Fact]
    public void TryReadError_reads_client_error()
    {
        var ex = FakeDriverHost.ClientError(-90, "net");

        Assert.True(Driver.TryReadError(ex, out var error));
        Assert.Equal(new TiberoErrorInfo(-90, "net", TiberoErrorOrigin.Client), error);
    }

    [Fact]
    public void TryReadError_ignores_other_exceptions_and_inner_ones()
    {
        var wrapped = new InvalidOperationException("outer", FakeDriverHost.ServerError(-1, "inner"));

        Assert.False(Driver.TryReadError(new InvalidOperationException("x"), out var none));
        Assert.Equal(default, none);
        Assert.False(Driver.TryReadError(wrapped, out _));
    }

    [Fact]
    public void Handler_error_flows_through_command_execution()
    {
        try
        {
            FakeDriverHost.SetHandler(_ => throw FakeDriverHost.ServerError(-5, "denied"));
            using var connection = Driver.CreateConnection("x");
            using var cmd = connection.CreateCommand();

            var ex = Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());

            Assert.True(Driver.TryReadError(ex, out var error));
            Assert.Equal(-5, error.Number);
        }
        finally
        {
            FakeDriverHost.Reset();
        }
    }

    // ---- 묶기 실패 ----

    [Fact]
    public void Bind_against_unrelated_assembly_lists_missing_members()
    {
        var ex = Assert.Throws<TiberoDriverBindException>(() => TiberoDriver.Bind(typeof(object).Assembly));

        Assert.True(ex.MissingMembers.Count > 20);
        Assert.Contains("TiberoCommand.BindByName", ex.MissingMembers);
        Assert.Contains("TiberoDbType.Varchar2", ex.MissingMembers);
        Assert.Contains("TbOdpException.ErrorCode", ex.MissingMembers);
        Assert.False(ex.Check.IsUsable);
        Assert.StartsWith("이 드라이버는 쓸 수 없습니다(없는 멤버: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Check.Reason, ex.Message);
    }

    [Fact]
    public void Bind_against_test_assembly_fails_too()
    {
        var ex = Assert.Throws<TiberoDriverBindException>(() => TiberoDriver.Bind(typeof(TiberoDriverBindTests).Assembly));

        Assert.Contains("TiberoParameter.CollectionType", ex.MissingMembers);
    }

    [Fact]
    public void Unavailable_exception_message_defaults_to_not_specified()
    {
        var ex = new TiberoDriverUnavailableException(DriverCheck.Missing);

        Assert.Equal(TiberoDriver.NotSpecifiedMessage, ex.Message);
        Assert.False(ex.Check.IsUsable);
    }
}
