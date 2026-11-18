using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Ai.Settings;

namespace Oranda.App.Features.ChangeBundle.Ai;

/// <summary>셸에서 검토를 만든다: 서비스는 DI, 보내기 전 확인은 지금 위에 뜬 창(실행 창·주 창) 위의 작은 확인 창.</summary>
internal static class DdlReviewShell
{
    public static DdlReviewViewModel Create(IServiceProvider services, Func<DdlReviewInput?> input) =>
        new(input, DdlReviewServices.From(services), ConfirmAsync);

    /// <summary>받는 곳·보낼 칸을 보이고 묻는다. 띄울 창이 없으면 보내지 않는다.</summary>
    public static async Task<bool> ConfirmAsync(AiStripRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return TopWindow() is { } owner
            && await AiConfirmDialog.ShowAsync(owner, DdlReviewViewModel.ConfirmMessage(request), "보내기", "취소").ConfigureAwait(true);
    }

    private static Window? TopWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;
}
