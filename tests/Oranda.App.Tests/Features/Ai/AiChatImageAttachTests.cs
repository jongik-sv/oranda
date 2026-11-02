using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;
using SkiaSharp;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>대화 모델의 이미지 첨부(img2): 이미지만 보내기, 장수 상한, 취소 복원, 지원하지 않는 프로필 차단, 보낸 줄의 축소 이미지.</summary>
public class AiChatImageAttachTests
{
    internal static byte[] Png(int width = 60, int height = 40, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static AiChatViewModel Started()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — 부서별 주문 수");
        return vm;
    }

    internal static AiSettings ProfileSettings(bool? supportsImages)
    {
        var id = Guid.NewGuid();
        return new AiSettings
        {
            Profiles = [new AiLlmProfile { Id = id, Name = "예제 LLM", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://llm.example:8080", Model = "example-model", SupportsImages = supportsImages }],
            ActiveProfileId = id,
        };
    }

    [Fact]
    public async Task Images_alone_can_be_sent_and_the_input_and_attachments_are_cleared_before_the_event()
    {
        var vm = Started();
        Assert.False(vm.SendCommand.CanExecute(null));

        Assert.Equal(2, await vm.AddImagesAsync([Png(), Png(80, 50)]));
        Assert.True(vm.SendCommand.CanExecute(null)); // 글 없이 이미지만으로도 보낼 수 있다
        Assert.Equal("이미지 2장 · 최대 5장", vm.FooterText);

        ChatFollowUp? sent = null;
        var draftAtEvent = "unset";
        var attachmentsAtEvent = -1;
        vm.FollowUpRequested += (_, f) => { sent = f; draftAtEvent = vm.Draft; attachmentsAtEvent = vm.Attachments.Count; };
        vm.SendCommand.Execute(null);

        Assert.Equal(2, sent!.Images.Count);
        Assert.Equal(string.Empty, sent.Text);
        Assert.Equal(string.Empty, draftAtEvent); // 비우는 동작은 이벤트 앞에서 일어난다
        Assert.Equal(0, attachmentsAtEvent);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task A_sixth_image_is_refused_with_a_notice()
    {
        var vm = Started();
        Assert.Equal(5, await vm.AddImagesAsync(Enumerable.Range(0, 5).Select(_ => Png())));

        Assert.Equal(0, await vm.AddImagesAsync([Png()]));

        Assert.Equal(5, vm.Attachments.Count);
        Assert.Contains("5장", vm.AttachNotice, StringComparison.Ordinal);
        Assert.False(vm.CanAttach);
    }

    [Fact]
    public Task Concurrent_adds_never_exceed_five_images() => HeadlessHost.Run(async () =>
    {
        var vm = Started(); // 화면 스레드에서 돈다: 운영처럼 이어지는 코드가 한 스레드에서 직렬로 실행된다

        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => vm.AddImagesAsync(Enumerable.Range(0, 3).Select(_ => Png()))));

        Assert.Equal(5, vm.Attachments.Count);
        Assert.Equal(5, counts.Sum());
    });

    [Fact]
    public async Task Restoring_more_images_than_fit_keeps_what_fits_and_says_so()
    {
        var vm = Started();
        await vm.AddImagesAsync(Enumerable.Range(0, 5).Select(_ => Png()));
        ChatFollowUp? sent = null;
        vm.FollowUpRequested += (_, f) => sent = f;
        vm.SendCommand.Execute(null);
        await vm.AddImagesAsync([Png(), Png()]); // 취소하는 사이 새로 붙임

        vm.RestoreFollowUp(sent!);

        Assert.Equal(5, vm.Attachments.Count);
        Assert.True(vm.HasAttachNotice);
    }

    [Fact]
    public async Task A_broken_image_is_refused_but_the_good_ones_are_kept()
    {
        var vm = Started();

        Assert.Equal(1, await vm.AddImagesAsync(["not an image"u8.ToArray(), Png()]));

        Assert.Single(vm.Attachments);
        Assert.True(vm.HasAttachNotice);
    }

    [Fact]
    public async Task Restoring_a_cancelled_follow_up_brings_back_text_and_images_without_overwriting_new_text()
    {
        var vm = Started();
        await vm.AddImagesAsync([Png(), Png()]);
        vm.Draft = "두 계획을 비교해 주세요";
        ChatFollowUp? sent = null;
        vm.FollowUpRequested += (_, f) => sent = f;
        vm.SendCommand.Execute(null);
        Assert.Equal(string.Empty, vm.Draft);

        vm.RestoreFollowUp(sent!);

        Assert.Equal("두 계획을 비교해 주세요", vm.Draft);
        Assert.Equal(sent!.Images, vm.Attachments);

        vm.SendCommand.Execute(null); // 다시 보냈다가 취소하는 사이 사용자가 새 글을 썼다
        vm.Draft = "새로 쓴 글";
        vm.RestoreFollowUp(sent!);
        Assert.Equal("두 계획을 비교해 주세요" + Environment.NewLine + "새로 쓴 글", vm.Draft); // 어느 쪽 글도 잃지 않는다
        Assert.Equal(2, vm.Attachments.Count);
    }

    [Fact]
    public async Task A_profile_without_image_support_closes_the_entrances_and_blocks_sending_attached_images()
    {
        var vm = Started();
        var settings = ProfileSettings(supportsImages: true);
        vm.SettingsProvider = () => settings;
        await vm.AddImagesAsync([Png()]);
        vm.Draft = "계획 비교";
        Assert.True(vm.SendCommand.CanExecute(null));

        settings = ProfileSettings(supportsImages: false); // 첨부한 뒤 지원하지 않는 프로필로 바뀜
        vm.RefreshActiveModel();

        Assert.False(vm.ImagesAllowed);
        Assert.Equal(AiChatViewModel.ImagesUnsupportedText, vm.ImagesBlockedReason);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.Equal(0, await vm.AddImagesAsync([Png()]));
        Assert.Equal(AiChatViewModel.ImagesUnsupportedText, vm.AttachNotice);

        vm.Attachments.Clear(); // 이미지를 빼면 글은 보낼 수 있다
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public void Unknown_support_and_claude_code_leave_the_entrances_open()
    {
        var vm = Started();
        vm.SettingsProvider = () => ProfileSettings(supportsImages: null);
        Assert.True(vm.ImagesAllowed);
        Assert.Equal("예제 LLM", vm.ModelLabel);

        vm.SettingsProvider = () => new AiSettings();
        Assert.True(vm.ImagesAllowed);
        Assert.Equal("Claude Code", vm.ModelLabel);
    }

    [Fact]
    public async Task The_sent_line_keeps_the_images_next_to_the_text()
    {
        var vm = Started();
        await vm.AddImagesAsync([Png()]);
        ChatFollowUp? sent = null;
        vm.FollowUpRequested += (_, f) => sent = f;
        vm.Draft = "비교";
        vm.SendCommand.Execute(null);

        vm.BeginTurn(sent!.Text, sent.Images);

        var entry = Assert.IsType<ChatUserEntry>(vm.Entries[^1]);
        Assert.True(entry.HasImages);
        Assert.Equal("비교", entry.Text);
        Assert.Equal(sent.Images[0].Attachment.Data, entry.Images[0].Attachment.Data);
    }
}
