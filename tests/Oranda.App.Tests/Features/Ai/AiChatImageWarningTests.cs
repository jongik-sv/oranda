using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>운영 접속 경고 창(시안 ChatWarning): 접속·받는 곳·이미지 장수와 크기를 보이고, 「그대로 보내기」 와 취소가 결과를 낸다.</summary>
public class AiChatImageWarningTests
{
    private static readonly AiImageSendNotice Notice = new("ERP 운영", ConnectionEnvironment.Production, "예제 LLM", "http://llm.example:8080", 2, 1_887_436, Masking: true, AiAccessLevel.Query);

    [Fact]
    public Task The_dialog_shows_connection_recipient_and_images_and_renders_in_both_themes() => HeadlessHost.Run(() =>
    {
        var dialog = new AiImageWarningDialog { DataContext = new AiImageWarningViewModel(Notice) };
        dialog.Show();

        Assert.Equal("ERP 운영", dialog.FindControl<TextBlock>("ConnectionText")!.Text);
        Assert.Equal("예제 LLM · http://llm.example:8080", dialog.FindControl<TextBlock>("RecipientText")!.Text);
        Assert.Equal("2장 · 1.8 MB", dialog.FindControl<TextBlock>("ImagesText")!.Text);
        Assert.True(dialog.FindControl<Border>("EnvBadge")!.IsVisible);
        Assert.Equal("그대로 보내기", dialog.FindControl<Button>("ConfirmButton")!.Content);

        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                dialog.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
                bmp.Render(dialog);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"chat-warning-{tone}.png"));
#pragma warning restore CS0618
            }
        }

        dialog.Close();
    });

    [Fact]
    public void Confirm_closes_with_true_and_cancel_with_false_and_a_development_connection_has_no_production_badge()
    {
        var model = new AiImageWarningViewModel(Notice with { Environment = ConnectionEnvironment.Development });
        var results = new List<bool>();
        model.CloseRequested += (_, confirmed) => results.Add(confirmed);

        model.ConfirmCommand.Execute(null);
        model.CancelCommand.Execute(null);

        Assert.Equal([true, false], results);
        Assert.False(model.Notice.IsProduction);
        Assert.Equal("개발", model.Notice.EnvironmentLabel);
    }
}
