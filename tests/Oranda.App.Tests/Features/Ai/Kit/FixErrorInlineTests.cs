using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.Execution;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Language;
using Oranda.Core.Settings;
using Oranda.Core.Text;
using Oranda.Language;
using Oranda.Language.Ai.Literals;
using Oranda.Language.Lexing;

namespace Oranda.App.Tests.Features.Ai.Kit;

/// <summary>
/// 기존 명령 등급(설계 §1.4)과 「오류 고치기」 바로 처리(TSK-15-04): 채팅창 대신 제안 막대, 가벼운 일·도구 없음, 가리는 대화의 서버 글·원문 위치 누출 없음(실제 가림),
/// 받아들이기 = 그 자리 차이 미리보기(까닭 한 줄) · 탭·문장 대조 · 자리표시 되돌리기.
/// </summary>
public class FixErrorInlineTests
{
    private const string Script = "SELECT 1 FROM dual;\nSELECT * FROM tb_order WHERE stat_cd = 'SECRET' AND qty > zz;\n";

    private const string ServerText = "ORA-00904: \"ZZ\": invalid identifier";

    private sealed class Tabs : IEditorTabs
    {
        public Dictionary<Guid, string> Texts { get; } = [];

        public Guid? Active { get; set; }

        public Guid? ActiveTabId => Active;

        public string? GetText(Guid tabId) => Texts.GetValueOrDefault(tabId);

        public SqlStatement? GetStatementAt(Guid tabId, int offset) =>
            Texts.TryGetValue(tabId, out var text) ? StatementSplitter.GetStatementAt(text, offset, SplitOptions.Default) : null;

        public bool Activate(Guid tabId) => (Active = tabId) is not null;

        public string? GetTitle(Guid tabId) => "주문 조회";

        public Guid OpenText(string title, string text, Guid? connectionFrom = null) => throw new InvalidOperationException("막대는 새 탭을 열지 않는다");

        public bool ReplaceText(Guid tabId, string text) => throw new InvalidOperationException("막대는 탭 글을 직접 바꾸지 않는다");

        public void CopyBinds(Guid fromTab, Guid toTab)
        {
        }
    }

    private sealed class Services(EventBus bus) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEventBus) ? bus : null;
    }

    private sealed class NoEditor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Fixture
    {
        public Fixture(ConnectionEnvironment env = ConnectionEnvironment.Development)
        {
            Profile = new ConnectionProfile("dev-example", "scott", "h/s", Id: Guid.NewGuid(), Environment: env, AiAccess: AiAccessLevel.Plan);
            Tab = Guid.NewGuid();
            TabsFake.Texts[Tab] = Script;
            TabsFake.Active = Tab;
            Statement = TabsFake.GetStatementAt(Tab, 21)!;
            var error = new DbError("ORA-00904", ServerText) { Number = 904, Offset = Statement.Text.IndexOf("zz", StringComparison.Ordinal), Line = 1, Column = 52 };
            ExecutionState.For(Bus).SetError(Statement, error, Tab, Profile.Id);
            Inline = new FixErrorInline(new FixErrorCommand(TabsFake), TabsFake, Masker);
        }

        public ConnectionProfile Profile { get; }

        public Guid Tab { get; }

        public Tabs TabsFake { get; } = new();

        public SqlStatement Statement { get; }

        public EventBus Bus { get; } = new();

        public SqlLiteralMasker Masker { get; } = new();

        public FixErrorInline Inline { get; }

        public FakeDriver Driver { get; } = new();

        public List<TextEdit> Proposed { get; } = [];

        public bool AcceptPreview { get; set; } = true;

        public List<int> Estimates { get; } = [];

        public AiOneShotRun Run(AiSettings? settings, int tokens)
        {
            Estimates.Add(tokens);
            return new(new AiOneShotSetup(Driver, AiKitRoute.For(Profile, settings ?? new AiSettings { Light = new AiLightSettings { Model = "haiku" } }, FixErrorCommand.Instance.Tier, tokens), FixErrorInline.Feature));
        }

        public Task<AiStripPreparation> PrepareAsync(AiSettings? settings = null)
        {
            var main = AiKitRoute.For(Profile, settings ?? new AiSettings(), FixErrorCommand.Instance.Tier);
            var env = new AiCommandStartEnv(new Services(Bus), new NoEditor(), Profile, main.Context, AiStatementSource.Found, null, Tab, main.MaskRequired);
            return Inline.PrepareAsync(env, tokens => Run(settings, tokens), AiJoinSyntax.Oracle, CancellationToken.None);
        }

        public AiInlineStripViewModel Strip() => AiSuggestionBar.Create(FixErrorInline.Title, async (_, _) => await PrepareAsync(), (accept, ct) => Inline.AcceptAsync(accept, (edit, _) =>
        {
            Proposed.Add(edit);
            return Task.FromResult(AcceptPreview);
        }, ct));
    }

    private static string Answer(string sql) => "## 원인\n**ORA-00904 · 열 이름 오타**\n\n## 고친 SQL\n```sql\n" + sql + "\n```";

    [Fact]
    public void Explain_sql_and_fix_error_are_light_and_the_rest_stay_main()
    {
        Assert.Equal(AiTier.Light, ExplainSqlCommand.Instance.Tier);
        Assert.Equal(AiTier.Light, FixErrorCommand.Instance.Tier);
        Assert.Equal(AiTier.Main, ExplainPlanCommand.Instance.Tier);
    }

    [Fact]
    public async Task Fix_error_goes_light_without_tools_with_the_error_and_statement_in_the_bundle()
    {
        var f = new Fixture();
        f.Driver.Answer = _ => KitFakes.Answer(Answer("SELECT * FROM tb_order WHERE stat_cd = 'SECRET' AND qty > 0"));
        var strip = f.Strip();

        await strip.RunAsync(CancellationToken.None);

        var request = Assert.Single(f.Driver.Requests);
        Assert.Equal(AiTier.Light, request.Tier);
        Assert.Null(request.OutputSchema); // 도구·구조 결과 없는 한 번짜리
        Assert.False(request.MaskingRequired);
        Assert.Contains("You have no tools", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("## 고친 SQL", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("<context name=\"error\">", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("invalid identifier", request.Prompt, StringComparison.Ordinal); // 가리지 않는 대화는 서버 글도 보낸다
        Assert.Contains("'SECRET'", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(AiStripState.Done, strip.State);
        Assert.Equal("haiku", strip.ModelLabel);
        Assert.True(strip.ShowsAccept);
    }

    [Fact]
    public async Task A_statement_too_big_for_the_light_model_goes_to_the_main_model_before_masking_is_decided()
    {
        var f = new Fixture(ConnectionEnvironment.Production);
        var id = Guid.NewGuid();
        var local = new AiLlmProfile { Id = id, Name = "로컬", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://192.0.2.10:11434/v1", Model = "m", MaxInputTokens = 2_100, LocalAiConfirmedUrl = "http://192.0.2.10:11434/v1" };
        var settings = new AiSettings { Model = "opus", Profiles = [local], Light = new AiLightSettings { ProfileId = id } };

        var preparation = await f.PrepareAsync(settings); // 문장 + 지시 몫이 2,100 의 70% 를 넘는다

        var route = preparation.Request!.Run.Route;
        Assert.True(f.Estimates.Single() > 2_100 * 0.7);
        Assert.Equal(AiTier.Main, route.Tier);
        Assert.Equal(AiTier.Light, route.RequestedTier);
        Assert.True(route.Escalated);
        Assert.True(route.MaskRequired); // 올려 간 주 모델(유료)의 규칙으로 가린다
        Assert.DoesNotContain("SECRET", preparation.Request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masked_connection_sends_no_server_text_no_literals_and_a_masked_position()
    {
        var f = new Fixture(ConnectionEnvironment.Production);

        var preparation = await f.PrepareAsync();

        var request = preparation.Request!;
        var prompt = request.Prompt;
        Assert.DoesNotContain("SECRET", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid identifier", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1, column 52", prompt, StringComparison.Ordinal);
        Assert.Contains("ORA-00904", prompt, StringComparison.Ordinal);
        var masked = request.Bundle.Items.Single(i => i.Key == "statement").Text;
        var at = masked.IndexOf("zz", StringComparison.Ordinal);
        Assert.Contains($"character {at} (0-based)", prompt, StringComparison.Ordinal); // 가린 글 기준 위치
        Assert.NotNull(request.Bundle.Vault);
        Assert.DoesNotContain("SECRET", request.Bundle.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusals_come_from_the_same_target_rules()
    {
        var f = new Fixture();
        ExecutionState.For(f.Bus).ClearError();

        var preparation = await f.PrepareAsync();

        Assert.Equal(FixErrorCommand.NoErrorText, preparation.RefusalText);
        var strip = f.Strip();
        await strip.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Refused, strip.State);
        Assert.Empty(f.Driver.Requests);
    }

    [Fact]
    public async Task Accept_proposes_the_fix_in_place_with_the_reason_and_restored_values()
    {
        var f = new Fixture(ConnectionEnvironment.Production);
        f.Driver.Answer = r =>
        {
            var masked = r.Prompt[r.Prompt.IndexOf("SELECT * FROM", StringComparison.Ordinal)..];
            masked = masked[..masked.IndexOf('\n', StringComparison.Ordinal)].Replace("zz", "0", StringComparison.Ordinal);
            return KitFakes.Answer(Answer(masked + ";"));
        };
        var strip = f.Strip();
        await strip.RunAsync(CancellationToken.None);

        await strip.AcceptCommand.ExecuteAsync(null);

        var edit = Assert.Single(f.Proposed);
        Assert.Equal(f.Statement.Range, edit.Range);
        Assert.Equal("SELECT * FROM tb_order WHERE stat_cd = 'SECRET' AND qty > 0", edit.NewText); // 가린 값 되돌림 · 구분자 맞춤
        Assert.Equal("AI 오류 고치기 — ORA-00904 · 열 이름 오타", edit.Description);
        Assert.Equal(AiInlineStripViewModel.AcceptedText, strip.StatusText);
        Assert.False(strip.ShowsAccept);
        Assert.Equal(Script, f.TabsFake.Texts[f.Tab]); // 막대는 글을 직접 바꾸지 않는다(미리보기가 적용한다)
    }

    [Fact]
    public async Task Accept_refuses_when_the_tab_or_statement_moved_or_nothing_changed()
    {
        var f = new Fixture();
        f.Driver.Answer = _ => KitFakes.Answer(Answer("SELECT * FROM tb_order WHERE stat_cd = 'SECRET' AND qty > 0"));
        var strip = f.Strip();
        await strip.RunAsync(CancellationToken.None);

        f.TabsFake.Active = Guid.NewGuid();
        await strip.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(FixErrorInline.OtherTabText, strip.StatusText);

        f.TabsFake.Active = f.Tab;
        f.TabsFake.Texts[f.Tab] = Script.Replace("zz", "yy", StringComparison.Ordinal);
        await strip.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(FixErrorInline.ChangedText, strip.StatusText);
        Assert.Empty(f.Proposed);

        f.TabsFake.Texts[f.Tab] = Script;
        f.AcceptPreview = false;
        await strip.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(FixErrorInline.RejectedText, strip.StatusText);
        Assert.True(strip.ShowsAccept); // 거절했으면 다시 받아들일 수 있다

        var same = new Fixture();
        same.Driver.Answer = _ => KitFakes.Answer(Answer(same.Statement.Text));
        var sameStrip = same.Strip();
        await sameStrip.RunAsync(CancellationToken.None);
        await sameStrip.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(FixErrorInline.SameText, sameStrip.StatusText);
    }

    [Fact]
    public async Task Unknown_placeholder_or_a_missing_sql_block_is_not_inserted()
    {
        var f = new Fixture(ConnectionEnvironment.Production);
        f.Driver.Answer = _ => KitFakes.Answer(Answer("SELECT * FROM tb_order WHERE stat_cd = :v9 AND qty > 0"));
        var strip = f.Strip();
        await strip.RunAsync(CancellationToken.None);
        await strip.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(FixErrorInline.UnknownPlaceholderText, strip.StatusText);
        Assert.Empty(f.Proposed);

        Assert.Null(FixErrorInline.ExtractSql("  "));
        Assert.Equal("SELECT 1 FROM dual", FixErrorInline.ExtractSql("```sql\nSELECT 1 FROM dual\n```"));
        Assert.Equal("SELECT 2 FROM dual", FixErrorInline.ExtractSql("SELECT 2 FROM dual"));
    }

    [Fact]
    public async Task Discard_hides_the_bar_and_a_broken_answer_cannot_be_accepted()
    {
        var f = new Fixture();
        f.Driver.Answer = _ => KitFakes.Answer("고칠 수 없습니다");
        var strip = f.Strip();
        await strip.RunAsync(CancellationToken.None);

        Assert.False(strip.ShowsAccept); // 틀이 깨졌으면 넣을 글이 없다
        Assert.True(strip.ShowsRawText);
        strip.DiscardCommand.Execute(null);
        Assert.False(strip.IsVisible);
    }

    [Fact]
    public Task The_command_opens_the_bar_under_the_sql_editor_not_the_chat() => HeadlessHost.Run(async () =>
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), Script);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.AiBar);

        await FixErrorInlineLauncher.RunAsync(new Services(new EventBus()), vm, CancellationToken.None); // 보이는 접속이 없다

        var bar = Assert.IsType<AiInlineStripViewModel>(vm.AiBar);
        Assert.Equal(FixErrorInline.Title, bar.Title);
        Assert.Equal(AiStripState.Refused, bar.State);
        Assert.Equal(FixErrorInline.UnavailableText, bar.StatusText);
        Dispatcher.UIThread.RunJobs();
        var strip = view.GetVisualDescendants().OfType<AiInlineStrip>().Single();
        Assert.True(strip.IsVisible);
        Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StripRetry").IsVisible);
        window.Close();
    });
}
