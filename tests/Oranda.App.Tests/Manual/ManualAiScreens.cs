using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Ai;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 AI 장 화면 캡처 생성기. DB·모델·CLI 를 부르지 않고 메모리 안 설정과 지어낸 값만 쓴다.
/// 연결 테스트·모델 새로고침·보내기는 누르지 않고, 기타 CLI 의 PATH 확인은 시험 함수로 답한다. AI 대화의 답은 <see cref="ExampleEvents"/> 가 낸다.
/// 번호의 뜻은 <c>site/src/content/docs/ai/models.md</c>, <c>chat.md</c>, <c>overview.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualAiScreens
{
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static AiModelSettingsPage SettingsPage(AiSettings ai) => new(
        ai,
        new AiProfileServices(new SettingsService(new MemorySettingsStore()), CustomCliInstalled: name => name == "examplecli"),
        installCheckDelay: TimeSpan.Zero);

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        return window;
    }

    private static AiSettings ExampleSettings() => new()
    {
        Profiles = [ProfileSamples.Example(), ProfileSamples.Ollama()],
        CustomClis = [new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "examplecli -p {prompt_file}" }],
    };

    [Fact]
    public Task Model_settings() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var view = new AiModelSettingsView { DataContext = SettingsPage(ExampleSettings()), Margin = new Thickness(24) };
        var window = Show(view, 760, 830);
        var overlay = ManualCapture.AddOverlay(window);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "ai-model-settings",
        [
            new(ManualCapture.FindByText(view, "사용 모델"), 1, 72),
            new(Named<Button>(view, "ProfileBox"), 2, 300, 18),
            new(Named<ComboBox>(view, "ModelBox"), 3, 130, 16),
            new(Named<CheckBox>(view, "MaskLiteralsCheck"), 4),
            new(Named<StackPanel>(view, "JoinSyntaxPanel"), 5, 150),
            new(Named<StackPanel>(view, "BenchmarkTimeoutPanel"), 6, 300, 14),
            new(Named<Border>(view, "CustomCliPanel"), 7),
        ]);
        window.Close();
    });

    [Fact]
    public Task Model_dropdown() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var page = SettingsPage(ExampleSettings());
        var view = new AiModelSettingsView { DataContext = page, Margin = new Thickness(24) };
        var window = Show(view, 760, 640);
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        page.ToggleDropdownCommand.Execute(null);
        Pump();
        window.UpdateLayout();
        Pump();
        Assert.True(page.IsDropdownOpen);

        // 펼친 목록은 창 내용 위에 따로 그려지므로 번호 표시 겹침판을 목록 안에 얹는다.
        var list = (Border)window.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "ProfilePopup").Child!;
        var content = list.Child!;
        list.Child = null;
        var overlay = new Canvas { IsHitTestVisible = false };
        var host = new Grid();
        host.Children.Add(content);
        host.Children.Add(overlay);
        list.Child = host;
        Pump();
        window.UpdateLayout();
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "ai-model-dropdown",
        [
            new(ManualCapture.FindByText(list, "시스템 CLI"), 1, 74),
            new(ManualCapture.FindByText(list, "기타 CLI"), 2, 62),
            new(ManualCapture.FindByText(list, "내 프로필"), 3, 62),
            new(ManualCapture.Find<Button>(list, b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "새 프로필 만들기…"), 4, 170, 16),
        ]);
        window.Close();
    });

    [Fact]
    public Task Profile_manager() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var selected = Guid.NewGuid();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example(selected), ProfileSamples.Ollama()));
        var model = harness.Manager(select: selected);
        await model.Ready;
        var window = new AiProfileManagerWindow { DataContext = model };
        window.Show();
        Pump();
        window.UpdateLayout();
        var overlay = ManualCapture.AddOverlay(window);
        await model.SaveCommand.ExecuteAsync(null); // 메모리 안 설정에만 쓴다. 결과 띠를 보이려는 것이고 서버는 부르지 않는다.
        Assert.True(model.HasResult, "결과 띠가 나오지 않았다.");
        Assert.Equal(0, harness.Probe.Calls);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "ai-profile-manager",
        [
            new(Named<ItemsControl>(window, "ProfileList"), 1, 190, 10),
            new(Named<Button>(window, "NewProfileButton"), 2, 180, 14),
            new(Named<ItemsControl>(window, "PresetList"), 3, -8),
            new(Named<TextBox>(window, "BaseUrlBox"), 4, 670, 14),
            new(Named<Button>(window, "RefreshModelsButton"), 5, -10),
            new(Named<Button>(window, "TestButton"), 5, -10),
            new(Named<StackPanel>(window, "ResultBand"), 6, 160, 8),
            new(Named<Button>(window, "CancelButton"), 7, -10),
        ]);
        window.Close();
    });

    [Fact]
    public Task Custom_cli_rows() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var page = SettingsPage(new AiSettings
        {
            SystemCliId = AiCustomCliTarget.SelectionOf("c1"),
            CustomClis =
            [
                new AiCustomCli { Id = "c1", Name = "Example CLI", Command = "examplecli --yolo -p {prompt}" },
                new AiCustomCli { Id = "c2", Name = "Second CLI", Command = "secondcli --print {prompt_file}" },
            ],
        });
        var view = new AiModelSettingsView { DataContext = page, Margin = new Thickness(24) };
        var window = Show(view, 760, 640);

        // 이 그림은 기타 CLI 영역만 보인다.
        foreach (var other in ((StackPanel)view.Content!).Children.Where(c => c.Name != "CustomCliPanel"))
        {
            other.IsVisible = false;
        }

        await HeadlessHost.WaitUntil(() => page.CustomClis.All(r => r.IsFound || r.IsNotFound));
        var overlay = ManualCapture.AddOverlay(window);
        Pump();

        var firstName = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "CustomCliNameBox");
        var found = ManualCapture.FindByText(window, "이 앱의 PATH 기준으로 찾았습니다");
        var warning = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("warning-text") && t.Text == page.CustomClis[0].Warnings[0]);
        ManualCapture.SaveOverlaid(window, overlay, "ai-custom-cli",
        [
            new(firstName, 1, 500, 16),
            new(found, 2, 215),
            new(warning, 3, 450, 8),
            new(window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "UseCustomCliButton"), 4, -12),
        ]);
        window.Close();
    });

    [Fact]
    public Task Chat() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var vm = new AiChatViewModel { SettingsProvider = () => new AiSettings() };
        vm.StartConversation("튜닝 · 지역별 주문 합계");
        vm.BeginTurn("이 문장이 느린 이유를 찾고 고쳐 줘");
        await vm.RunAsync(ExampleEvents(), CancellationToken.None);
        vm.AddProposal(
            "재작성 제안 · 날짜 조건 고침", "TRUNC 를 없애 ORDERS_DATE_IX 를 쓰게 합니다.", false, () => Task.FromResult<bool?>(null), "경과 -62%");
        var view = new AiChatView { DataContext = vm };
        var window = Show(view, 560, 780);
        var overlay = ManualCapture.AddOverlay(window);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "ai-chat",
        [
            new(Named<TextBlock>(view, "TitleText"), 1, 190, 8),
            new(Named<ScrollViewer>(view, "ConversationScroll"), 2, 270, 340),
            new(Named<TextBlock>(view, "ToolGroupSummary"), 3, 150, 8),
            new(Named<TextBlock>(view, "ProposalLabel"), 4, 190, 8),
            new(Named<TextBox>(view, "DraftBox"), 5, 510, 16),
            new(Named<Button>(view, "AttachButton"), 6, 44, 12),
        ]);
        window.Close();
    });

    /// <summary>가짜 응답 생성기: 도구 카드 셋과 짧은 답. 어느 모델도 부르지 않는다.</summary>
    private static async IAsyncEnumerable<AiEvent> ExampleEvents()
    {
        yield return new AiStarted("example");
        foreach (var (id, tool, size) in new[] { ("c1", "get_cursor_plan", 1400), ("c2", "get_index_info", 620), ("c3", "get_table_stats", 880) })
        {
            yield return new AiToolCall(id, tool, "{\"object\":\"ORDERS\"}");
            yield return new AiToolResult(id, tool, size, AiToolJudgement.Allowed);
            await Task.Yield();
        }

        yield return new AiTextDelta("`TRUNC(o.order_date)` 때문에 `ORDERS_DATE_IX` 를 쓰지 못해 **표 전체를 읽습니다**. 날짜 범위 조건으로 바꾸면 인덱스를 쓸 수 있습니다.");
        yield return new AiFinal(null, null, "example");
    }

    private static AiImageSendNotice ExampleNotice() =>
        new("prod-example", ConnectionEnvironment.Production, "예제 LLM", "https://llm.example", 2, 1_880_000, true);

    private static void SaveWarning(Window window, string name)
    {
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        ManualCapture.Save(window, name,
        [
            new(Named<TextBlock>(window, "MessageText"), 1, 430, -22),
            new(Named<TextBlock>(window, "RecipientText"), 2, 230, 6),
            new(Named<TextBlock>(window, "ConnectionText"), 3, 110, 6),
            new(Named<Button>(window, "ConfirmButton"), 4, -12),
        ]);
        window.Close();
    }

    [Fact]
    public Task Text_warning() => HeadlessHost.Run(() =>
    {
        if (ManualCapture.Enabled)
        {
            SaveWarning(new AiTextWarningDialog { DataContext = new AiImageWarningViewModel(ExampleNotice()) }, "ai-text-warning");
        }
    });

    [Fact]
    public Task Image_warning() => HeadlessHost.Run(() =>
    {
        if (ManualCapture.Enabled)
        {
            SaveWarning(new AiImageWarningDialog { DataContext = new AiImageWarningViewModel(ExampleNotice()) }, "ai-image-warning");
        }
    });

    [Fact]
    public Task Access_level() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        // 「끔」 으로 저장된 운영 접속에서 수준을 올린 상태라 접속 이름 확인 칸이 보인다.
        var vm = new ConnectionAiAccessViewModel(
            new ConnectionProfile("prod-example", "app_read", "prod.example.com:1521/prodsvc", Environment: ConnectionEnvironment.Production) { AiAccess = AiAccessLevel.Off });
        vm.SelectLevelCommand.Execute(AiAccessLevel.Query);
        Assert.True(vm.NeedsNameConfirmation, "접속 이름 확인 칸이 나오지 않았다.");
        var view = new ConnectionAiAccessView { DataContext = vm, Margin = new Thickness(16) };
        var window = Show(view, 760, 330);
        var overlay = ManualCapture.AddOverlay(window);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "ai-access-level",
        [
            new(Named<ItemsControl>(view, "LevelList"), 1),
            new(Named<TextBlock>(view, "SendsText"), 2, 690, 6),
            new(Named<TextBlock>(view, "ProductionNote"), 3, 62, 38),
            new(Named<TextBox>(view, "ConfirmNameBox"), 4, 610, 14),
            new(Named<CheckBox>(view, "ExplainBox"), 5),
        ]);
        window.Close();
    });
}
