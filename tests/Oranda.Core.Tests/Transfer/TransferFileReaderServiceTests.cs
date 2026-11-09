using System.Reflection;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Transfer;

namespace Oranda.Core.Tests.Transfer;

/// <summary><see cref="ITransferFileReader"/> 서비스가 정적 읽기와 같은 결과를 내고, DBMS 키 없이 등록된다.</summary>
public sealed class TransferFileReaderServiceTests
{
    private const string Text = "id,name\n1,\"a,b\"\n2,Lee\n3,Park\n";

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes(Text));

    [Fact]
    public void Service_returns_the_same_rows_as_the_static_reader() => AssertSameRows(new TransferFileReaderService());

#pragma warning disable CA1859 // 계약(인터페이스)을 통해 부르는 것이 시험의 요점이다
    private static void AssertSameRows(ITransferFileReader service)
#pragma warning restore CA1859
    {
        Assert.Equal(TransferFileReader.ReadRows(TransferFormat.Csv, Csv()).Select(Join), service.ReadRows(TransferFormat.Csv, Csv()).Select(Join));
        var preview = service.Preview(TransferFormat.Csv, Csv(), 2);
        Assert.Equal(2, preview.Count);
        Assert.Equal("id|name", Join(preview[0]));
        Assert.Equal("1|a,b", Join(preview[1]));
    }

    [Fact]
    public void Service_is_registered_without_a_dbms_key()
    {
        var attribute = typeof(TransferFileReaderService).GetCustomAttributes<OrandaServiceAttribute>(inherit: false).Single();

        Assert.Equal(typeof(ITransferFileReader), attribute.ServiceType);
        Assert.Null(attribute.ForDbms);
        Assert.Throws<NotSupportedException>(() => new TransferFileReaderService().ReadRows(TransferFormat.Json, Csv()).ToList());
    }

    private static string Join(object?[] row) => string.Join("|", row.Select(c => Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture)));
}
