using System;
using System.Collections.Generic;
using System.Linq;

namespace Oranda.App.Shell.Layout;

/// <summary>모니터 한 대의 작업 영역(작업 표시줄 제외, DIP).</summary>
public sealed record ScreenArea(string Name, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>메인 창을 어디에 어떤 크기로 열지 정한 결과(DIP).</summary>
public sealed record ResolvedPlacement(int X, int Y, int Width, int Height, WindowStateKind State, string? Screen);

/// <summary>
/// 창 위치·크기 복원 규칙(ui-shell.md 5장, data-model 2.5). 순수 계산이라 모니터 구성을 바꿔 가며 시험할 수 있다.
/// </summary>
public static class WindowPlacementPolicy
{
    public const int MinWidth = 960;

    public const int MinHeight = 600;

    /// <summary>첫 실행 창 상한.</summary>
    public const int FirstRunMaxWidth = 1280;

    public const int FirstRunMaxHeight = 800;

    /// <summary>저장된 위치와 모니터 목록으로 열 자리를 정한다.</summary>
    public static ResolvedPlacement Resolve(WindowPlacement? saved, IReadOnlyList<ScreenArea> screens)
    {
        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? (screens.Count > 0 ? screens[0] : null)
            ?? new ScreenArea("primary", 0, 0, FirstRunMaxWidth, FirstRunMaxHeight, true);

        if (saved is null)
        {
            var w = Math.Min(FirstRunMaxWidth, (int)(primary.Width * 0.8));
            var h = Math.Min(FirstRunMaxHeight, (int)(primary.Height * 0.8));
            return Centered(primary, w, h, WindowStateKind.Normal);
        }

        // 저장된 화면이 아직 있으면 그 화면, 없으면 위치가 걸치는 화면, 그것도 없으면 주 화면 가운데.
        var target = screens.FirstOrDefault(s => s.Name == saved.Screen);
        var width = saved.Width;
        var height = saved.Height;
        var state = saved.State;
        if (target is null)
        {
            target = screens.FirstOrDefault(s => Overlaps(s, saved.X, saved.Y, width, height));
            if (target is null || saved.Screen is not null)
            {
                // 화면 이름이 있었는데 사라졌다 = 연결이 끊긴 화면 → 주 화면 가운데.
                return Centered(primary, width, height, state);
            }
        }

        if (!Overlaps(target, saved.X, saved.Y, width, height))
        {
            return Centered(target, width, height, state);
        }

        var (cw, ch) = Clamp(target, width, height);
        var x = Math.Clamp(saved.X, target.X, Math.Max(target.X, target.Right - cw));
        var y = Math.Clamp(saved.Y, target.Y, Math.Max(target.Y, target.Bottom - ch));
        return new ResolvedPlacement(x, y, cw, ch, state, target.Name);
    }

    /// <summary>화면 작업 영역 안에 들어오는 최소 크기: 960×600, 영역이 더 작으면 영역 크기.</summary>
    public static (int Width, int Height) MinimumFor(ScreenArea area) => (Math.Min(MinWidth, area.Width), Math.Min(MinHeight, area.Height));

    /// <summary>화면보다 크지 않고 최소 크기보다 작지 않게 맞춘다.</summary>
    public static (int Width, int Height) Clamp(ScreenArea area, int width, int height)
    {
        var (minW, minH) = MinimumFor(area);
        return (Math.Clamp(width, minW, area.Width), Math.Clamp(height, minH, area.Height));
    }

    private static ResolvedPlacement Centered(ScreenArea area, int width, int height, WindowStateKind state)
    {
        var (w, h) = Clamp(area, width, height);
        return new ResolvedPlacement(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h, state, area.Name);
    }

    /// <summary>창의 제목 줄 쪽 일부(위쪽 100×40)가 화면에 걸치면 사용자가 잡을 수 있다고 본다.</summary>
    private static bool Overlaps(ScreenArea s, int x, int y, int width, int height)
    {
        var right = x + Math.Min(width, 100);
        var bottom = y + Math.Min(height, 40);
        return x < s.Right && right > s.X && y < s.Bottom && bottom > s.Y;
    }
}
