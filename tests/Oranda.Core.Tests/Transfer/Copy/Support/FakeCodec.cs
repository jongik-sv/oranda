using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Tests.Transfer.Copy;

internal sealed class FakeCodec : IBulkValueCodec
{
    public bool IsSupported(string serverType) => true;

    public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

    public BindDataType BindTypeOf(string serverType) => BindDataType.Varchar2;

    public string? ToKeyText(object? value, string serverType) => value?.ToString();

    public object? FromKeyText(string? text, string serverType) => text;

    public long? ToInt64(object? value) => value is long l ? l : null;

    public string? ToText(object? value) => value?.ToString();

    public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    public bool IsZero(object? value) => value is 0L;

    public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}
