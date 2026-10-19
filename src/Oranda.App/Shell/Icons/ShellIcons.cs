using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace Oranda.App.Shell.Icons;

/// <summary>
/// 툴바·메뉴 아이콘(ui-shell.md 「아이콘」: 벡터만, 툴바 16 DIP). 명령의 <c>Icon</c> 키로 찾는다.
/// 경로는 20×20 격자 그대로 그려서(Viewbox 안 Canvas) 아이콘마다 여백이 달라도 크기가 고르다.
/// 색은 <c>OrandaIcon&lt;Tone&gt;Brush</c> 리소스(밝게·어둡게 두 벌)에서만 받는다.
/// </summary>
public static partial class ShellIcons
{
    private const double Grid = 20;

    private static readonly Dictionary<string, (string Tone, Geometry Geometry)> Cache = Build();

    /// <summary>등록된 아이콘 키 전체(시험·설정 화면용).</summary>
    public static IReadOnlyCollection<string> Keys => Cache.Keys;

    public static bool Contains(string? key) => key is not null && Cache.ContainsKey(key);

    /// <summary>아이콘 컨트롤. 키를 모르면 null.</summary>
    public static Control? Create(string? key, double size = 16)
    {
        if (key is null || !Cache.TryGetValue(key, out var icon))
        {
            return null;
        }

        var path = new Path { Data = icon.Geometry };
        path.Bind(Shape.FillProperty, path.GetResourceObservable("OrandaIcon" + icon.Tone + "Brush"));
        var canvas = new Canvas { Width = Grid, Height = Grid, Children = { path } };
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas, Classes = { "shell-icon" } };
    }

    private static Dictionary<string, (string, Geometry)> Build()
    {
        var map = new Dictionary<string, (string, Geometry)>(StringComparer.Ordinal);
        foreach (var (key, tone, data) in Data)
        {
            map[key] = (tone, Geometry.Parse(data));
        }

        return map;
    }
}
