using System.Runtime.CompilerServices;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Settings;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 이미지 이어 말하기의 흐름(flow1·flow2): 이미지·기대값 전달, 가리는 대화의 새 이미지 경고(재전송·글만은 무경고, 취소는 복원),
/// 보낼 곳이 바뀌면 이어 말하기 중단, 감사 기록.
/// </summary>
public class AiTuningFlowImageTests
{
    private const string Statement = "SELECT * FROM tb_order WHERE stat_cd = '10'";

    private static readonly AiContext Context = new("DEV-ERP", ConnectionEnvironment.Production, AiAccessLevel.Query);

    private sealed class FakeEditor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => new(Statement, new TextRange(0, Statement.Length), StatementKind.Query);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return new AiFinal("답", null, "sess-" + Requests.Count);
            await Task.Yield();
        }
    }

    /// <summary>값을 가리지 않고 그대로 돌려주는 가림 구현(가리는 대화의 사전만 만든다).</summary>
    private sealed class PassThroughMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) => SqlMaskResult.Ok(sql);

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    private static AiSettings Profile(string url = "http://llm.example:8080", string model = "example-model", AiApiFormat format = AiApiFormat.OpenAi, Guid? id = null)
    {
        var profileId = id ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new AiSettings
        {
            Profiles = [new AiLlmProfile { Id = profileId, Name = "예제 LLM", Preset = AiProfilePreset.Custom, Format = format, BaseUrl = url, Model = model }],
            ActiveProfileId = profileId,
        };
    }

    private sealed class Harness
    {
        public RecordingDriver Driver { get; } = new();

        public AiChatViewModel Chat { get; } = new();

        public AiSettings Settings { get; set; } = Profile();

        public bool Mask { get; set; } = true;

        public bool Confirm { get; set; } = true;

        public List<AiImageSendNotice> Confirmed { get; } = [];

        public List<AiImageSendNotice> Audited { get; } = [];

        public AiTuningFlow Flow { get; private set; } = null!;

        public Func<AiImageSendNotice, CancellationToken, Task>? AuditOverride { get; set; }

        public Func<AiContext, CancellationToken, Task<AiContext?>>? Refresh { get; set; }

        /// <summary>확인 창이 떠 있는 동안 일어나는 일(설정 변경·새 시작 시도).</summary>
        public Func<Task>? OnConfirm { get; set; }

        public static async Task<Harness> StartAsync(bool withConfirm = true, bool vault = true, bool mask = true)
        {
            var h = new Harness { Mask = mask };
            h.Flow = new AiTuningFlow(
                new FakeEditor(), h.Driver, h.Chat, (_, _, _) => { },
                masker: vault ? new PassThroughMasker() : null,
                maskRequired: _ => h.Mask,
                refreshContext: (context, token) => h.Refresh is { } refresh ? refresh(context, token) : Task.FromResult<AiContext?>(context),
                settings: () => h.Settings,
                confirmImages: withConfirm
                    ? async (notice, _) =>
                    {
                        h.Confirmed.Add(notice);
                        if (h.OnConfirm is { } during)
                        {
                            await during();
                        }

                        return h.Confirm;
                    }
                    : null,
                auditImages: (notice, token) =>
                {
                    if (h.AuditOverride is { } audit)
                    {
                        return audit(notice, token);
                    }

                    h.Audited.Add(notice);
                    return Task.CompletedTask;
                });
            await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Context);
            return h;
        }

        /// <summary>대화 창에서 이미지를 붙이고 보내기를 누른 것과 같다(입력칸·첨부는 보낸 뒤 비워진다).</summary>
        public async Task<ChatFollowUp> SendAsync(string text, int images)
        {
            await Chat.AddImagesAsync(Enumerable.Range(0, images).Select(i => AiChatImageAttachTests.Png(60 + i, 40)));
            ChatFollowUp? followUp = null;
            Chat.FollowUpRequested += (_, f) => followUp = f;
            Chat.Draft = text;
            Chat.SendCommand.Execute(null);
            return followUp!;
        }
    }

    [Fact]
    public async Task Every_request_carries_the_expected_profile_and_new_images_are_passed_to_the_driver()
    {
        var h = await Harness.StartAsync();
        var first = h.Driver.Requests.Single();
        Assert.Equal(new AiProfileExpectation(Guid.Parse("11111111-1111-1111-1111-111111111111"), "http://llm.example:8080", AiApiFormat.OpenAi), first.Expected);
        Assert.Null(first.Images);

        var followUp = await h.SendAsync("두 계획을 비교", 2);
        await h.Flow.FollowUpAsync(followUp);

        var second = h.Driver.Requests[1];
        Assert.Equal(first.Expected, second.Expected);
        Assert.Equal(2, second.Images!.Count);
        Assert.All(second.Images, i => Assert.Equal("image/png", i.MediaType));
        Assert.Equal("sess-1", second.ResumeId);
        var line = h.Chat.Entries.OfType<ChatUserEntry>().Last();
        Assert.Equal(2, line.Images.Count); // 보낸 줄에 축소 이미지
    }

    [Fact]
    public async Task The_send_button_reaches_the_driver_with_the_images_through_the_attached_flow()
    {
        var h = await Harness.StartAsync();
        h.Flow.Attach(); // 실제 배선: 대화 창의 보내기 → 흐름
        await h.Chat.AddImagesAsync([AiChatImageAttachTests.Png(), AiChatImageAttachTests.Png(70, 40)]);
        h.Chat.Draft = "비교";

        h.Chat.SendCommand.Execute(null);
        for (var i = 0; i < 200 && h.Driver.Requests.Count < 2; i++)
        {
            await Task.Delay(25);
        }

        var request = h.Driver.Requests[^1];
        Assert.Equal(2, request.Images!.Count);
        Assert.StartsWith("비교", request.Prompt, StringComparison.Ordinal);
        Assert.Single(h.Confirmed);
    }

    [Fact]
    public async Task An_exception_while_preparing_restores_the_input_and_is_rethrown()
    {
        var h = await Harness.StartAsync();
        h.Refresh = (_, _) => throw new InvalidOperationException("접속 저장소 오류");
        var followUp = await h.SendAsync("비교", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Flow.FollowUpAsync(followUp));

        Assert.Equal("비교", h.Chat.Draft);
        Assert.Single(h.Chat.Attachments);
        Assert.Single(h.Driver.Requests);
    }

    [Fact]
    public async Task A_timed_out_or_failing_audit_does_not_block_the_send()
    {
        var h = await Harness.StartAsync();
        h.AuditOverride = (_, _) => throw new OperationCanceledException(); // 저장소가 시간 제한 토큰에 취소됨(호출 쪽 취소가 아님)
        var followUp = await h.SendAsync("비교", 1);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Equal(2, h.Driver.Requests.Count);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("감사 기록", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_changed_while_the_confirmation_is_open_stop_the_send()
    {
        var h = await Harness.StartAsync();
        h.OnConfirm = () => { h.Settings = Profile(url: "http://llm2.example:8080"); return Task.CompletedTask; }; // 확인하는 사이 보낼 곳이 바뀜
        var followUp = await h.SendAsync("비교", 1);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Single(h.Driver.Requests); // 확인한 곳과 다른 곳으로 나가지 않는다
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.ProfileChangedText);
        Assert.Equal("비교", h.Chat.Draft);
        Assert.Single(h.Chat.Attachments);
    }

    [Fact]
    public async Task A_new_conversation_cannot_start_while_a_follow_up_is_waiting_for_its_confirmation()
    {
        var h = await Harness.StartAsync();
        var generation = h.Flow.Generation;
        h.OnConfirm = () => h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Context);
        var followUp = await h.SendAsync("비교", 1);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Equal(generation, h.Flow.Generation); // 시작이 거부됨
        Assert.Equal(2, h.Driver.Requests.Count); // 이미지 이어 말하기만 추가(새 시작 없음)
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("튜닝이 진행 중", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_shown_recipient_never_contains_credentials_from_the_address()
    {
        var h = await Harness.StartAsync();
        h.Settings = Profile(url: "http://user:secret@llm.example:8080/?key=abc");
        var target = AiConversationTarget.From(h.Settings);

        Assert.Null(target.RecipientUrl); // 읽을 수 없는 주소는 보이지 않는다(라우터도 거부한다)
        Assert.Equal("http://llm.example:8080/v1", AiConversationTarget.From(Profile(url: "http://llm.example:8080")).RecipientUrl);
    }

    [Fact]
    public async Task Claude_code_conversations_expect_no_profile()
    {
        var driver = new RecordingDriver();
        var flow = new AiTuningFlow(new FakeEditor(), driver, new AiChatViewModel(), (_, _, _) => { }, settings: () => new AiSettings());

        await flow.StartAsync(new TuningStartChoice(false, false, "default"), Context);

        Assert.Equal(new AiProfileExpectation(null), driver.Requests.Single().Expected);
    }

    [Fact]
    public async Task A_new_image_in_a_masking_conversation_asks_first_with_the_recipient_count_and_size_and_is_audited()
    {
        var h = await Harness.StartAsync();
        var followUp = await h.SendAsync("비교", 2);

        await h.Flow.FollowUpAsync(followUp);

        var notice = Assert.Single(h.Confirmed);
        Assert.True(notice.Masking);
        Assert.True(notice.IsProduction);
        Assert.Equal("예제 LLM", notice.RecipientName);
        Assert.Equal("http://llm.example:8080/v1", notice.RecipientUrl); // 요청이 실제로 가는 기준 주소(OpenAI 형식은 /v1 을 붙임)
        Assert.Equal(2, notice.Count);
        Assert.Equal(followUp.Images.Sum(i => i.Attachment.Data.Length), notice.TotalBytes);
        Assert.Equal(2, h.Driver.Requests.Count); // 시작 + 이미지 이어 말하기
        Assert.Same(notice, Assert.Single(h.Audited));
    }

    [Fact]
    public async Task Text_only_follow_ups_in_a_masking_conversation_do_not_warn_or_audit()
    {
        var h = await Harness.StartAsync();
        await h.Flow.FollowUpAsync(await h.SendAsync("비교", 1));
        h.Confirmed.Clear();
        h.Audited.Clear();

        await h.Flow.FollowUpAsync(new ChatFollowUp("조금 더 설명해 주세요", [])); // 앞서 보낸 이미지는 같은 곳으로 이어 간다: 다시 묻지 않는다

        Assert.Empty(h.Confirmed);
        Assert.Empty(h.Audited);
        Assert.Null(h.Driver.Requests[^1].Images);
        Assert.Equal(3, h.Driver.Requests.Count);
    }

    [Fact]
    public async Task Cancelling_the_warning_sends_nothing_restores_text_and_images_and_leaves_no_audit()
    {
        var h = await Harness.StartAsync();
        h.Confirm = false;
        var followUp = await h.SendAsync("비교해 주세요", 2);
        Assert.Equal(string.Empty, h.Chat.Draft);
        Assert.Empty(h.Chat.Attachments);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Single(h.Driver.Requests); // 시작 한 번뿐
        Assert.Equal("비교해 주세요", h.Chat.Draft);
        Assert.Equal(2, h.Chat.Attachments.Count);
        Assert.Empty(h.Audited);
        Assert.Single(h.Chat.Entries.OfType<ChatUserEntry>()); // 사용자 줄이 늘지 않았다
    }

    [Fact]
    public async Task Without_masking_a_new_image_is_sent_without_asking_but_is_still_audited()
    {
        var h = await Harness.StartAsync(vault: false, mask: false);
        await h.Flow.FollowUpAsync(await h.SendAsync("비교", 1));

        Assert.Empty(h.Confirmed);
        Assert.False(Assert.Single(h.Audited).Masking);
        Assert.Single(h.Driver.Requests[^1].Images!);
    }

    [Fact]
    public async Task A_masking_conversation_that_cannot_open_the_confirmation_does_not_send_images()
    {
        var h = await Harness.StartAsync(withConfirm: false);
        var followUp = await h.SendAsync("비교", 1);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Single(h.Driver.Requests);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.ImageConfirmMissingText);
        Assert.Single(h.Chat.Attachments);
    }

    [Theory]
    [InlineData("other-url")]
    [InlineData("other-model")]
    [InlineData("other-format")]
    [InlineData("other-profile")]
    [InlineData("claude-code")]
    public async Task Following_up_after_the_target_changed_is_refused_and_the_input_is_restored(string change)
    {
        var h = await Harness.StartAsync();
        h.Settings = change switch
        {
            "other-url" => Profile(url: "http://llm2.example:8080"),
            "other-model" => Profile(model: "model-b"),
            "other-format" => Profile(format: AiApiFormat.Anthropic),
            "other-profile" => Profile(id: Guid.NewGuid()),
            _ => new AiSettings(),
        };
        var followUp = await h.SendAsync("이어서", 1);

        await h.Flow.FollowUpAsync(followUp);

        Assert.Single(h.Driver.Requests);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.ProfileChangedText);
        Assert.Equal("이어서", h.Chat.Draft);
        Assert.Single(h.Chat.Attachments);
        Assert.Empty(h.Confirmed); // 경고 없이 새로운 곳으로 가지 않는다
    }

    [Fact]
    public void Router_and_driver_error_codes_become_texts_that_say_what_to_do()
    {
        Assert.Contains("다시 시작", AiErrorText.Describe(new AiError("x", "profile_changed")), StringComparison.Ordinal);
        Assert.Contains("다시 시작", AiErrorText.Describe(new AiError("x", "resume_unknown")), StringComparison.Ordinal);
        Assert.Contains("한도", AiErrorText.Describe(new AiError(string.Empty, "context_limit")), StringComparison.Ordinal);
        Assert.Contains("한도", AiErrorText.Describe(new AiError("x", "turn_limit")), StringComparison.Ordinal);
        Assert.Equal("서버 오류", AiErrorText.Describe(new AiError("서버 오류", "http_500"))); // 모르는 코드는 드라이버 글 그대로
    }

    [Fact]
    public void The_audit_row_records_count_size_and_recipient_but_no_image_content()
    {
        var notice = new AiImageSendNotice("ERP 운영", ConnectionEnvironment.Production, "예제 LLM", "http://llm.example:8080", 2, 1_887_436, Masking: true, AiAccessLevel.Query);

        var entry = AiTuningLauncher.BuildImageAuditEntry(notice, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(AiTuningLauncher.ImageAuditTool, entry.Tool);
        Assert.Equal("images=2; bytes=1887436; to=예제 LLM · http://llm.example:8080", entry.Arguments);
        Assert.Equal("unmasked_images_confirmed", entry.Reason);
        Assert.Equal(AiAccessLevel.Query, entry.AccessLevel);
        Assert.Equal("ERP 운영", entry.ProfileName);
    }
}
