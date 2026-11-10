using Avalonia.Controls;
using Avalonia.Media;
using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 환경 칩·머리 띠 색: 접속 관리자 목록(LabelColorRules.Effective)과 같은 규칙. 사용자 색 우선, 운영은 항상 테마 빨강.

public sealed class EnvironmentColorsTests
{
    [Theory]
    [InlineData(ConnectionEnvironment.Test, "#2E7D32", true)]
    [InlineData(ConnectionEnvironment.Development, " #1565C0 ", true)]
    [InlineData(ConnectionEnvironment.Production, "#2E7D32", false)] // 운영은 사용자 색을 무시
    [InlineData(ConnectionEnvironment.Test, null, false)]
    [InlineData(ConnectionEnvironment.Test, "green", false)]
    public void User_color_applies_only_to_non_production_with_a_valid_color(ConnectionEnvironment env, string? color, bool expected)
        => Assert.Equal(expected, EnvironmentColors.TryUserColor(env, color, out _));

    [Fact]
    public void Foreground_is_black_on_light_and_white_on_dark()
    {
        Assert.Equal(Colors.Black, EnvironmentColors.ForegroundOn(Color.Parse("#F0A04B")));
        Assert.Equal(Colors.White, EnvironmentColors.ForegroundOn(Color.Parse("#107C10")));
        Assert.Equal(Colors.White, EnvironmentColors.ForegroundOn(Color.Parse("#B65A00")));
    }

    [Fact]
    public void Chip_with_a_user_color_uses_it_and_a_production_chip_keeps_the_theme_brush()
    {
        var chip = new Border();
        var text = new TextBlock();
        EnvironmentColors.ApplyChip(chip, text, ConnectionEnvironment.Test, "#2E7D32", "OrandaEnvTestBrush");
        Assert.Equal(Color.Parse("#2E7D32"), Assert.IsAssignableFrom<ISolidColorBrush>(chip.Background).Color); // 접속 관리자 목록과 같은 초록
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

        var prod = new Border();
        EnvironmentColors.ApplyChip(prod, new TextBlock(), ConnectionEnvironment.Production, "#2E7D32", "OrandaEnvProductionBrush");
        Assert.Null(prod.Background); // 리소스 묶음(테마 빨강)이라 이 단독 컨트롤에는 아직 값이 없다 — 사용자 색이 아니다
    }

    [Fact]
    public void Frame_takes_the_user_color_and_falls_back_to_the_style_when_there_is_none()
    {
        var frame = new Border();
        EnvironmentColors.ApplyFrame(frame, ConnectionEnvironment.Test, "#2E7D32");
        Assert.Equal(Color.Parse("#2E7D32"), Assert.IsAssignableFrom<ISolidColorBrush>(frame.BorderBrush).Color);

        EnvironmentColors.ApplyFrame(frame, ConnectionEnvironment.Production, "#2E7D32");
        Assert.Null(frame.BorderBrush); // 직접 얹은 값을 지워 env-production 스타일 색으로
    }
}
