using Oranda.App.Features.TableEdit;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>검사 줄 문구: ✔ 줄(같은 검사 코드 + Severity.Ok)은 실패 문구가 아니라 통과 문구다.</summary>
public class CheckFindingTextTests
{
    [Theory]
    [InlineData(CheckCode.Overflow, "새 길이에 모두 들어감")]
    [InlineData(CheckCode.NullRows, "NULL 행 없음")]
    [InlineData(CheckCode.ExpressionFails, "식 계산됨")]
    [InlineData(CheckCode.DuplicateKeys, "겹치는 키 없음")]
    public void Passed_lines_say_the_check_passed(CheckCode code, string expected)
    {
        var text = CheckFindingText.Format(new CheckFinding(CheckSeverity.Ok, code, "c1") { Subject = "STATUS", Count = 0 });

        Assert.Equal("✔ STATUS: " + expected, text);
    }

    [Theory]
    [InlineData("foreignKeysInOtherSchemas", "다른 스키마의 외래 키(사전 권한 없음)")]
    [InlineData("usedByVirtualColumn:V_TOTAL", "이 열을 쓰는 가상 열이 있음: V_TOTAL")]
    [InlineData("droppedColumn:My Col", "지우는 열 My Col 을(를) 씀")]
    [InlineData("droppedColumn:OLD_FLAG", "지우는 열 OLD_FLAG 을(를) 씀")]
    [InlineData("ORA-01722: invalid number", "ORA-01722: invalid number")]
    public void Detail_keys_become_text_and_other_text_stays(string detail, string expected) =>
        Assert.Equal(expected, CheckFindingText.DetailText(detail));

    [Fact]
    public void Lower_bound_conversion_count_says_at_least()
    {
        var text = CheckFindingText.Format(new CheckFinding(CheckSeverity.Block, CheckCode.ConversionFails, "c1") { Subject = "STATUS", Count = 3, Detail = "atLeast" });

        Assert.Equal("✖ STATUS: 변환 안 되는 행 3건 이상", text);
    }

    [Fact]
    public void Failing_lines_keep_the_failure_text()
    {
        var text = CheckFindingText.Format(new CheckFinding(CheckSeverity.Block, CheckCode.Overflow, "c1") { Subject = "STATUS", Count = 37 });

        Assert.StartsWith("✖ STATUS: 새 길이를 넘는 행 37건", text, StringComparison.Ordinal);
    }
}
