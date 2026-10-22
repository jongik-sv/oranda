using Avalonia;
using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>TSK-13-24 접속 관리자를 여는 동안 소유 창(메인 창)의 위치·크기가 바뀌지 않는다.</summary>
public sealed class OwnerPlacementGuardTests
{
    [Fact]
    public Task Owner_position_and_size_are_restored_while_the_child_is_open() => HeadlessHost.Run(async () =>
    {
        var owner = new Window { Width = 800, Height = 600, Position = new PixelPoint(40, 50) };
        owner.Show();
        var child = new Window { Width = 960, Height = 640 };
        var position = owner.Position;
        var size = owner.ClientSize;

        OwnerPlacementGuard.Hold(owner, child);
        child.Show(owner);

        owner.Position = new PixelPoint(position.X + 30, position.Y + 300); // 화면 경계 보정이 소유 창을 민 상황
        owner.Width = 700;
        await HeadlessHost.WaitUntil(() => owner.Position == position && owner.ClientSize == size);

        child.Close();
        owner.Position = new PixelPoint(0, 0);
        Assert.Equal(new PixelPoint(0, 0), owner.Position); // 자식이 닫힌 뒤에는 더 이상 붙들지 않는다
        owner.Close();
    });

    [Fact]
    public Task Child_is_fitted_inside_a_smaller_owner() => HeadlessHost.Run(() =>
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();
        var child = new Window { Width = 960, Height = 640, MinWidth = 640, MinHeight = 420 };

        OwnerPlacementGuard.FitInside(owner, child);

        Assert.True(child.Width <= owner.ClientSize.Width);
        Assert.True(child.Height <= Math.Max(child.MinHeight, owner.ClientSize.Height));
        owner.Close();
        return Task.CompletedTask;
    });
}
