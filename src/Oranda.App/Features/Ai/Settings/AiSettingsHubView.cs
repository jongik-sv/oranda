using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Access;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 설정 「AI」 탭 화면(<see cref="AiSettingsHubPage"/>, 확인된 시안): 연결 전·막힘이면 맨 위 큰 칸, 그 아래 번호 배지 붙은 세 묶음
/// ① 「AI 가 볼 수 있는 접속」(<see cref="AccessTableView"/>, 도우미 단계와 같은 컨트롤) ② 「Oranda 안의 AI」 ③ 「밖의 AI 에이전트」.
/// 묶음은 <see cref="Expander"/> 라 연결 전에는 한 줄로 접힌다. 글은 모두 뷰모델이 한/영으로 만든다(코드로 만든 화면).
/// </summary>
public sealed class AiSettingsHubView : UserControl
{
    private AiSettingsHubPage? _page;

    public AiSettingsHubView()
    {
        Content = Build();
        DataContextChanged += (_, _) => Attach();
    }

    /// <summary>연결 전·막힘의 맨 위 큰 칸(시험용).</summary>
    internal Border StartBox { get; private set; } = null!;

    internal Button StartWizardButton { get; private set; } = null!;

    internal Button BlockedButton { get; private set; } = null!;

    internal Expander AccessGroup { get; private set; } = null!;

    internal Expander InsideGroup { get; private set; } = null!;

    internal Expander OutsideGroup { get; private set; } = null!;

    internal Button TestButton { get; private set; } = null!;

    internal Button ReconnectButton { get; private set; } = null!;

    internal Expander Advanced { get; private set; } = null!;

    internal Button AgentConnectButton { get; private set; } = null!;

    internal Button SkillInstallButton { get; private set; } = null!;

    internal ContentControl AccessHost { get; private set; } = null!;

    internal TextBlock InsideSummary { get; private set; } = null!;

    internal TextBlock OutsideSummary { get; private set; } = null!;

    private void Attach()
    {
        _page = DataContext as AiSettingsHubPage;
        if (_page is not null)
        {
            _page.Owner = () => TopLevel.GetTopLevel(this) as Window;
            _page.ConfirmDiscard = async message =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner)
                {
                    return true;
                }

                var english = Oranda.App.Features.AiSetup.Contract.AiSetupText.CurrentIsEnglish;
                return await AiConfirmDialog.ShowAsync(owner, message, english ? "Discard" : "버리기", english ? "Keep" : "그대로 두기").ConfigureAwait(true);
            };
            AccessHost.Content = _page.Access is { } access ? new AccessTableView(access) : null;
            _ = _page.LoadAccessAsync();
            _page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AiSettingsHubPage.RevealedSection) && _page?.RevealedSection is { } section)
                {
                    BringSectionIntoView(section);
                }
            };
            if (_page.RevealedSection is { } pending)
            {
                BringSectionIntoView(pending);
            }
        }
        else
        {
            AccessHost.Content = null;
        }
    }

    /// <summary>묶음을 화면에 들인다(펼친 뒤 배치가 끝나면).</summary>
    private void BringSectionIntoView(AiSettingsSection section)
    {
        var target = section switch
        {
            AiSettingsSection.Access => AccessGroup,
            AiSettingsSection.Inside => InsideGroup,
            _ => OutsideGroup,
        };
        Avalonia.Threading.Dispatcher.UIThread.Post(() => target.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private StackPanel Build()
    {
        // ---- 맨 위 큰 칸(연결 전·막힘) ----
        var headline = new TextBlock { Name = "AiHubHeadline", FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        headline.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.Headline)));
        var detail = Plain(nameof(AiSettingsHubPage.Detail));
        StartWizardButton = CommandButton(nameof(AiSettingsHubPage.StartWizardText), nameof(AiSettingsHubPage.StartWizardCommand), accent: true);
        StartWizardButton.Name = "AiHubStartWizard";
        StartWizardButton.Padding = new Thickness(20, 10);
        StartWizardButton.FontSize = 15;
        StartWizardButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsNotConnected)));
        BlockedButton = CommandButton(nameof(AiSettingsHubPage.BlockedActionText), nameof(AiSettingsHubPage.ResolveBlockedCommand), accent: true);
        BlockedButton.Name = "AiHubBlockedAction";
        BlockedButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsBlocked)));
        StartBox = new Border
        {
            Name = "AiHubStartBox",
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16),
            Child = new StackPanel { Spacing = 10, Children = { headline, detail, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { StartWizardButton, BlockedButton } } } },
        };
        StartBox.Bind(Border.BorderBrushProperty, StartBox.GetResourceObservable("OrandaButtonAccentBrush"));
        StartBox.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.ShowsStartBox)));

        // ---- ① AI 가 볼 수 있는 접속 ----
        AccessHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AccessGroup = Group("AiHubAccess", 1, nameof(AiSettingsHubPage.AccessTitle), nameof(AiSettingsHubPage.AccessCollapsedHint), nameof(AiSettingsHubPage.AccessExpanded),
            new StackPanel { Spacing = 8, Children = { Plain(nameof(AiSettingsHubPage.AccessNote)), Help("AiHubAccessHelp", nameof(AiSettingsHubPage.AccessHelp)), AccessHost } });
        AccessGroup.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.HasAccess)));

        // ---- ② Oranda 안의 AI ----
        var pill = Pill(nameof(AiSettingsHubPage.InsideStatusText));
        // 마지막 시험 줄: 실패·쓸 수 없음이면 경고색 줄, 그 밖은 보통 줄.
        var warn = new MultiBinding
        {
            Converter = BoolConverters.Or,
            Bindings = { new Binding(nameof(AiSettingsHubPage.LastTestFailed)), new Binding(nameof(AiSettingsHubPage.IsProblem)) },
        };
        var lastTestOk = Plain(nameof(AiSettingsHubPage.Detail));
        lastTestOk.Name = "AiHubDetail";
        lastTestOk.Bind(IsVisibleProperty, new MultiBinding
        {
            Converter = BoolConverters.And,
            Bindings =
            {
                new Binding(nameof(AiSettingsHubPage.LastTestFailed)) { Converter = BoolConverters.Not },
                new Binding(nameof(AiSettingsHubPage.IsProblem)) { Converter = BoolConverters.Not },
            },
        });
        var lastTestWarn = Plain(nameof(AiSettingsHubPage.Detail));
        lastTestWarn.Name = "AiHubDetailWarning";
        lastTestWarn.Classes.Add("warning-text");
        lastTestWarn.Bind(IsVisibleProperty, warn);
        var lastTest = new StackPanel { Children = { lastTestOk, lastTestWarn } };
        TestButton = CommandButton(nameof(AiSettingsHubPage.TestText), nameof(AiSettingsHubPage.TestCommand), accent: true);
        TestButton.Name = "AiHubTest";
        TestButton.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.IsConnected)));
        ReconnectButton = CommandButton(nameof(AiSettingsHubPage.ReconnectText), nameof(AiSettingsHubPage.StartWizardCommand), accent: false);
        ReconnectButton.Name = "AiHubReconnect";
        var statusBlock = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                pill,
                lastTest,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { TestButton, ReconnectButton } },
                Help("AiHubInsideHelp", nameof(AiSettingsHubPage.InsideHelp)),
            },
        };
        statusBlock.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.ShowsInsideStatus)));
        InsideSummary = new TextBlock { Name = "AiHubInsideSummary", TextWrapping = TextWrapping.Wrap };
        InsideSummary.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.InsideSummary)));

        var advancedTitle = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        advancedTitle.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.AdvancedTitle)));
        var model = new AiModelSettingsView();
        model.Bind(DataContextProperty, new Binding(nameof(AiSettingsHubPage.Model)));
        var advancedHint = Help("AiHubAdvancedHint", nameof(AiSettingsHubPage.AdvancedHint));
        Advanced = new Expander
        {
            Name = "AiHubAdvanced",
            Header = advancedTitle,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { advancedHint, model } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Advanced.Bind(Expander.IsExpandedProperty, new Binding(nameof(AiSettingsHubPage.AdvancedExpanded)) { Mode = BindingMode.TwoWay });
        InsideGroup = Group("AiHubInside", 2, nameof(AiSettingsHubPage.InsideTitle), nameof(AiSettingsHubPage.InsideCollapsedHint), nameof(AiSettingsHubPage.InsideExpanded),
            new StackPanel { Spacing = 10, Children = { Plain(nameof(AiSettingsHubPage.InsideSubtitle), dim: true), statusBlock, GreyBand(InsideSummary), Advanced } });

        // ---- ③ 밖의 AI 에이전트 ----
        var agentStatus = Plain(nameof(AiSettingsHubPage.AgentStatusText));
        agentStatus.Name = "AiHubAgentStatus";
        var skillStatus = Plain(nameof(AiSettingsHubPage.SkillStatusText));
        skillStatus.Name = "AiHubSkillStatus";
        skillStatus.Bind(IsVisibleProperty, new Binding(nameof(AiSettingsHubPage.HasSkillStatus)));
        SkillInstallButton = CommandButton(nameof(AiSettingsHubPage.SkillInstallText), nameof(AiSettingsHubPage.OpenSkillInstallCommand), accent: false);
        SkillInstallButton.Name = "AiHubSkillInstall";
        AgentConnectButton = CommandButton(nameof(AiSettingsHubPage.AgentConnectText), nameof(AiSettingsHubPage.OpenAgentConnectCommand), accent: false);
        AgentConnectButton.Name = "AiHubAgentConnect";
        OutsideSummary = new TextBlock { Name = "AiHubOutsideSummary", TextWrapping = TextWrapping.Wrap };
        OutsideSummary.Bind(TextBlock.TextProperty, new Binding(nameof(AiSettingsHubPage.OutsideSummary)));
        var outsideNote = Plain(nameof(AiSettingsHubPage.OutsideNote), dim: true);
        outsideNote.Name = "AiHubOutsideNote";
        OutsideGroup = Group("AiHubOutside", 3, nameof(AiSettingsHubPage.OutsideTitle), nameof(AiSettingsHubPage.OutsideCollapsedHint), nameof(AiSettingsHubPage.OutsideExpanded),
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Plain(nameof(AiSettingsHubPage.OutsideSubtitle), dim: true),
                    Help("AiHubOutsideExample", nameof(AiSettingsHubPage.OutsideExample)),
                    agentStatus,
                    skillStatus,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { SkillInstallButton, AgentConnectButton } },
                    Help("AiHubOutsideHelp", nameof(AiSettingsHubPage.OutsideHelp)),
                    GreyBand(OutsideSummary),
                    outsideNote,
                },
            });

        var intro = Plain(nameof(AiSettingsHubPage.IntroText));
        intro.Name = "AiHubIntro";
        intro.LineHeight = 20;

        return new StackPanel { Spacing = 14, Children = { intro, StartBox, AccessGroup, InsideGroup, OutsideGroup } };
    }

    /// <summary>단추·표 옆의 쉬운 설명 줄(조금 작은 흐린 글, 줄 간격 넉넉히).</summary>
    private static TextBlock Help(string name, string property)
    {
        var block = Plain(property, dim: true);
        block.Name = name;
        block.LineHeight = 18;
        return block;
    }

    /// <summary>번호 배지 붙은 묶음. 접히면 머리에 한 줄 안내가 보인다.</summary>
    private static Expander Group(string name, int number, string titleProperty, string collapsedHintProperty, string expandedProperty, Control content)
    {
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("OrandaButtonAccentBrush"));
        var title = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.TextProperty, new Binding(titleProperty));
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        hint.Classes.Add("dim");
        hint.Bind(TextBlock.TextProperty, new Binding(collapsedHintProperty));
        hint.Bind(IsVisibleProperty, new Binding(expandedProperty) { Converter = BoolConverters.Not });
        var text = new StackPanel { Spacing = 2, Children = { title, hint } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        head.Children.Add(badge);
        Grid.SetColumn(text, 1);
        head.Children.Add(text);

        var expander = new Expander
        {
            Name = name,
            Header = head,
            Content = new Border { Padding = new Thickness(4, 6, 4, 4), Child = content },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Bind(Expander.IsExpandedProperty, new Binding(expandedProperty) { Mode = BindingMode.TwoWay });
        Avalonia.Automation.AutomationProperties.SetName(expander, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return expander;
    }

    private static Border GreyBand(TextBlock text)
    {
        var band = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6), Child = text, Classes = { "ai-grey-band" } };
        band.Bind(Border.BackgroundProperty, band.GetResourceObservable("OrandaPanelHeaderBrush"));
        return band;
    }

    private static Border Pill(string property)
    {
        var text = new TextBlock { FontWeight = FontWeight.SemiBold };
        text.Bind(TextBlock.TextProperty, new Binding(property));
        var pill = new Border { Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = text, Name = "AiHubStatusPill" };
        pill.Bind(Border.BorderBrushProperty, pill.GetResourceObservable("OrandaButtonAccentBrush"));
        pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable("OrandaTableSelectionBrush"));
        return pill;
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

    private static TextBlock Plain(string property, bool dim = false)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (dim)
        {
            block.Classes.Add("dim");
            block.FontSize = 12;
        }

        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }
}
