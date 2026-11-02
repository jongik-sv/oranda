using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Text;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 일반 대화(튜닝 없이 보내는 글): 입력창이 처음부터 활성, 전송·이어 말하기, 접속 없음·허용 수준 끔, 드라이버 없음, 실행 중 잠금,
/// 일반 대화 중 튜닝 시작·새 대화, 보낼 곳이 바뀔 때, 구조 결과에 칸이 빠졌을 때, 시작 경로 예외 표시.
/// </summary>
public class AiGeneralChatTests
{
    private static readonly AiContext Connection = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);

    private static AiSettings Profile(string url = "http://llm.example:8080", string model = "example-model", Guid? id = null)
    {
        var profileId = id ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new AiSettings
        {
            Profiles = [new AiLlmProfile { Id = profileId, Name = "예제 LLM", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = url, Model = model }],
            ActiveProfileId = profileId,
        };
    }

    private sealed class FakeEditor : IEditorContext
    {
        public const string DefaultSql = "SELECT * FROM tb_order WHERE stat_cd = '10'";

        /// <summary>활성 탭이 있다(SQL 탭).</summary>
        public bool HasDocument { get; set; } = true;

        /// <summary>커서 위치의 문장 글. null 이면 문장 없음.</summary>
        public string? Statement { get; set; } = DefaultSql;

        /// <summary>활성 탭이 묶인 접속 id. null 이면 접속 없는 탭.</summary>
        public Guid? TabConnectionId { get; set; }

        public EditorSnapshot? GetActiveDocument() => HasDocument ? new EditorSnapshot(Guid.NewGuid(), Statement ?? string.Empty, new TextRange(0, 0), 0) { ConnectionProfileId = TabConnectionId } : null;

        public SqlStatement? GetCurrentStatement() => HasDocument && Statement is { } sql ? new(sql, new TextRange(0, sql.Length), StatementKind.Query) : null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        /// <summary>null 이 아니면 이 일이 끝날 때까지 첫 사건을 미룬다(실행 중 상태를 보려고).</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>마지막 사건의 구조 결과(튜닝 시험용).</summary>
        public JsonElement? Result { get; set; }

        /// <summary>요청을 받았을 때 실행하는 일(실제 게이트로 도구를 불러 본다).</summary>
        public Func<AiRequest, Task>? OnRun { get; set; }

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            if (OnRun is { } onRun)
            {
                await onRun(request);
            }

            if (Gate is { } gate)
            {
                await gate.Task;
            }

            yield return new AiFinal("답 " + Requests.Count, Result, "sess-" + Requests.Count);
        }
    }

    private sealed class PassThroughMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new();

        /// <summary>「'10'」 을 자리표시로 바꾸는 가림(값이 프롬프트에 새지 않는지 보려고).</summary>
        public SqlMaskResult Mask(string sql, LiteralVault vault) => SqlMaskResult.Ok(sql.Replace("'10'", ":v1", StringComparison.Ordinal));

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    /// <summary>시험이 나중에 마스커를 바꿀 수 있게 감싼 마스커.</summary>
    private sealed class LateMasker(Func<ISqlLiteralMasker> inner) : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => inner().CreateVault(statementText);

        public SqlMaskResult Mask(string sql, LiteralVault vault) => inner().Mask(sql, vault);

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => inner().Restore(sql, vault);

        public string MaskPredicate(string predicate, LiteralVault vault) => inner().MaskPredicate(predicate, vault);
    }

    private sealed class FailingMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) => SqlMaskResult.Failed;

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    /// <summary>문장에 진짜 :v1 바인드가 있으면 접두를 바꿔 사전을 만든다(실제 마스커와 같다).</summary>
    private sealed class PrefixAwareMasker : ISqlLiteralMasker
    {
        public LiteralVault CreateVault(string statementText) => new(statementText.Contains(":v1", StringComparison.Ordinal) ? "lit" : "v");

        public SqlMaskResult Mask(string sql, LiteralVault vault) => SqlMaskResult.Ok(sql);

        public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

        public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
    }

    private sealed class Harness
    {
        public RecordingDriver Driver { get; } = new();

        public AiChatViewModel Chat { get; } = new();

        public AiSettings Settings { get; set; } = Profile();

        /// <summary>일반 대화의 접속. null 이면 연결 없음.</summary>
        public AiContext? Connected { get; set; } = Connection;

        public bool Mask { get; set; }

        public Guid? ConnectionId { get; set; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public FakeEditor Editor { get; } = new();

        /// <summary>활성 문서가 SQL 작업 공간 문서인가(null = 모름).</summary>
        public bool? SqlDocumentActive { get; set; } = true;

        /// <summary>마스커를 바꿔 끼운다(기본은 PassThroughMasker).</summary>
        public ISqlLiteralMasker? MaskerOverride { get; set; }

        public bool Confirm { get; set; } = true;

        public bool ConfirmTextAnswer { get; set; } = true;

        public List<AiImageSendNotice> TextConfirmed { get; } = [];

        /// <summary>색인 확인(읽기) 중에 일어나는 일.</summary>
        public Action? OnReadIndexes { get; set; }

        public List<AiImageSendNotice> Confirmed { get; } = [];

        public List<AiContext> SessionContexts { get; } = [];

        public Func<Task>? OnConfirm { get; set; }

        public AiTuningFlow Flow { get; }

        public Harness(bool withMasker = false)
        {
            AgentSession? Make(AiContext context, LiteralVault? vault)
            {
                SessionContexts.Add(context);
                return new AgentSession(new AgentRunScope("run-" + SessionContexts.Count, new ConnectionProfile(context.ConnectionName ?? "x", "u", "h/s"), context) { Literals = vault });
            }

            Flow = new AiTuningFlow(
                Editor, Driver, Chat, (_, _, _) => { },
                sessionFactory: Make,
                generalSessionFactory: Make,
                refreshContext: (context, _) => Task.FromResult<AiContext?>(context),
                masker: withMasker ? new LateMasker(() => MaskerOverride ?? new PassThroughMasker()) : null,
                maskRequired: _ => Mask,
                settings: () => Settings,
                confirmImages: async (notice, _) =>
                {
                    Confirmed.Add(notice);
                    if (OnConfirm is { } during)
                    {
                        await during();
                    }

                    return Confirm;
                },
                auditImages: (_, _) => Task.CompletedTask,
                readIndexes: (_, _, _) =>
                {
                    OnReadIndexes?.Invoke();
                    return Task.FromResult<IReadOnlyList<IndexColumnsRow>?>(null);
                },
                generalContext: _ => Task.FromResult(new AiGeneralScope(Connected, Mask, ConnectionId, SqlDocumentActive)),
                confirmText: (notice, _) =>
                {
                    TextConfirmed.Add(notice);
                    return Task.FromResult(ConfirmTextAnswer);
                });
            Chat.SettingsProvider = () => Settings;
            Flow.Attach(); // 실제 배선: 대화 창의 보내기 → 흐름
        }

        /// <summary>대화 창에서 글(과 이미지)을 쓰고 보내기를 누른다. 보낸 차례가 끝나기까지 기다린다.</summary>
        public async Task SendAsync(string text, int images = 0)
        {
            await Chat.AddImagesAsync(Enumerable.Range(0, images).Select(i => AiChatImageAttachTests.Png(60 + i, 40)));
            Chat.Draft = text;
            Chat.SendCommand.Execute(null);
            while (Chat.IsRunning || Flow.IsRunning)
            {
                await Task.Delay(5);
            }

            await Task.Delay(20); // 이벤트 처리기(async void)가 마지막 줄까지 쓰도록
        }
    }

    [Fact]
    public void The_input_is_active_from_the_start_without_a_tuning_conversation()
    {
        var vm = new AiChatViewModel();

        Assert.False(vm.IsTuning);
        Assert.True(vm.CanType);
        Assert.True(vm.ShowStartHint);

        vm.Draft = "NVL 과 COALESCE 차이가 뭐야?";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public Task The_view_enables_the_input_and_shows_the_general_hint_on_open() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
        window.Show();
        var view = (AiChatView)window.Content!;
        var hint = view.FindControl<TextBlock>("StartHint")!;

        Assert.True(view.FindControl<TextBox>("DraftBox")!.IsEnabled);
        Assert.True(hint.IsVisible);
        Assert.Contains("물어보세요", hint.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("시작하면 여기서", hint.Text, StringComparison.Ordinal); // 튜닝을 시작하라는 옛 안내가 아니다
        Assert.False(view.FindControl<TextBlock>("UnavailableHint")!.IsVisible);

        vm.AddAssistantText("안녕하세요");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(hint.IsVisible); // 줄이 생기면 안내는 사라진다
        window.Close();
    });

    [Fact]
    public async Task A_general_message_reaches_the_driver_without_any_tuning_parts()
    {
        var h = new Harness();

        await h.SendAsync("NVL 과 COALESCE 차이가 뭐야?");

        var request = Assert.Single(h.Driver.Requests);
        Assert.Contains("NVL 과 COALESCE 차이가 뭐야?", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Tune this", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Index rules", request.Prompt, StringComparison.Ordinal);
        Assert.Null(request.OutputSchema); // 튜닝 구조 결과를 요구하지 않는다
        Assert.Null(request.ResumeId);
        Assert.Equal(new AiProfileExpectation(Guid.Parse("11111111-1111-1111-1111-111111111111"), "http://llm.example:8080", AiApiFormat.OpenAi), request.Expected); // 튜닝과 같은 프로필 선택
        Assert.Null(h.Flow.LastResult);
        Assert.Null(h.Chat.ReportSource); // 레포트 재료도 없다
        Assert.False(h.Chat.IsTuning);
        Assert.Equal(["NVL 과 COALESCE 차이가 뭐야?"], h.Chat.Entries.OfType<ChatUserEntry>().Select(e => e.Text));
        Assert.Equal("답 1", Assert.Single(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith('답')).Text);
        Assert.Equal("sess-1", h.Chat.ResumeId);
    }

    [Fact]
    public async Task A_follow_up_resumes_the_same_general_conversation()
    {
        var h = new Harness();
        await h.SendAsync("첫 질문");

        await h.SendAsync("이어서 하나 더");

        Assert.Equal(2, h.Driver.Requests.Count);
        Assert.Equal("sess-1", h.Driver.Requests[1].ResumeId);
        Assert.Equal(h.Driver.Requests[0].Expected, h.Driver.Requests[1].Expected);
        Assert.Equal(2, h.Chat.Entries.OfType<ChatAssistantEntry>().Count(e => e.Text.StartsWith('답')));
    }

    [Fact]
    public async Task An_active_connection_gives_db_tools_within_its_access_level()
    {
        var h = new Harness { Connected = Connection };

        await h.SendAsync("TB_ORDER 컬럼이 뭐야?");

        var session = h.Driver.Requests.Single().Session!;
        Assert.Equal(AiAccessLevel.Query, session.Context.AccessLevel); // 기존 허용 수준 그대로(새 권한 없음)
        Assert.Equal("DEV-ERP", session.Context.ConnectionName);
        Assert.Contains("Oranda tools", h.Driver.Requests.Single().Prompt, StringComparison.Ordinal);
        Assert.True(session.IsRevoked); // 끝나면 폐기
    }

    [Theory]
    [InlineData(false)] // 연결 없음
    [InlineData(true)] // 연결은 있으나 이 접속의 AI 허용 수준이 끔
    public async Task Without_a_connection_or_with_access_off_the_answer_comes_without_db_tools(bool connectedButOff)
    {
        var h = new Harness { Connected = connectedButOff ? Connection with { AccessLevel = AiAccessLevel.Off } : null };

        await h.SendAsync("ROWNUM 이 뭐야?");

        var request = Assert.Single(h.Driver.Requests);
        Assert.Equal(AiAccessLevel.Off, request.Session!.Level); // 게이트가 도구 목록을 비우고 모든 호출을 거부한다
        Assert.Empty(h.SessionContexts); // 도구 실행 범위를 만들지 않았다
        Assert.Contains("No database tools are available", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("답 1", Assert.Single(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith('답')).Text);
    }

    [Fact]
    public async Task A_masking_connection_without_a_masker_answers_without_tools_and_says_so()
    {
        var h = new Harness(withMasker: false) { Mask = true };

        await h.SendAsync("질문");

        Assert.Equal(AiAccessLevel.Off, h.Driver.Requests.Single().Session!.Level);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralMaskerMissingText);
    }

    [Fact]
    public async Task A_masking_connection_keeps_one_vault_for_the_conversation_and_confirms_new_images()
    {
        var h = new Harness(withMasker: true) { Mask = true };

        await h.SendAsync("이 화면 봐 줘", images: 1);
        await h.SendAsync("글만 이어서");

        var first = h.Driver.Requests[0];
        Assert.Single(first.Images!);
        var notice = Assert.Single(h.Confirmed); // 가리는 대화의 새 이미지만 확인(글만은 무경고)
        Assert.True(notice.Masking);
        Assert.StartsWith("예제 LLM", notice.Recipient, StringComparison.Ordinal);
        Assert.Contains("placeholders", first.Prompt, StringComparison.Ordinal);
        Assert.Equal(2, h.SessionContexts.Count);
    }

    [Fact]
    public async Task Cancelling_the_image_warning_sends_nothing_and_restores_the_draft()
    {
        var h = new Harness(withMasker: true) { Mask = true, Confirm = false };

        await h.SendAsync("이 화면 봐 줘", images: 1);

        Assert.Empty(h.Driver.Requests);
        Assert.Equal("이 화면 봐 줘", h.Chat.Draft);
        Assert.Single(h.Chat.Attachments);
        Assert.Empty(h.Chat.Entries.OfType<ChatUserEntry>());
    }

    [Fact]
    public async Task The_input_locks_while_a_turn_runs_and_reopens_after_it()
    {
        var h = new Harness();
        h.Driver.Gate = new TaskCompletionSource();
        h.Chat.Draft = "느린 질문";
        h.Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(h.Chat.IsRunning);
        Assert.False(h.Chat.CanType); // 실행 중에는 입력 잠금
        Assert.False(h.Chat.CanAttach);
        h.Chat.Draft = "겹쳐 보내기";
        Assert.False(h.Chat.SendCommand.CanExecute(null));
        Assert.True(h.Chat.StopCommand.CanExecute(null));

        h.Driver.Gate.SetResult();
        while (h.Chat.IsRunning)
        {
            await Task.Delay(5);
        }

        Assert.True(h.Chat.CanType);
        Assert.True(h.Chat.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_second_message_while_the_first_waits_for_the_image_confirmation_is_refused_and_kept()
    {
        var h = new Harness(withMasker: true) { Mask = true };
        var release = new TaskCompletionSource();
        h.OnConfirm = () => release.Task;
        await h.Chat.AddImagesAsync([AiChatImageAttachTests.Png()]);
        h.Chat.Draft = "첫 글";
        h.Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        // 확인 창이 떠 있는 동안: 튜닝 시작도, 겹쳐 보내기도 막는다.
        await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Connection);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("진행 중", StringComparison.Ordinal));
        Assert.False(h.Chat.IsTuning);
        await h.Flow.FollowUpAsync(new ChatFollowUp("둘째 글", []));
        Assert.Equal("둘째 글", h.Chat.Draft); // 잃지 않고 입력칸에 돌려 둔다
        Assert.Empty(h.Driver.Requests);

        release.SetResult();
        while (h.Driver.Requests.Count == 0 || h.Chat.IsRunning)
        {
            await Task.Delay(5);
        }

        Assert.Single(h.Driver.Requests);
    }

    [Fact]
    public async Task Starting_a_tuning_in_a_general_conversation_replaces_it_with_the_tuning_conversation()
    {
        var h = new Harness();
        await h.SendAsync("일반 질문");
        Assert.False(h.Chat.IsTuning);

        await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.True(h.Chat.IsTuning);
        Assert.StartsWith("튜닝 — ", h.Chat.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Chat.Entries.OfType<ChatUserEntry>(), e => e.Text == "일반 질문"); // 새 대화
        var tuning = h.Driver.Requests[1];
        Assert.Null(tuning.ResumeId); // 일반 대화의 세션을 이어받지 않는다
        Assert.NotNull(tuning.OutputSchema);
        Assert.Contains("Tune this Oracle SQL statement", tuning.Prompt, StringComparison.Ordinal);

        // 튜닝 대화의 이어 말하기는 튜닝 규칙을 쓴다(구조 결과를 요구).
        await h.SendAsync("힌트로만");
        var followUp = h.Driver.Requests[2];
        Assert.Equal("sess-2", followUp.ResumeId);
        Assert.NotNull(followUp.OutputSchema);
    }

    [Fact]
    public async Task New_conversation_returns_to_a_general_conversation_without_the_old_session()
    {
        var h = new Harness();
        await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });
        Assert.True(h.Chat.NewConversationCommand.CanExecute(null));

        h.Chat.NewConversationCommand.Execute(null);

        Assert.False(h.Chat.IsTuning);
        Assert.Empty(h.Chat.Entries);
        Assert.Null(h.Chat.ResumeId);
        Assert.False(h.Chat.NewConversationCommand.CanExecute(null)); // 비어 있으면 할 일이 없다

        await h.SendAsync("이제 일반 질문");
        var request = h.Driver.Requests[^1];
        Assert.Null(request.OutputSchema);
        Assert.Null(request.ResumeId);
    }

    [Fact]
    public async Task New_conversation_is_off_while_a_turn_runs()
    {
        var h = new Harness();
        h.Driver.Gate = new TaskCompletionSource();
        h.Chat.Draft = "느린 질문";
        h.Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(h.Chat.NewConversationCommand.CanExecute(null));

        h.Driver.Gate.SetResult();
        while (h.Chat.IsRunning)
        {
            await Task.Delay(5);
        }

        Assert.True(h.Chat.NewConversationCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_the_profile_mid_conversation_starts_a_new_conversation_and_does_not_resend_the_old_one()
    {
        var h = new Harness();
        await h.SendAsync("첫 질문");
        h.Settings = Profile(url: "http://other-llm.example:9090", model: "other");

        await h.SendAsync("다른 모델에게");

        var second = h.Driver.Requests[1];
        Assert.Null(second.ResumeId); // 앞 대화를 새 모델로 이어 보내지 않는다
        Assert.Equal("http://other-llm.example:9090", second.Expected!.BaseUrl);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralTargetChangedText);
    }

    [Fact]
    public async Task Changing_the_connection_mid_conversation_starts_a_new_conversation()
    {
        var h = new Harness();
        await h.SendAsync("첫 질문");
        h.Connected = Connection with { ConnectionName = "PRD-ERP" };
        h.ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        await h.SendAsync("다른 접속에서");

        Assert.Null(h.Driver.Requests[1].ResumeId);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralConnectionChangedText);
    }

    [Fact]
    public async Task A_profile_change_while_the_image_confirmation_is_open_sends_nothing_and_the_next_send_starts_fresh()
    {
        var h = new Harness(withMasker: true) { Mask = true };
        h.OnConfirm = () =>
        {
            h.Settings = Profile(url: "http://other-llm.example:9090");
            return Task.CompletedTask;
        };

        await h.SendAsync("이 화면", images: 1);

        Assert.Empty(h.Driver.Requests); // 확인한 곳과 다른 곳으로 나가지 않는다
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralSendCancelledText);
        Assert.Equal("이 화면", h.Chat.Draft);
    }

    [Fact]
    public Task An_unavailable_driver_keeps_the_input_open_and_shows_the_reason() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel { UnavailableProvider = () => "AI 모델이 「선택 안 함」 입니다." };
        var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
        window.Show();
        var view = (AiChatView)window.Content!;

        Assert.True(view.FindControl<TextBox>("DraftBox")!.IsEnabled); // 입력은 받는다
        Assert.True(view.FindControl<TextBlock>("UnavailableHint")!.IsVisible);
        Assert.Contains("선택 안 함", view.FindControl<TextBlock>("UnavailableHint")!.Text, StringComparison.Ordinal);
        Assert.False(view.FindControl<TextBlock>("StartHint")!.IsVisible);
        window.Close();
    });

    private sealed class Services(Dictionary<Type, object> map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => map.GetValueOrDefault(serviceType);
    }

    [Fact]
    public async Task Sending_without_an_ai_driver_service_leaves_the_reason_in_the_chat_and_keeps_the_text()
    {
        var launcher = new AiTuningLauncher(new Services([]));
        var chat = new AiChatViewModel();
        launcher.EnsureChat(chat);

        Assert.True(chat.CanType);
        Assert.NotNull(chat.UnavailableReason);
        chat.Draft = "질문";
        chat.SendCommand.Execute(null);
        await Task.Delay(50);

        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("오류: AI 드라이버를 쓸 수 없습니다", StringComparison.Ordinal));
        Assert.Equal("질문", chat.Draft); // 글을 잃지 않는다
        Assert.False(chat.IsRunning);
    }

    [Fact]
    public void Sending_with_nothing_listening_keeps_the_text_and_says_so()
    {
        var vm = new AiChatViewModel { Draft = "질문" };

        vm.SendCommand.Execute(null);

        Assert.Equal("질문", vm.Draft);
        Assert.Contains("준비되지 않아", Assert.Single(vm.Entries.OfType<ChatAssistantEntry>()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_structured_result_with_missing_fields_does_not_crash_the_flow()
    {
        var h = new Harness();
        h.Driver.Result = JsonDocument.Parse("""{"rewrites":[{"label":null,"sql":"SELECT 1 FROM dual"},{"label":"x"}],"index_suggestions":[{"ddl":"CREATE INDEX i ON t(c)"},{"table":"T"}]}""").RootElement.Clone();

        await h.Flow.StartAsync(new TuningStartChoice(false, true, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" }); // 칸이 빠져도 예외 없이 끝난다

        Assert.True(h.Chat.IsTuning);
        Assert.NotNull(h.Flow.LastResult);
        Assert.Equal(string.Empty, h.Flow.LastResult!.Summary);
        Assert.Single(h.Flow.LastResult.Rewrites); // SQL 없는 후보는 뺀다
        Assert.Single(h.Flow.LastResult.IndexSuggestions); // DDL 없는 후보는 뺀다
        Assert.Equal(string.Empty, h.Flow.LastResult.IndexSuggestions[0].Table);
    }

    [Fact]
    public void Sanitize_fills_every_missing_field_with_an_empty_value()
    {
        var parsed = Oranda.Core.Ai.Schemas.TuningResult.Parse(JsonDocument.Parse("{}").RootElement);

        var clean = AiTuningFlow.Sanitize(parsed);

        Assert.Equal(string.Empty, clean.Summary);
        Assert.Empty(clean.Rewrites);
        Assert.Empty(clean.IndexSuggestions);
        Assert.Empty(clean.Evidence);
    }

    [Fact]
    public async Task A_failure_while_starting_a_tuning_is_shown_in_the_chat_instead_of_only_the_trace()
    {
        var bus = new EventBus();
        Oranda.App.Features.Execution.ExecutionState.For(bus).FocusPending(new ConnectionProfile("DEV-ERP", "u", "h/s"));
        var chat = new AiChatViewModel();
        var launcher = new AiTuningLauncher(new Services(new()
        {
            [typeof(IEditorContext)] = new ThrowingEditor(),
            [typeof(IAiDriver)] = new RecordingDriver(),
            [typeof(IEventBus)] = bus,
        }))
        {
            ChatOverride = () => Task.FromResult<AiChatViewModel?>(chat),
        };

        await launcher.RunAsync(CancellationToken.None); // 예외를 밖으로 내지 않는다

        var text = Assert.Single(chat.Entries.OfType<ChatAssistantEntry>()).Text;
        Assert.Equal(AiTuningLauncher.StartFailedText, text); // 화면에는 고정 글만(상세는 가린 뒤 로그로)
        Assert.DoesNotContain("편집기 오류", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingEditor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => throw new InvalidOperationException("편집기 오류");

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public void Error_texts_cover_the_cli_and_api_codes_and_say_what_to_do()
    {
        Assert.Contains("새 대화", AiErrorText.Describe(new AiError("x", "resume_route_mismatch")), StringComparison.Ordinal);
        Assert.Contains("소속", AiErrorText.Describe(new AiError("x", "session_record_unreadable")), StringComparison.Ordinal);
        Assert.Contains("쓰기 권한", AiErrorText.Describe(new AiError("x", "session_record_failed")), StringComparison.Ordinal);
        Assert.Contains("새 대화", AiErrorText.Describe(new AiError("x", "resume_unknown")), StringComparison.Ordinal);
        Assert.Contains("새 대화", AiErrorText.Describe(new AiError("x", "turn_limit")), StringComparison.Ordinal);

        // context_limit: 한도 종류를 말하는 드라이버 글을 그대로 쓴다(토큰이 아닌 메시지 수·이미지 크기 한도일 수 있다). 글이 비면 일반 글.
        const string imageLimit = "이번 요청이 너무 큽니다(첨부한 이미지의 총 크기 상한). 이미지 수나 크기를 줄여 다시 보내세요.";
        Assert.Equal(imageLimit, AiErrorText.Describe(new AiError(imageLimit, "context_limit")));
        Assert.Contains("한도", AiErrorText.Describe(new AiError(string.Empty, "context_limit")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_limits_fit_inside_the_api_message_limit_of_about_36_mb()
    {
        // 한 번에 붙일 수 있는 최대치(5장 × 5 MB)의 base64 글자 수가 API 대화 상한(36 MB) 안에 들어간다.
        Assert.Equal(36L * 1024 * 1024, AiImageNormalizer.ApiConversationLimitBytes);
        Assert.True(AiImageNormalizer.MaxRequestBase64Chars < AiImageNormalizer.ApiConversationLimitBytes);
        Assert.True(AiImageNormalizer.MaxRequestBase64Chars > 34_000_000);
    }

    /// <summary><c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG 로 저장한다(화면 확인용, 커밋하지 않는다).</summary>
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

    [Fact]
    public Task Render_the_general_chat_states() => HeadlessHost.Run(() =>
    {
        var open = new AiChatViewModel();
        var window = new Window { Width = 440, Height = 520, Content = new AiChatView { DataContext = open } };
        window.Show();
        Render(window, "general-open");
        window.Close();

        var unavailable = new AiChatViewModel { UnavailableProvider = () => "AI 모델이 「선택 안 함」 입니다. 설정에서 AI 모델을 고르면 보낼 수 있습니다." };
        window = new Window { Width = 440, Height = 520, Content = new AiChatView { DataContext = unavailable } };
        window.Show();
        Render(window, "general-unavailable");
        window.Close();

        var chatting = new AiChatViewModel();
        chatting.BeginTurn("NVL 과 COALESCE 차이가 뭐야?");
        chatting.AddAssistantText("**NVL** 은 인자 2개, **COALESCE** 는 여러 개를 받고 앞에서부터 첫 NULL 이 아닌 값을 돌려줍니다.");
        chatting.EndTurn();
        chatting.Draft = "이어서 예시도 보여 줘";
        window = new Window { Width = 440, Height = 520, Content = new AiChatView { DataContext = chatting } };
        window.Show();
        Render(window, "general-chatting");
        window.Close();
    });

    [Theory]
    [InlineData("""{"summary":"요약","rewrites":[{"label":"a","sql":"select 2 from dual"}],"index_suggestions":[],"evidence":[]}""", 0, 0, 1)] // rationale 없음: 쓸 수 있다
    [InlineData("""{"summary":"요약","rewrites":[{"label":"a","rationale":"b"}]}""", 1, 0, 0)] // sql 없음
    [InlineData("""{"summary":"요약","rewrites":[{}]}""", 1, 0, 0)]
    [InlineData("""{"summary":"요약","rewrites":[null]}""", 1, 0, 0)]
    [InlineData("""{"summary":"요약","index_suggestions":[{"table":"T","ddl":"x","rationale":"r"}]}""", 0, 0, 0)] // columns 없음: 쓸 수 있다
    [InlineData("""{"summary":"요약","index_suggestions":[{"table":"T","rationale":"r"}]}""", 0, 1, 0)] // ddl 없음
    [InlineData("""{"summary":"요약","index_suggestions":[{"table":"T","columns":["C"],"ddl":"CREATE INDEX i ON t(c)"},null,{}]}""", 0, 2, 0)] // 나머지 후보는 남는다
    public async Task Nested_missing_fields_never_throw_and_unusable_candidates_are_skipped_with_one_line(string json, int skippedRewrites, int skippedIndexes, int keptRewrites)
    {
        var h = new Harness();
        h.Driver.Result = JsonDocument.Parse(json).RootElement.Clone();

        await h.Flow.StartAsync(new TuningStartChoice(false, true, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == "요약"); // 요약은 잃지 않는다
        Assert.Equal(keptRewrites, h.Flow.LastResult!.Rewrites.Count);
        var notice = h.Chat.Entries.OfType<ChatAssistantEntry>().FirstOrDefault(e => e.Text.StartsWith("AI 가 보낸 후보 중", StringComparison.Ordinal));
        if (skippedRewrites + skippedIndexes == 0)
        {
            Assert.Null(notice);
        }
        else
        {
            Assert.Contains($"재작성 {skippedRewrites}개 · 인덱스 {skippedIndexes}개", notice!.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_index_review_tolerates_a_suggestion_without_columns()
    {
        var parsed = Oranda.Core.Ai.Schemas.TuningResult.Parse(JsonDocument.Parse("""{"index_suggestions":[{"table":"T","ddl":"x"}]}""").RootElement);

        var verdict = AiIndexReview.Review(parsed.IndexSuggestions[0], null);

        Assert.Null(verdict.Dropped);
    }

    [Fact]
    public void Whitespace_only_sql_and_ddl_candidates_are_skipped_like_missing_ones()
    {
        var parsed = Oranda.Core.Ai.Schemas.TuningResult.Parse(JsonDocument.Parse(
            """{"summary":"s","rewrites":[{"label":"a","sql":"   ","rationale":"r"},{"label":"b","sql":"select 1 from dual","rationale":"r"}],"index_suggestions":[{"table":"T","columns":["C"],"ddl":"   ","rationale":"r"}]}""").RootElement);

        var clean = AiTuningFlow.Sanitize(parsed, out var skippedRewrites, out var skippedIndexes);

        Assert.Equal("b", Assert.Single(clean.Rewrites).Label);
        Assert.Empty(clean.IndexSuggestions);
        Assert.Equal((1, 1), (skippedRewrites, skippedIndexes));
    }

    [Fact]
    public void Structured_output_and_server_error_codes_keep_the_driver_detail()
    {
        var invalid = AiErrorText.Describe(new AiError("rewrites[0].sql 칸이 비어 있습니다", "structured_output_invalid"));
        Assert.Contains("형식에 맞지 않습니다", invalid, StringComparison.Ordinal);
        Assert.Contains("rewrites[0].sql", invalid, StringComparison.Ordinal); // 어긋난 칸의 경로를 잃지 않는다

        Assert.Equal("필터 글", AiErrorText.Describe(new AiError("필터 글", "content_filtered")));
        Assert.Contains("콘텐츠 필터", AiErrorText.Describe(new AiError(string.Empty, "content_filtered")), StringComparison.Ordinal);
        Assert.Equal("서버가 오류를 알려 왔습니다: boom", AiErrorText.Describe(new AiError("서버가 오류를 알려 왔습니다: boom", "api_error")));
        Assert.Contains("서버", AiErrorText.Describe(new AiError(string.Empty, "api_error")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_modify_suggestion_without_an_existing_index_is_a_new_index_in_both_the_chat_and_the_tab()
    {
        var parsed = Oranda.Core.Ai.Schemas.TuningResult.Parse(JsonDocument.Parse(
            """{"index_suggestions":[{"table":"T","columns":["C"],"ddl":"CREATE INDEX i ON t(c)","rationale":"r","action":"modify"}]}""").RootElement);

        var clean = AiTuningFlow.Sanitize(parsed);

        Assert.False(clean.IndexSuggestions[0].IsModify); // 대화의 「변형」 개수에서 빠지고
        var tab = AiTuningFlow.BuildIndexText([new ReviewedIndex(clean.IndexSuggestions[0], [])], [], masked: false);
        Assert.Contains("새 인덱스: T", tab, StringComparison.Ordinal); // 탭에서도 새 인덱스다
    }

    [Fact]
    public void The_start_dialog_for_a_profile_shows_only_the_profile_model_never_the_settings_model_and_requests_carry_the_expectation()
    {
        var settings = Profile(model: "example-model");
        settings = settings with { Model = "opus" }; // Claude Code 용 모델 설정이 남아 있어도
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, settings);

        Assert.Equal(["example-model"], info.Models.Select(m => m.Id));
        Assert.Equal("example-model", info.InitialModelId);
        Assert.False(info.ShowEffort); // API 경로는 effort 를 쓰지 않는다
    }

    [Fact]
    public async Task Tuning_and_general_requests_both_carry_the_expected_profile()
    {
        var h = new Harness();
        await h.SendAsync("질문");
        await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.All(h.Driver.Requests, r => Assert.Equal("http://llm.example:8080", r.Expected!.BaseUrl));
    }

    // ── 일반 대화의 도구 범위: 활성 SQL 탭의 문장 ──

    [Fact]
    public async Task The_active_statement_is_the_tool_scope_and_the_prompt_target_and_the_chat_says_which()
    {
        var h = new Harness();

        await h.SendAsync("이 문장 인덱스 확인해 줘");

        var request = Assert.Single(h.Driver.Requests);
        Assert.Equal(FakeEditor.DefaultSql, request.Session!.Context.StatementText); // 튜닝과 같은 범위 계산이 쓰는 문장
        Assert.Equal(AiAccessLevel.Query, request.Session.Context.AccessLevel);
        Assert.Contains("<statement>" + Environment.NewLine + FakeEditor.DefaultSql, request.Prompt, StringComparison.Ordinal);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("DB 도구는 활성 탭의 이 문장 범위에서만", StringComparison.Ordinal));

        // 같은 문장이면 안내를 되풀이하지 않고, 문장이 바뀌면 다시 보인다.
        await h.SendAsync("한 번 더");
        Assert.Single(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("DB 도구는", StringComparison.Ordinal));
        h.Editor.Statement = "SELECT 1 FROM dual";
        await h.SendAsync("다른 문장");
        Assert.Equal(2, h.Chat.Entries.OfType<ChatAssistantEntry>().Count(e => e.Text.StartsWith("DB 도구는", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false, "SELECT 1")] // 활성 탭이 없다(또는 SQL 탭이 아니다)
    [InlineData(true, null)] // 탭은 있으나 커서 자리에 문장이 없다
    [InlineData(true, "   ")] // 빈 글
    public async Task Without_an_active_statement_the_answer_comes_without_tools_and_the_prompt_says_so(bool hasDocument, string? statement)
    {
        var h = new Harness();
        h.Editor.HasDocument = hasDocument;
        h.Editor.Statement = statement;

        await h.SendAsync("질문");

        var request = Assert.Single(h.Driver.Requests);
        Assert.Equal(AiAccessLevel.Off, request.Session!.Level);
        Assert.Empty(h.SessionContexts);
        Assert.DoesNotContain("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("no statement in the active SQL tab", request.Prompt, StringComparison.Ordinal); // 프롬프트도 사실대로
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == (hasDocument ? AiTuningFlow.GeneralNoStatementText : AiTuningFlow.GeneralOtherDocumentText));
    }

    [Fact]
    public async Task A_masking_connection_masks_the_scope_statement_in_the_prompt_and_says_only_tool_results_and_that_statement_are_masked()
    {
        var h = new Harness(withMasker: true) { Mask = true };

        await h.SendAsync("이 문장 봐 줘");

        var request = Assert.Single(h.Driver.Requests);
        Assert.Contains(":v1", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("'10'", request.Prompt, StringComparison.Ordinal); // 값이 프롬프트로 새지 않는다
        Assert.Equal(FakeEditor.DefaultSql, request.Session!.Context.StatementText); // 원문은 범위 검사에만(LLM 으로 가지 않는다)
        Assert.Contains("the user's own message was not masked", request.Prompt, StringComparison.Ordinal); // 사실대로
        Assert.DoesNotContain("Literal values in the SQL were replaced", request.Prompt, StringComparison.Ordinal);
        Assert.NotNull(request.Session.Run.Literals);
    }

    private sealed class CountingSource : IAgentSessionSource
    {
        public int Acquired { get; private set; }

        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct)
        {
            Acquired++; // 실제 앱에서는 여기서 비밀번호 입력 창이나 모니터 세션이 열릴 수 있다
            return ValueTask.FromResult<ISession>(new Oranda.Testing.FakeSession());
        }
    }

    private sealed class OneTableExtractor : IStatementReferenceExtractor
    {
        private static readonly AgentObjectRef Order = new("APP", "TB_ORDER", "TABLE");
        private static readonly AgentObjectRef Other = new("APP", "TB_OTHER", "TABLE");

        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(new AgentStatementInfo(StatementKind.Query, false, [Order], 1));

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult<AgentObjectRef?>(name.Equals("TB_ORDER", StringComparison.OrdinalIgnoreCase) ? Order : name.Equals("TB_OTHER", StringComparison.OrdinalIgnoreCase) ? Other : null);

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentObjectRef>>([]);
    }

    private sealed class OkHandler(string toolName) : IAgentToolHandler
    {
        public string ToolName { get; } = toolName;

        public int Calls { get; private set; }

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(AgentToolOutput.Ok(new System.Text.Json.Nodes.JsonObject { ["ok"] = true }));
        }
    }

    private sealed class NullAudit : IAgentAuditStore
    {
        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>([]);
    }

    [Fact]
    public async Task The_real_gate_allows_the_statement_scope_objects_denies_others_and_a_toolless_run_never_touches_the_db_session()
    {
        // 가짜 세션이 아니라 실제 AgentGate: 흐름이 만든 실행 범위(요청의 Session)로 도구를 불러 본다.
        var source = new CountingSource();
        var describe = new OkHandler(AgentToolNames.DescribeObject);
        var gate = new AgentGate(source, new OneTableExtractor(), [describe], new NullAudit());
        var h = new Harness();
        var results = new List<(string Object, AiToolJudgement Verdict, string? Note, int ToolCount)>();
        h.Driver.OnRun = async request =>
        {
            var tools = gate.ListTools(request.Session!);
            foreach (var name in new[] { "TB_ORDER", "TB_OTHER" })
            {
                var response = await gate.CallToolAsync(request.Session!, AgentToolNames.DescribeObject, new System.Text.Json.Nodes.JsonObject { ["object"] = name }, CancellationToken.None);
                results.Add((name, response.Judgement, response.Note, tools.Count));
            }
        };
        h.Editor.Statement = "SELECT * FROM tb_order";

        await h.SendAsync("TB_ORDER 컬럼을 보여 줘");

        Assert.Equal(AiToolJudgement.Allowed, results.Single(r => r.Object == "TB_ORDER").Verdict); // 범위 안
        var other = results.Single(r => r.Object == "TB_OTHER");
        Assert.Equal(AiToolJudgement.Denied, other.Verdict); // 범위 밖: 새 권한 없음
        Assert.Equal("scope", other.Note);
        Assert.Equal(1, describe.Calls);
        Assert.True(results[0].ToolCount > 0);

        // 문장이 없는 일반 대화: 도구 목록이 비고 모든 호출이 DB 세션을 얻기 전에 거부된다(비밀번호 창·모니터 세션을 열지 않는다).
        source = new CountingSource();
        gate = new AgentGate(source, new OneTableExtractor(), [describe], new NullAudit());
        results.Clear();
        h.Editor.Statement = null;
        await h.SendAsync("문장 없이 질문");

        Assert.All(results, r => Assert.Equal((AiToolJudgement.Denied, 0), (r.Verdict, r.ToolCount)));
        Assert.Equal(0, source.Acquired);
    }

    // ── 가리는 접속의 입력 글 확인 ──

    [Fact]
    public async Task A_masking_conversation_asks_once_about_the_typed_text_and_again_for_a_new_conversation_or_connection()
    {
        var h = new Harness(withMasker: true) { Mask = true };

        await h.SendAsync("첫 질문");
        await h.SendAsync("둘째 질문");

        var notice = Assert.Single(h.TextConfirmed); // 대화당 한 번
        Assert.Equal(0, notice.Count);
        Assert.True(notice.Masking);
        Assert.Equal(2, h.Driver.Requests.Count);

        h.Chat.StartNewConversation(); // 새 대화
        await h.SendAsync("새 대화의 질문");
        Assert.Equal(2, h.TextConfirmed.Count);

        h.Connected = Connection with { ConnectionName = "PRD-ERP" }; // 접속이 바뀜
        h.ConnectionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        await h.SendAsync("다른 접속");
        Assert.Equal(3, h.TextConfirmed.Count);
    }

    [Fact]
    public async Task Declining_the_text_confirmation_sends_nothing_and_keeps_the_text()
    {
        var h = new Harness(withMasker: true) { Mask = true, ConfirmTextAnswer = false };

        await h.SendAsync("민감한 글");

        Assert.Empty(h.Driver.Requests);
        Assert.Equal("민감한 글", h.Chat.Draft);
        Assert.Empty(h.Chat.Entries.OfType<ChatUserEntry>());
    }

    [Fact]
    public async Task A_conversation_that_does_not_mask_never_asks_about_the_typed_text()
    {
        var h = new Harness { Mask = false };

        await h.SendAsync("질문");

        Assert.Empty(h.TextConfirmed);
        Assert.Single(h.Driver.Requests);
    }

    [Fact]
    public async Task Without_a_connection_a_global_mask_setting_still_asks_and_without_a_confirm_window_nothing_is_sent()
    {
        var h = new Harness { Connected = null, ConnectionId = null, Mask = true };
        await h.SendAsync("질문");
        Assert.Single(h.TextConfirmed);
        Assert.Equal("(연결 없음)", h.TextConfirmed[0].ConnectionName);
        Assert.Single(h.Driver.Requests);

        // 확인 창을 열 수 없으면(위임자 없음) 보내지 않는다.
        var flow = new AiTuningFlow(
            new FakeEditor(), new RecordingDriver(), h.Chat, (_, _, _) => { }, settings: () => h.Settings,
            generalContext: _ => Task.FromResult(new AiGeneralScope(null, true)));
        var chat = h.Chat;
        chat.StartNewConversation();
        await flow.FollowUpAsync(new ChatFollowUp("글", []));
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.TextConfirmMissingText);
        Assert.Equal("글", chat.Draft);
    }

    // ── 대화 세대와 준비 중 잠금 ──

    [Fact]
    public async Task A_conversation_change_while_the_text_confirmation_is_open_sends_no_request_into_the_new_conversation()
    {
        var h = new Harness(withMasker: true) { Mask = true };
        var busyDuring = false;
        var canStartNewDuring = true;

        // 확인 창이 떠 있는 동안 「새 대화」 단추는 꺼져 있다. 그래도 모델 쪽에서 대화가 바뀌면(방어) 요청이 새 대화로 가지 않는다.
        h.ConfirmTextAnswer = true;
        var flow = h.Flow;
        h.Chat.Draft = "옛 대화의 글";
        var chat = h.Chat;
        var confirmFlow = new AiTuningFlow(
            h.Editor, h.Driver, chat, (_, _, _) => { }, settings: () => h.Settings, masker: new PassThroughMasker(), maskRequired: _ => true,
            generalContext: _ => Task.FromResult(new AiGeneralScope(Connection, true, h.ConnectionId)),
            confirmText: (_, _) =>
            {
                busyDuring = chat.IsBusy;
                canStartNewDuring = chat.NewConversationCommand.CanExecute(null);
                chat.StartNewConversation();
                return Task.FromResult(true);
            });

        await confirmFlow.FollowUpAsync(new ChatFollowUp("옛 대화의 글", []));

        Assert.True(busyDuring);
        Assert.False(canStartNewDuring); // 준비 중에는 새 대화를 막는다
        Assert.Empty(h.Driver.Requests);
        Assert.Empty(chat.Entries.OfType<ChatUserEntry>()); // 새 대화에 옛 요청의 줄이 생기지 않는다
        Assert.False(chat.IsBusy);
        Assert.NotNull(flow);
    }

    [Fact]
    public async Task A_conversation_change_while_the_index_check_runs_does_not_save_the_old_result_as_the_new_report_source()
    {
        var h = new Harness();
        h.Driver.Result = JsonDocument.Parse("""{"summary":"요약","rewrites":[],"index_suggestions":[{"table":"T","columns":["C"],"ddl":"CREATE INDEX i ON t(c)","rationale":"r"}],"evidence":[]}""").RootElement.Clone();
        h.OnReadIndexes = () => h.Chat.StartNewConversation(); // 결과를 반영하는 사이 대화가 바뀐다

        await h.Flow.StartAsync(new TuningStartChoice(false, true, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.Null(h.Chat.ReportSource); // 옛 결과의 레포트 재료가 새 대화에 붙지 않는다
        Assert.DoesNotContain(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("인덱스 후보", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_new_conversation_button_stays_off_while_the_tuning_result_is_being_applied()
    {
        var h = new Harness();
        var canStartNew = true;
        h.Driver.Result = JsonDocument.Parse("""{"summary":"요약","index_suggestions":[{"table":"T","columns":["C"],"ddl":"CREATE INDEX i ON t(c)","rationale":"r"}]}""").RootElement.Clone();
        h.OnReadIndexes = () => canStartNew = h.Chat.NewConversationCommand.CanExecute(null);

        await h.Flow.StartAsync(new TuningStartChoice(false, true, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.False(canStartNew);
        Assert.True(h.Chat.NewConversationCommand.CanExecute(null)); // 끝나면 다시 켜진다
    }

    // ── 접속 비교는 이름이 아니라 id ──

    [Fact]
    public async Task The_connection_is_compared_by_id_not_by_name()
    {
        var h = new Harness();
        await h.SendAsync("첫 질문");

        h.Connected = Connection with { ConnectionName = "DEV-ERP (이름만 바꿈)" }; // 같은 id, 이름만 다름
        await h.SendAsync("이름만 바뀐 접속");
        Assert.Equal("sess-1", h.Driver.Requests[1].ResumeId); // 같은 대화로 이어 간다

        h.Connected = Connection; // 같은 이름, 다른 id
        h.ConnectionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        await h.SendAsync("같은 이름의 다른 접속");
        Assert.Null(h.Driver.Requests[2].ResumeId); // 새 대화
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralConnectionChangedText);
    }

    // ── 구조 결과 정리(item 8) ──

    [Fact]
    public void A_modify_with_a_drop_but_no_existing_index_name_stays_a_modify_and_is_flagged()
    {
        var parsed = Oranda.Core.Ai.Schemas.TuningResult.Parse(JsonDocument.Parse(
            """{"index_suggestions":[{"table":"T","columns":["C"],"ddl":"DROP INDEX app.i1;\nCREATE INDEX i1 ON t(c, d)","rationale":"r","action":"modify"},{"table":"T","columns":["C"],"ddl":"CREATE INDEX i2 ON t(c)","rationale":"r","action":"modify"}]}""").RootElement);

        var clean = AiTuningFlow.Sanitize(parsed, out _, out _, out var flagged);

        Assert.True(clean.IndexSuggestions[0].IsModify); // DROP 이 있으면 create 로 바꾸지 않는다
        Assert.False(clean.IndexSuggestions[1].IsModify); // DROP 이 없으면 새 인덱스
        Assert.Equal(1, flagged);
        var tab = AiTuningFlow.BuildIndexText([new ReviewedIndex(clean.IndexSuggestions[0], [])], [], masked: false);
        Assert.Contains("변형(기존 인덱스 이름 없음", tab, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_summary_makes_no_empty_bubble_and_skipped_indexes_are_not_mentioned_when_indexes_were_not_asked()
    {
        var h = new Harness();
        h.Driver.Result = JsonDocument.Parse("""{"rewrites":[{"label":"a"}],"index_suggestions":[{"table":"T"}]}""").RootElement.Clone();

        await h.Flow.StartAsync(new TuningStartChoice(false, false, "example-model"), Connection with { StatementText = "SELECT 1 FROM dual" });

        Assert.DoesNotContain(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => string.IsNullOrWhiteSpace(e.Text));
        var notice = Assert.Single(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("AI 가 보낸 후보 중", StringComparison.Ordinal));
        Assert.DoesNotContain("인덱스", notice.Text, StringComparison.Ordinal);
        Assert.Contains("재작성 1개", notice.Text, StringComparison.Ordinal);
    }

    // ── 오류 글·로그 ──

    [Fact]
    public void Redact_hides_secret_looking_values_and_url_user_info()
    {
        var clean = AiErrorText.Redact("HttpRequestException: Authorization: Bearer sk-abc123 at https://user:pw@llm.example:8080/v1 api_key=XYZ");

        Assert.DoesNotContain("sk-abc123", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pw", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("XYZ", clean, StringComparison.Ordinal);
        Assert.Contains("llm.example", clean, StringComparison.Ordinal);
    }

    // ── 연결: UnavailableReason·일반 대화 접속 정보 ──

    private sealed class FakeGate(bool aiEnabled) : Oranda.Core.Features.IFeatureGate
    {
        public bool IsEnabled(string featureId) => featureId != Oranda.Core.Features.FeatureIds.AiTuning || aiEnabled;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;

        public bool ReadOnlyRole => false;

        public IReadOnlyCollection<string> PendingRestart => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    [Fact]
    public void The_unavailable_reason_also_looks_at_the_ai_feature_gate()
    {
        var off = new AiTuningLauncher(new Services(new() { [typeof(Oranda.Core.Features.IFeatureGate)] = new FakeGate(false), [typeof(IAiDriver)] = new RecordingDriver() }));
        var chat = new AiChatViewModel();
        off.EnsureChat(chat);
        Assert.Contains("꺼져", chat.UnavailableReason!, StringComparison.Ordinal);

        var on = new AiTuningLauncher(new Services(new() { [typeof(Oranda.Core.Features.IFeatureGate)] = new FakeGate(true), [typeof(IAiDriver)] = new RecordingDriver() }));
        var chat2 = new AiChatViewModel();
        on.EnsureChat(chat2);
        Assert.Null(chat2.UnavailableReason);
    }

    private sealed class FakeStore(Func<ConnectionsDocument> load) : Oranda.Core.Storage.IConnectionStore
    {
        public Task<Oranda.Core.Storage.StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) => Task.FromResult(new Oranda.Core.Storage.StoredDocument<ConnectionsDocument>(load()));

        public Task SaveAsync(ConnectionsDocument document, CancellationToken ct) => Task.CompletedTask;
    }

    private static (AiTuningLauncher Launcher, ConnectionProfile Profile) LauncherWithShown(Func<ConnectionsDocument> store, AiAccessLevel? access = AiAccessLevel.Query, ConnectionEnvironment env = ConnectionEnvironment.Development)
    {
        var bus = new EventBus();
        var profile = new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: Guid.Parse("66666666-6666-6666-6666-666666666666"), Environment: env, AiAccess: access);
        Oranda.App.Features.Execution.ExecutionState.For(bus).FocusPending(profile);
        var map = new Dictionary<Type, object> { [typeof(IEventBus)] = bus, [typeof(Oranda.Core.Storage.IConnectionStore)] = new FakeStore(store) };
        return (new AiTuningLauncher(new Services(map)), profile);
    }

    [Fact]
    public async Task The_general_scope_carries_the_connection_id_the_access_level_and_the_active_profile()
    {
        var (launcher, profile) = LauncherWithShown(() => new ConnectionsDocument { Profiles = [new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: Guid.Parse("66666666-6666-6666-6666-666666666666"), AiAccess: AiAccessLevel.Plan)] });

        var scope = await launcher.GeneralContextAsync(CancellationToken.None);

        Assert.Equal(profile.Id, scope.ConnectionId);
        Assert.Equal(AiAccessLevel.Plan, scope.Context!.AccessLevel); // 접속의 지금 허용 수준(저장소 최신 값)
        Assert.Equal("DEV-ERP", scope.Context.ConnectionName);
        Assert.Equal(profile.Id, launcher.ActiveProfile!.Id); // 도구 범위가 쓸 접속
        Assert.False(scope.Masking);
    }

    [Fact]
    public async Task A_connection_with_access_off_is_reported_as_off()
    {
        var (launcher, _) = LauncherWithShown(() => new ConnectionsDocument { Profiles = [new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: Guid.Parse("66666666-6666-6666-6666-666666666666"), AiAccess: AiAccessLevel.Off)] });

        var scope = await launcher.GeneralContextAsync(CancellationToken.None);

        Assert.Equal(AiAccessLevel.Off, scope.Context!.AccessLevel);
    }

    [Fact]
    public async Task An_unreadable_store_or_a_deleted_connection_is_treated_as_a_masking_conversation_without_tools()
    {
        var (broken, _) = LauncherWithShown(() => throw new IOException("저장소를 읽을 수 없음"));
        var scope = await broken.GeneralContextAsync(CancellationToken.None);
        Assert.Null(scope.Context); // 접속을 믿지 않는다
        Assert.True(scope.Masking); // 알 수 없으면 가리는 대화로
        Assert.Null(broken.ActiveProfile);

        var (deleted, _) = LauncherWithShown(() => new ConnectionsDocument { Profiles = [] });
        scope = await deleted.GeneralContextAsync(CancellationToken.None);
        Assert.Null(scope.Context);
        Assert.True(scope.Masking);
    }

    [Fact]
    public async Task With_no_shown_connection_the_scope_has_no_context_and_follows_the_global_mask_setting()
    {
        var launcher = new AiTuningLauncher(new Services(new() { [typeof(IEventBus)] = new EventBus() }));

        var scope = await launcher.GeneralContextAsync(CancellationToken.None);

        Assert.Null(scope.Context);
        Assert.False(scope.Masking); // 설정 서비스가 없으면 가리지 않는 기본값
    }

    [Fact]
    public Task The_chat_view_connects_itself_to_the_ai_flow_when_it_opens() => HeadlessHost.Run(() =>
    {
        var services = new Services([]);
        var previous = AiChatView.ServicesProvider;
        AiChatView.ServicesProvider = () => services;
        try
        {
            var vm = new AiChatViewModel();
            var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
            window.Show();

            Assert.True(AiTuningLauncher.For(services).IsWired(vm)); // 생성자 배선: 튜닝을 시작하지 않아도 입력이 AI 로 이어진다
            window.Close();
        }
        finally
        {
            AiChatView.ServicesProvider = previous;
        }
    });

    [Fact]
    public void The_conversation_size_test_reads_the_driver_constant_directly()
    {
        // 한 번에 붙일 수 있는 최대치(5장 × 5 MB)의 base64 글자 수가 API 드라이버의 실제 대화 상한 안에 들어간다.
        var driverLimit = new Oranda.Platform.Ai.Api.OpenAiApiOptions().MaxBytes;
        Assert.Equal(AiImageNormalizer.ApiConversationLimitBytes, driverLimit);
        Assert.True(AiImageNormalizer.MaxRequestBase64Chars < driverLimit);
        Assert.True(AiImageNormalizer.MaxRequestBase64Chars > 34_000_000);
    }

    // ── 재리뷰: 문장 출처(탭의 접속·문서 종류) ──

    [Theory]
    [InlineData("other-connection")]
    [InlineData("plsql-document")]
    [InlineData("unknown-document")]
    public async Task A_statement_from_another_connection_tab_or_a_non_sql_document_is_never_sent(string situation)
    {
        var h = new Harness(withMasker: true) { Mask = false };
        h.Editor.Statement = "SELECT * FROM t WHERE c = 'SECRET-LITERAL'";
        switch (situation)
        {
            case "other-connection":
                h.Editor.TabConnectionId = Guid.Parse("99999999-9999-9999-9999-999999999999"); // 탭은 다른 접속
                break;
            case "plsql-document":
                h.SqlDocumentActive = false; // 활성 문서가 SQL 탭이 아니다
                break;
            default:
                h.SqlDocumentActive = null; // 판단할 수 없으면 닫는다
                break;
        }

        await h.SendAsync("질문");

        var request = Assert.Single(h.Driver.Requests);
        Assert.DoesNotContain("SECRET-LITERAL", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(AiAccessLevel.Off, request.Session!.Level);
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralOtherDocumentText);
    }

    [Fact]
    public async Task A_tab_without_a_connection_or_with_the_same_connection_keeps_the_scope()
    {
        var h = new Harness();
        h.Editor.TabConnectionId = null;
        await h.SendAsync("접속 없는 탭");
        Assert.Contains("<statement>", h.Driver.Requests[0].Prompt, StringComparison.Ordinal);

        h.Chat.StartNewConversation();
        h.Editor.TabConnectionId = h.ConnectionId; // 같은 접속
        await h.SendAsync("같은 접속의 탭");
        Assert.Contains("<statement>", h.Driver.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scope_line_says_the_statement_is_sent_too_and_the_prompt_tells_the_model_other_sql_is_invisible()
    {
        var h = new Harness();

        await h.SendAsync("질문");

        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("이 문장도 함께 보냅니다", StringComparison.Ordinal));
        Assert.Contains("the tools cannot see it", h.Driver.Requests.Single().Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_masking_cannot_be_done_the_statement_is_not_sent_at_all()
    {
        // 가림 기능이 없음
        var noMasker = new Harness(withMasker: false) { Mask = true };
        noMasker.Editor.Statement = "SELECT * FROM t WHERE c = 'SECRET-LITERAL'";
        await noMasker.SendAsync("질문");
        var request = Assert.Single(noMasker.Driver.Requests);
        Assert.DoesNotContain("SECRET-LITERAL", request.Prompt, StringComparison.Ordinal); // 가림 우회 금지
        Assert.DoesNotContain("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.Null(request.Session!.Context.StatementText);

        // 가림 실패
        var failing = new Harness(withMasker: true) { Mask = true, MaskerOverride = new FailingMasker() };
        failing.Editor.Statement = "SELECT * FROM t WHERE c = 'SECRET-LITERAL'";
        await failing.SendAsync("질문");
        request = Assert.Single(failing.Driver.Requests);
        Assert.DoesNotContain("SECRET-LITERAL", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(failing.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralMaskFailedText);

        // 허용 수준 끔
        var off = new Harness(withMasker: true) { Connected = Connection with { AccessLevel = AiAccessLevel.Off }, Mask = true };
        await off.SendAsync("질문");
        Assert.DoesNotContain("<statement>", Assert.Single(off.Driver.Requests).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_bind_that_collides_with_the_placeholder_prefix_moves_the_conversation_to_a_new_vault()
    {
        var h = new Harness(withMasker: true) { Mask = true, MaskerOverride = new PrefixAwareMasker() };
        h.Editor.Statement = "SELECT * FROM t WHERE c = 1";
        await h.SendAsync("첫 질문");
        h.Editor.Statement = "SELECT * FROM t WHERE c = :v1"; // 진짜 바인드 :v1

        await h.SendAsync("둘째 질문");

        Assert.Null(h.Driver.Requests[1].ResumeId); // 앞 사전(접두 v)을 이어 쓰지 않고 새 대화
        Assert.Contains(h.Chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiTuningFlow.GeneralPrefixChangedText);
        Assert.Equal("lit", h.Driver.Requests[1].Session!.Run.Literals!.Prefix);
    }

    [Fact]
    public async Task Sending_only_images_does_not_ask_about_typed_text()
    {
        var h = new Harness(withMasker: true) { Mask = true };
        await h.Chat.AddImagesAsync([AiChatImageAttachTests.Png()]);
        h.Chat.Draft = string.Empty;
        h.Chat.SendCommand.Execute(null);
        while (h.Chat.IsRunning || h.Flow.IsRunning || h.Driver.Requests.Count == 0)
        {
            await Task.Delay(5);
        }

        Assert.Empty(h.TextConfirmed); // 입력한 글이 없다
        Assert.Single(h.Confirmed); // 이미지는 따로 확인한다
        Assert.Single(h.Driver.Requests);
    }

    // ── 재리뷰: 튜닝 바인드·대화 대조·창·로그 ──

    [Fact]
    public void General_sessions_never_carry_the_tuning_binds_but_tuning_sessions_do()
    {
        var launcher = new AiTuningLauncher(new Services([]))
        {
            SessionTracker = session => session,
            ActiveProfile = new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: Guid.NewGuid()),
        };
        var list = new Oranda.App.Features.Binds.BindVariableStore();
        list.Add("WORD_DH").Text = "20261030";
        var statement = new SqlStatement("SELECT 1 FROM dual WHERE d = :word_dh", new TextRange(0, 10), StatementKind.Query) { Verb = "SELECT", BindNames = ["word_dh"] };
        launcher.StartedBinds.Capture(statement, list);

        var tuning = launcher.NewSessionFor(general: false, Connection, null)!;
        var general = launcher.NewSessionFor(general: true, Connection, null)!;

        Assert.Contains("word_dh", tuning.Run.Binds.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(general.Run.Binds); // 앞 튜닝의 값이 일반 대화로 새지 않는다
    }

    [Fact]
    public Task The_text_warning_dialog_returns_true_on_confirm_and_false_on_cancel() => HeadlessHost.Run(async () =>
    {
        var notice = new AiImageSendNotice("ERP 운영", ConnectionEnvironment.Production, "예제 LLM", "http://llm.example:8080", 0, 0, Masking: true, AiAccessLevel.Query);
        var owner = new Window { Width = 300, Height = 200 };
        owner.Show();

        var confirmed = AiTextWarningDialog.ShowAsync(owner, notice);
        var dialog = owner.OwnedWindows.OfType<AiTextWarningDialog>().Single();
        Assert.Equal("예제 LLM · http://llm.example:8080", dialog.FindControl<TextBlock>("RecipientText")!.Text);
        Assert.True(dialog.FindControl<Border>("EnvBadge")!.IsVisible); // 운영 배지
        ((AiImageWarningViewModel)dialog.DataContext!).ConfirmCommand.Execute(null);
        Assert.True(await confirmed);

        var cancelled = AiTextWarningDialog.ShowAsync(owner, notice);
        dialog = owner.OwnedWindows.OfType<AiTextWarningDialog>().Single();
        ((AiImageWarningViewModel)dialog.DataContext!).CancelCommand.Execute(null);
        Assert.False(await cancelled);
        owner.Close();
    });

    [Fact]
    public void Redact_also_erases_sql_quoted_text()
    {
        var clean = AiErrorText.Redact("ORA-00933 near WHERE name = 'Hong Gildong' AND note = 'it''s secret'");

        Assert.DoesNotContain("Hong", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", clean, StringComparison.Ordinal);
        Assert.Contains("'***'", clean, StringComparison.Ordinal);
        Assert.Contains("WHERE name", clean, StringComparison.Ordinal);
    }
}
