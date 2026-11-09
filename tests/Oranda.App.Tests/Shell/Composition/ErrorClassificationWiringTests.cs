using Oranda.App.Shell.Composition;
using Oranda.Core.Execution;
using Oranda.Language.Errors;

namespace Oranda.App.Tests.Shell.Composition;

/// <summary>A4b: 앱 조립이 오류 분류 퍼사드에 DBMS 선택기를 잇되, 이어도 Oracle 판정은 연결 전과 같다.</summary>
public sealed class ErrorClassificationWiringTests
{
    // 퍼사드 선택기는 프로세스 전체 정적이다(다른 조립 시험도 훑으면 잇는다). 이어도 Oracle 판정은 같으므로 되돌리지 않는다.
    [Fact]
    public void Deferred_scan_connects_the_selector_to_the_error_classification_facade()
    {
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-ecw-" + Guid.NewGuid().ToString("N")) });

        c.ScanDeferred();

        Assert.NotNull(DbErrorClassification.Selector);
    }

    [Theory]
    [InlineData("ORA-00942: table or view does not exist", true)]
    [InlineData("ORA-01031: insufficient privileges", true)]
    [InlineData("ORA-01039: insufficient privileges on underlying objects", false)] // 전체 Oracle 표에는 있지만 연결 전 표에는 없다 → 불변
    [InlineData("ORA-04043: object does not exist", false)]
    [InlineData("ORA-00001: unique constraint violated", false)]
    public void Oracle_no_access_judgement_is_the_same_with_the_selector_connected(string message, bool expected)
    {
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-ecw-" + Guid.NewGuid().ToString("N")) });
        c.ScanDeferred();

        Assert.NotNull(DbErrorClassification.Selector);
        Assert.Equal(expected, DbErrorClassification.IsNoAccess(new InvalidOperationException(message)));
        Assert.Equal(expected, DbErrorClassification.IsNoAccessTop(new InvalidOperationException(message)));
    }

    [Fact]
    public void Oracle_disconnect_and_read_only_numbers_are_the_same_with_the_selector_connected()
    {
        using var c = AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-ecw-" + Guid.NewGuid().ToString("N")) });
        c.ScanDeferred();

        Assert.True(DbErrorClassification.IsDisconnected(new InvalidOperationException("ORA-03113: end-of-file")));
        Assert.False(DbErrorClassification.IsDisconnected(new InvalidOperationException("ORA-12537: connection closed"))); // 전체 표에는 있으나 연결 전 표에는 없다
        Assert.True(DbErrorClassification.IsReadOnly(new InvalidOperationException("ORA-01456: read only")));
        Assert.Equal(DbErrorKind.InsufficientPrivileges, DbErrorClassification.LeadingKindOf(new InvalidOperationException("ORA-01031: x")));
    }
}
