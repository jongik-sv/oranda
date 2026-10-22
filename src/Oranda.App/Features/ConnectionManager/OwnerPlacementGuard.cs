using System;
using Avalonia;
using Avalonia.Controls;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 소유 창(메인 창) 위에 대화 상자를 여는 동안 메인 창의 위치·크기를 그대로 둔다(TSK-13-24).
/// 고정 크기(960×640)의 자식 창이 화면 경계 보정을 받을 때 소유 창까지 밀려 하단이 화면 밖으로 나가던 증상을 막는다.
/// 모달이라 사용자가 그 사이에 메인 창을 옮길 수 없으므로 바뀐 위치는 곧 복원 대상이다.
/// </summary>
internal static class OwnerPlacementGuard
{
    /// <summary>자식 창이 닫힐 때까지 소유 창의 위치·크기를 지금 값으로 지킨다. 일반 상태(최대화·전체 화면 아님)일 때만 지킨다.</summary>
    public static void Hold(Window owner, Window child)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(child);
        if (owner.WindowState != WindowState.Normal)
        {
            return;
        }

        var position = owner.Position;
        var size = owner.ClientSize;
        var restoring = false;

        void Restore()
        {
            if (restoring || owner.WindowState != WindowState.Normal)
            {
                return;
            }

            restoring = true;
            try
            {
                if (owner.Position != position)
                {
                    owner.Position = position;
                }

                if (owner.ClientSize != size)
                {
                    owner.Width = size.Width;
                    owner.Height = size.Height;
                }
            }
            finally
            {
                restoring = false;
            }
        }

        void OnMoved(object? sender, PixelPointEventArgs e) => Restore();
        void OnResized(object? sender, WindowResizedEventArgs e) => Restore();

        owner.PositionChanged += OnMoved;
        owner.Resized += OnResized;
        child.Opened += (_, _) => Restore();
        child.Closed += (_, _) =>
        {
            owner.PositionChanged -= OnMoved;
            owner.Resized -= OnResized;
            Restore();
        };
    }

    /// <summary>자식 창의 기본 크기가 소유 창보다 크면 소유 창 안에 들어가게 줄인다(화면 경계 보정이 소유 창을 미는 일을 줄임).</summary>
    public static void FitInside(Window owner, Window child)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(child);
        var size = owner.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        child.Width = Math.Min(child.Width, Math.Max(child.MinWidth, size.Width - 40));
        child.Height = Math.Min(child.Height, Math.Max(child.MinHeight, size.Height - 40));
    }
}
