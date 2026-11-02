using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>대화 화면의 이미지 입구(img3): 붙여 넣기·끌어 놓기·📎 단추·미지원 툴팁, 입력칸 위 축소 이미지와 보낸 줄.</summary>
public class AiChatImageViewTests
{
    private static async Task<(Window Window, AiChatView View, AiChatViewModel Vm)> Open(bool started = true)
    {
        var vm = new AiChatViewModel();
        if (started)
        {
            vm.StartConversation("튜닝 — SELECT o.order_id, o.status…");
        }

        var window = new Window { Width = 440, Height = 820, Content = new AiChatView { DataContext = vm } };
        window.Show();
        await Task.Yield();
        return (window, (AiChatView)window.Content!, vm);
    }

    /// <summary><c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG 로 저장한다(시안 대조용).</summary>
    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            window.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
        }
    }

    private static async Task SetClipboardImage(Window window, byte[] png)
    {
        var item = new global::Avalonia.Input.DataTransferItem();
        item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), png);
        var transfer = new global::Avalonia.Input.DataTransfer();
        transfer.Add(item);
        await window.Clipboard!.SetDataAsync(transfer);
    }

    [Fact]
    public Task Ctrl_V_with_only_an_image_on_the_clipboard_attaches_it_instead_of_being_swallowed_by_the_text_box() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        await SetClipboardImage(window, AiChatImageAttachTests.Png(200, 120));
        view.FindControl<TextBox>("DraftBox")!.Focus();

        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
        await HeadlessHost.WaitUntil(() => vm.Attachments.Count == 1);

        Assert.Equal(string.Empty, vm.Draft);
        Assert.Equal(1, view.FindControl<ItemsControl>("AttachmentList")!.ItemCount);
        window.Close();
    });

    [Fact]
    public Task Ctrl_V_with_text_still_pastes_the_text() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        await window.Clipboard!.SetTextAsync("SELECT 1 FROM dual");
        view.FindControl<TextBox>("DraftBox")!.Focus();

        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
        await HeadlessHost.WaitUntil(() => vm.Draft.Contains("SELECT 1 FROM dual", StringComparison.Ordinal));

        Assert.Empty(vm.Attachments);
        window.Close();
    });

    [Fact]
    public Task A_profile_without_image_support_disables_the_attach_button_and_explains_why() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        var settings = AiChatImageAttachTests.ProfileSettings(supportsImages: false);
        vm.SettingsProvider = () => settings;
        vm.RefreshActiveModel();
        await Task.Yield();

        var button = view.FindControl<Button>("AttachButton")!;
        Assert.False(button.IsEnabled);
        Assert.Equal("예제 LLM", view.FindControl<TextBlock>("ModelLabelText")!.Text);
        Assert.Equal(AiChatViewModel.ImagesUnsupportedText, vm.ImagesBlockedReason);
        Render(window, "chat-unsupported");
        window.Close();
    });

    [Fact]
    public Task Attached_images_show_as_thumbnails_with_a_remove_button_and_a_count_in_the_footer() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        vm.SettingsProvider = () => AiChatImageAttachTests.ProfileSettings(supportsImages: true);
        vm.RefreshActiveModel();
        await vm.AddImagesAsync([AiChatImageAttachTests.Png(264, 168), AiChatImageAttachTests.Png(264, 168, SkiaSharp.SKColors.Orange)]);
        vm.Draft = "두 실행 계획을 비교해 주세요";
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("이미지 2장 · 최대 5장", view.FindControl<TextBlock>("StatusLine")!.Text);
        Assert.True(view.FindControl<ScrollViewer>("AttachmentScroll")!.IsVisible);
        Assert.Equal(2, view.FindControl<ItemsControl>("AttachmentList")!.ItemCount);
        Assert.True(view.FindControl<Button>("AttachButton")!.IsEnabled);
        Assert.NotNull(vm.Attachments[0].Thumbnail);
        Render(window, "chat-attach");

        vm.RemoveAttachmentCommand.Execute(vm.Attachments[0]);
        Assert.Single(vm.Attachments);
        window.Close();
    });

    [Fact]
    public Task The_sent_line_shows_the_images_above_the_text() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        await vm.AddImagesAsync([AiChatImageAttachTests.Png(264, 168), AiChatImageAttachTests.Png(264, 168, SkiaSharp.SKColors.Orange)]);
        vm.BeginTurn("두 실행 계획을 비교해 주세요", [.. vm.Attachments]);
        vm.AddAssistantText("**두 번째 계획이 ORDERS 전체 스캔을 피함**");
        vm.EndTurn();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var sent = view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "SentImages"); // 템플릿 안이라 이름 범위가 다르다
        Assert.NotNull(sent);
        Assert.Equal(2, sent!.ItemCount);
        Render(window, "chat-sent");
        window.Close();
    });

    [Fact]
    public Task Dropping_an_image_file_attaches_it_and_dropping_text_does_not() => HeadlessHost.Run(async () =>
    {
        var (window, view, vm) = await Open();
        var dir = Path.Combine(Path.GetTempPath(), "oranda-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "plan.png");
            await File.WriteAllBytesAsync(path, AiChatImageAttachTests.Png(100, 60));
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            Assert.NotNull(file);
            var item = new global::Avalonia.Input.DataTransferItem();
            item.SetFile(file!);
            var transfer = new global::Avalonia.Input.DataTransfer();
            transfer.Add(item);

            Assert.True(AiImageSources.HasImage(transfer));
            var images = await AiImageSources.ReadDroppedAsync(transfer);
            Assert.Single(images);
            await vm.AddImagesAsync(images);
            Assert.Single(vm.Attachments);

            var textItem = new global::Avalonia.Input.DataTransferItem();
            textItem.SetText("SELECT 1");
            var textTransfer = new global::Avalonia.Input.DataTransfer();
            textTransfer.Add(textItem);
            Assert.Empty(await AiImageSources.ReadDroppedAsync(textTransfer));
            Assert.False(AiImageSources.HasImage(textTransfer)); // 글 끌어 놓기는 입력칸이 그대로 받는다

            // 이미지 파일에 파일 이름 글이 함께 와도 이미지로 본다. 이미지가 아닌 파일만 있으면 이미지가 아니다.
            var named = new global::Avalonia.Input.DataTransferItem();
            named.SetFile(file!);
            named.SetText("plan.png");
            var both = new global::Avalonia.Input.DataTransfer();
            both.Add(named);
            Assert.True(AiImageSources.HasImage(both));

            var notes = Path.Combine(dir, "notes.txt");
            await File.WriteAllTextAsync(notes, "x");
            var textFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(notes));
            var notesItem = new global::Avalonia.Input.DataTransferItem();
            notesItem.SetFile(textFile!);
            var notesTransfer = new global::Avalonia.Input.DataTransfer();
            notesTransfer.Add(notesItem);
            Assert.False(AiImageSources.HasImage(notesTransfer));

            // 읽기 전에 장수를 자른다(상한 + 1 장까지만 읽어 「5장까지」 안내가 나가게 한다)
            var many = new List<Avalonia.Platform.Storage.IStorageItem>();
            for (var i = 0; i < 9; i++)
            {
                var extra = Path.Combine(dir, $"p{i}.png");
                await File.WriteAllBytesAsync(extra, AiChatImageAttachTests.Png(20, 20));
                many.Add((await window.StorageProvider.TryGetFileFromPathAsync(new Uri(extra)))!);
            }

            Assert.Equal(3, (await AiImageSources.ReadFilesAsync(many, max: 2)).Count);
        }
        finally
        {
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    });
}
