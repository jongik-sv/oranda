using Avalonia.Controls;
using Avalonia.Media;
using Oranda.Core.Connection;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 환경 칩·머리 띠 색(ui-shell.md 4.1): 접속 관리자 목록과 같은 규칙(<see cref="LabelColorRules.Effective"/>)을 따른다.
/// 운영 = 항상 테마의 빨강(리소스 키), 그 밖에 사용자가 고른 색이 있으면 그 색(칩 글자는 밝기로 흑·백), 없으면 테마의 환경 기본색(리소스 키).
/// </summary>
public static class EnvironmentColors
{
    /// <summary>사용자 색을 쓰는 접속이면 그 색. 운영이거나 색이 없거나 형식이 틀리면 false(테마 리소스 키를 쓴다).</summary>
    public static bool TryUserColor(ConnectionEnvironment environment, string? color, out Color value)
    {
        value = default;
        if (environment == ConnectionEnvironment.Production || !LabelColorRules.TryParse(color?.Trim(), out var r, out var g, out var b))
        {
            return false;
        }

        value = Color.FromRgb(r, g, b);
        return true;
    }

    /// <summary>색 바탕 위 글자 색: 밝은 바탕이면 검정, 어두운 바탕이면 흰색(WCAG 상대 휘도 기준).</summary>
    public static Color ForegroundOn(Color background)
    {
        static double Channel(byte c)
        {
            var s = c / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Channel(background.R)) + (0.7152 * Channel(background.G)) + (0.0722 * Channel(background.B));
        return luminance > 0.179 ? Colors.Black : Colors.White; // 흑·백 대비가 같아지는 휘도
    }

    /// <summary>칩 바탕(<paramref name="brushKey"/> 는 테마 기본 키)과 글자. 사용자 색이면 바탕·글자 모두 고정 브러시, 아니면 테마 리소스를 묶는다.</summary>
    public static void ApplyChip(Border chip, TextBlock text, ConnectionEnvironment environment, string? color, string brushKey)
    {
        if (TryUserColor(environment, color, out var c))
        {
            chip.Background = new SolidColorBrush(c);
            text.Foreground = new SolidColorBrush(ForegroundOn(c));
        }
        else
        {
            chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable(brushKey));
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("OrandaEnvChipForegroundBrush"));
        }
    }

    /// <summary>머리 띠(바탕 + 글자들). 사용자 색이면 고정 브러시, 아니면 테마 리소스(<paramref name="foregroundKey"/> 가 글자 키).</summary>
    public static void ApplyHeader(Border header, IEnumerable<TextBlock> texts, ConnectionEnvironment environment, string? color, string brushKey, string? foregroundKey)
    {
        var user = TryUserColor(environment, color, out var c);
        if (user)
        {
            header.Background = new SolidColorBrush(c);
        }
        else
        {
            header.Bind(Border.BackgroundProperty, header.GetResourceObservable(brushKey));
        }

        foreach (var t in texts)
        {
            if (user)
            {
                t.Foreground = new SolidColorBrush(ForegroundOn(c));
            }
            else if (foregroundKey is not null)
            {
                t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(foregroundKey));
            }
        }
    }

    /// <summary>
    /// 테두리(env-frame 클래스 스타일이 테마 색을 준다). 사용자 색이면 그 색을 직접 얹고, 아니면 직접 얹은 값을 지워 스타일 색으로 돌린다.
    /// </summary>
    public static void ApplyFrame(Border frame, ConnectionEnvironment? environment, string? color)
    {
        if (environment is { } env && TryUserColor(env, color, out var c))
        {
            frame.BorderBrush = new SolidColorBrush(c);
        }
        else
        {
            frame.ClearValue(Border.BorderBrushProperty);
        }
    }

    /// <summary>환경 기본 리소스 키(운영·테스트·개발).</summary>
    public static string BrushKeyOf(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Production => "OrandaEnvProductionBrush",
        ConnectionEnvironment.Test => "OrandaEnvTestBrush",
        _ => "OrandaEnvDevelopmentBrush",
    };
}
