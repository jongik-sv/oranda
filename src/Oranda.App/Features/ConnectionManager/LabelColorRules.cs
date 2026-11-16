using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>환경 라벨 색 규칙(ui-shell.md 4.1): 빨강 계열은 운영만 쓴다.</summary>
public static class LabelColorRules
{
    /// <summary>운영이 아닌 접속에 빨강 계열을 넣었을 때(입력·고르기 같은 글).</summary>
    public const string RedReservedMessage = "빨강 계열 색은 운영 접속만 씁니다. 다른 색을 고르세요.";

    /// <summary>바이트 셋을 <c>#RRGGBB</c>(대문자)로.</summary>
    public static string ToHex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    /// <summary>화면에 보일 라벨 색: 운영은 항상 빨강, 나머지는 사용자 색(형식이 맞을 때) 또는 환경 기본색.</summary>
    public static string Effective(ConnectionEnvironment environment, string? color) => environment switch
    {
        ConnectionEnvironment.Production => "#C42B1C",
        _ when TryParse(color?.Trim(), out var r, out var g, out var b) => ToHex(r, g, b),
        ConnectionEnvironment.Test => "#B65A00",
        _ => "#107C10",
    };

    /// <summary><c>#RRGGBB</c> 를 읽는다. 형식이 틀리면 false.</summary>
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return false;
        }

        return byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    /// <summary>색상 각도 345°~15°, 채도 50% 이상이면 빨강 계열.</summary>
    public static bool IsRedFamily(string? hex)
    {
        if (!TryParse(hex, out var r, out var g, out var b))
        {
            return false;
        }

        double rf = r / 255d, gf = g / 255d, bf = b / 255d;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        if (delta == 0)
        {
            return false;
        }

        var lightness = (max + min) / 2;
        var saturation = delta / (1 - Math.Abs((2 * lightness) - 1));
        double hue;
        if (max == rf)
        {
            hue = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            hue = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            hue = 60 * (((rf - gf) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return saturation >= 0.5 && (hue >= 345 || hue <= 15);
    }
}
