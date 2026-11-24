using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Access;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 설정 「AI」 탭 화면(<see cref="AiSettingsHubPage"/>): 맨 위 「연결 상태」 카드, 「AI 를 쓸 접속」, 「외부 에이전트」, 접힌 「고급」(예전 「AI 모델」 쪽 <see cref="AiModelSettingsView"/>).
/// 글은 모두 뷰모델이 한/영으로 만든다(코드로 만든 화면이라 글자 표·리터럴 번역을 거치지 않는다).
/// </summary>
public sealed class AiSettingsHubView : UserControl
{
    private AiSettingsHubPage? _page;

    public AiSettingsHubView()
    {
        Content = Build();
        DataContextChanged += (_, _) => Attach();
    }

    /// <summary>카드(시험용).</summary>
    internal Border StatusCard { get; private set; } = null!;

    internal Button StartWizardButton { get; private set; } = null!;

    internal Button TestButton { get; private set; } = null!;

    internal Button ReconnectButton { get; private set; } = null!;

    internal Button BlockedButton { get; private set; } = null!;

    internal Expander Advanced { get; private set; } = null!;

    internal Button AgentConnectButton { get; private set; } = null!;

    internal Button SkillInstallButton { get; private set; } = null!;

    internal ContentControl AccessHost { get; private set; } = null!;

    private void Attach()
    {
        _page = DataContext as AiSettingsHubPage;
        if (_page is not null)
        {
            _page.Owner = () => TopLevel.GetTopLevel(this) as Window;
            AccessHost.Content = _page.Access is { } access ? new AccessView(access) : null;
            _ = _page.LoadAccessAsync();
        }
        else
        {
            AccessHost.Content = null;
        }
    }

    private StackPanel Build()
    {
        // ---- 연결 상태 카드 ----
        var caption = Caption(nameof(AiSettingsHubPage.StatusTitle));
        var headline = new TextBlock { Name = "AiHubHeadline", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        headline.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.Headline)));
        var detail = new TextBlock { Name = "AiHubDetail", TextWrapping = TextWrapping.Wrap };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.Detail)));
        var detailWarn = new TextBlock { TextWrapping = TextWrapping.Wrap };
        detailWarn.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.Detail)));
        detailWarn.Classes.Add("warning-text");
        // 실패한 시험·쓸 수 없는 대상은 경고색 줄로, 그 밖은 보통 줄로 보인다.
        var warn = new MultiBinding
        {
            Converter = Avalonia.Data.Converters.BoolConverters.Or,
            Bindings = { new Binding(nameof(AiSettingsHubPage.LastTestFailed)), new Binding(nameof(AiSettingsHubPage.IsProblem)) },
        };
        detailWarn.Bind(IsVisibleProperty, warn);
        detail.Bind(IsVisibleProperty, new MultiBinding
        {
            Converter = Avalonia.Data.Converters.BoolConverters.And,
            Bindings =
            {
                new Binding(nameof(AiSettingsHubPage.LastTestFailed)) { Converter = Avalonia.Data.Converters.BoolConverters.Not },
                new Binding(nameof(AiSettingsHubPage.IsProblem)) { Converter = Avalonia.Data.Converters.BoolConverters.Not },
            },
        });

        StartWizardButton = CommandButton(nameof(AiSettingsHubPage.StartWizardText), nameof(AiSettingsHubPage.StartWizardCommand), accent: true);
        StartWizardButton.Padding = new Thickness(18, 9);
        StartWizardButton.FontSize = 15;
        StartWizardButton.Name = "AiHubStartWizard";
        StartWizardButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsNotConnected)));

        TestButton = CommandButton(nameof(AiSettingsHubPage.TestText), nameof(AiSettingsHubPage.TestCommand), accent: true);
        TestButton.Name = "AiHubTest";
        TestButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsConnected)));
        ReconnectButton = CommandButton(nameof(AiSettingsHubPage.ReconnectText), nameof(AiSettingsHubPage.StartWizardCommand), accent: false);
        ReconnectButton.Name = "AiHubReconnect";
        ReconnectButton.Bind(IsVisibleProperty, new MultiBinding
        {
            Converter = Avalonia.Data.Converters.BoolConverters.Or,
            Bindings = { new Binding(nameof(AiSettingsHubPage.IsConnected)), new Binding(nameof(AiSettingsHubPage.IsProblem)) },
        });
        BlockedButton = CommandButton(nameof(AiSettingsHubPage.BlockedActionText), nameof(AiSettingsHubPage.ResolveBlockedCommand), accent: true);
        BlockedButton.Name = "AiHubBlockedAction";
        BlockedButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsBlocked)));

        var unavailable = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        unavailable.Classes.Add("dim");
        unavailable.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.TestUnavailableText)));
        unavailable.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.ShowTestUnavailable)));

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6, Children = { StartWizardButton, TestButton, ReconnectButton, BlockedButton } };
        StatusCard = new Border
        {
            Name = "AiHubStatusCard",
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14),
            Child = new StackPanel { Spacing = 8, Children = { caption, headline, detail, detailWarn, unavailable, buttons } },
        };
        StatusCard.Bind(Border.BorderBrushProperty, StatusCard.GetResourceObservable("OrandaButtonAccentBrush"));
        StatusCard.Bind(Border.BackgroundProperty, StatusCard.GetResourceObservable("OrandaPanelHeaderBrush"));

        // ---- AI 를 쓸 접속 ----
        AccessHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var accessHint = Small(nameof(AiSettingsHubPage.AccessHint));
        var access = Group(new StackPanel { Spacing = 6, Children = { AccessHost, accessHint } });
        access.Name = "AiHubAccess";
        access.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.HasAccess)));

        // ---- 외부 에이전트 ----
        var agentTitle = Heading(nameof(AiSettingsHubPage.AgentTitle));
        var agentHint = Small(nameof(AiSettingsHubPage.AgentHint));
        var agentStatus = new TextBlock { Name = "AiHubAgentStatus", TextWrapping = TextWrapping.Wrap };
        agentStatus.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.AgentStatusText)));
        AgentConnectButton = CommandButton(nameof(AiSettingsHubPage.AgentConnectText), nameof(AiSettingsHubPage.OpenAgentConnectCommand), accent: false);
        AgentConnectButton.Name = "AiHubAgentConnect";
        SkillInstallButton = CommandButton(nameof(AiSettingsHubPage.SkillInstallText), nameof(AiSettingsHubPage.OpenSkillInstallCommand), accent: false);
        SkillInstallButton.Name = "AiHubSkillInstall";
        var agent = Group(new StackPanel
        {
            Spacing = 6,
            Children = { agentTitle, agentHint, agentStatus, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { AgentConnectButton, SkillInstallButton } } },
        });
        agent.Name = "AiHubAgent";

        // ---- 고급(처음엔 접힘) ----
        var advancedHeader = new StackPanel { Spacing = 2 };
        var advancedTitle = new TextBlock { FontWeight = FontWeight.SemiBold };
        advancedTitle.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.AdvancedTitle)));
        advancedHeader.Children.Add(advancedTitle);
        advancedHeader.Children.Add(Small(nameof(AiSettingsHubPage.AdvancedHint)));
        var model = new AiModelSettingsView { Margin = new Thickness(0, 8, 0, 0) };
        model.Bind(DataContextProperty, new Binding(nameof(AiSettingsHubPage.Model)));
        Advanced = new Expander
        {
            Name = "AiHubAdvanced",
            Header = advancedHeader,
            Content = model,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Advanced.Bind(Expander.IsExpandedProperty, new Binding(nameof(AiSettingsHubPage.AdvancedExpanded)) { Mode = BindingMode.TwoWay });

        return new StackPanel { Spacing = 14, Children = { StatusCard, access, agent, Advanced } };
    }

    private static Button CommandButton(string textProperty, string commandProperty, bool accent)
    {
        var button = new Button();
        if (accent)
        {
            button.Classes.Add("accent");
        }

        button.Bind(ContentProperty, new Binding(textProperty));
        button.Bind(Button.CommandProperty, new Binding(commandProperty));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(textProperty));
        return button;
    }

    private static Border Group(Control child)
    {
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = child };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaSeparatorBrush"));
        return border;
    }

    private static TextBlock Heading(string property)
    {
        var block = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }

    private static TextBlock Caption(string property)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("panel-caption");
        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }

    private static TextBlock Small(string property)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.Classes.Add("dim");
        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }
}
