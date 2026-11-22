using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Wizard;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Features;

namespace Oranda.App.Features.AiSetup.Choose;

/// <summary>방법 고르기 단계 등록.</summary>
[OrandaService(typeof(IAiSetupStepFactory))]
public sealed class ChooseStepFactory : IAiSetupStepFactory
{
    public AiSetupStepId StepId => AiSetupStepId.Choose;

    public IAiSetupStep Create(AiSetupContext context) => new ChooseStep(context);
}

/// <summary>
/// 방법 고르기: 카드 3개(회사 Claude 계정 = 추천·회사 AI 서버·잘 모르겠어요). 카드를 고르면 갈래가 정해지고 끝남.
/// 들어올 때 claude 를 찾아(화면 스레드 밖) 있으면 A 카드에 「이 PC 에 설치됨」, 지금 설정이 이미 쓸 수 있으면 위에 「이미 연결되어 있어요」.
/// 조회만 쓰는 Role 이거나 AI 기능이 꺼져 있으면 카드 대신 안내와 기능 설정을 여는 단추를 보인다.
/// </summary>
public sealed class ChooseStep : AiSetupStepBase
{
    /// <summary>고른 카드에 붙이는 클래스.</summary>
    internal const string SelectedClass = "ai-setup-card-selected";

    private readonly Func<Task> _openRoles;
    private readonly Dictionary<AiSetupPath, Button> _cards = [];
    private bool _claudeInstalled;
    private bool _alreadyConnected;
    private int _findGeneration;

    /// <param name="openRoles">「주 업무 바꾸기」·「기능 설정 열기」(시험용). 기본 = Role·기능 고르기 명령.</param>
    public ChooseStep(AiSetupContext context, Func<Task>? openRoles = null)
        : base(context)
    {
        _openRoles = openRoles ?? (() => ShellCommandRunner.RunAsync(FeatureCommandIds.Roles));
        IsComplete = !Blocked && context.ChosenPath is not null;
    }

    public override AiSetupStepId StepId => AiSetupStepId.Choose;

    /// <summary>조회만 쓰는 Role 이다.</summary>
    public bool IsReadOnlyRole => FeatureCatalog.IsReadOnly(Context.Settings.Current.Features);

    /// <summary>AI 기능(aiTuning)이 꺼져 있다(조회만 Role 포함).</summary>
    public bool IsAiOff => !FeatureCatalog.Resolve(Context.Settings.Current.Features).Contains(FeatureIds.AiTuning);

    /// <summary>카드 대신 안내를 보인다.</summary>
    public bool Blocked => IsReadOnlyRole || IsAiOff;

    /// <summary>카드를 보인다.</summary>
    public bool ShowsCards => !Blocked;

    /// <summary>안내 제목.</summary>
    public string BlockedHeadline => IsReadOnlyRole ? ChooseText.ReadOnlyHeadline(English) : ChooseText.AiOffHeadline(English);

    /// <summary>안내 설명.</summary>
    public string BlockedDetail => IsReadOnlyRole ? ChooseText.ReadOnlyDetail(English) : ChooseText.AiOffDetail(English);

    /// <summary>안내 단추 글.</summary>
    public string BlockedAction => IsReadOnlyRole ? ChooseText.ChangeRole(English) : ChooseText.OpenFeatures(English);

    /// <summary>이 PC 에서 claude 를 찾았다(A 카드 배지).</summary>
    public bool ClaudeInstalled
    {
        get => _claudeInstalled;
        private set => Set(ref _claudeInstalled, value);
    }

    /// <summary>지금 설정으로 AI 를 이미 쓸 수 있다(위 한 줄).</summary>
    public bool AlreadyConnected
    {
        get => _alreadyConnected;
        private set => Set(ref _alreadyConnected, value);
    }

    /// <summary>고른 갈래.</summary>
    public AiSetupPath? Selected => Context.ChosenPath;

    /// <summary>카드를 고른다(단추와 같은 길).</summary>
    public void Select(AiSetupPath path)
    {
        if (Blocked)
        {
            return;
        }

        Context.ChosenPath = path;
        IsComplete = true;
        OnPropertyChanged(nameof(Selected));
        MarkSelected();
    }

    /// <summary>안내 단추(기능 설정 열기). 돌아오면 Role·기능을 다시 읽어 화면을 바꾼다.</summary>
    public async Task OpenRolesAsync()
    {
        try
        {
            await _openRoles().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 명령 실패는 명령 실행기가 기록한다.
        }

        if (!IsDisposed)
        {
            Reevaluate();
        }
    }

    /// <summary>배지·「이미 연결됨」 찾기(시험이 기다린다). 들어올 때마다 새로 시작한다.</summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>들어오면 끝남을 다시 정하고, 배지 찾기는 뒤에서 한다([다음]·카드 고르기를 막지 않게).</summary>
    public override Task EnterAsync(CancellationToken ct)
    {
        Reevaluate();
        return Task.CompletedTask;
    }

    public override Task LeaveAsync(CancellationToken ct)
    {
        _findGeneration++;
        return Task.CompletedTask;
    }

    /// <summary>Role·기능을 다시 읽어 안내·카드·끝남을 바꾸고, 카드가 보이면 배지 찾기를 다시 한다.</summary>
    private void Reevaluate()
    {
        IsComplete = !Blocked && Context.ChosenPath is not null;
        foreach (var name in new[] { nameof(IsReadOnlyRole), nameof(IsAiOff), nameof(Blocked), nameof(ShowsCards), nameof(BlockedHeadline), nameof(BlockedDetail), nameof(BlockedAction) })
        {
            OnPropertyChanged(name);
        }

        MarkSelected();
        Refreshing = Blocked ? Task.CompletedTask : RefreshAsync(++_findGeneration);
    }

    private async Task RefreshAsync(int generation)
    {
        ClaudeInstallation? found = null;
        var loggedIn = true;
        if (Context.Claude is { } claude)
        {
            try
            {
                found = await Task.Run(() => claude.FindAsync(CancellationToken.None)).ConfigureAwait(true);
                if (found is not null && AiActiveProfile.Find(Context.Settings.Current.Ai, AiProfilePresets.SystemClisFor(Context.Platform)).Kind == AiActiveKind.ClaudeCode)
                {
                    // Claude Code 가 대상이면 로그인까지 되어 있어야 「이미 연결됨」.
                    var status = await Task.Run(() => claude.GetLoginStatusAsync(CancellationToken.None)).ConfigureAwait(true);
                    loggedIn = status.LoggedIn || !status.StatusSupported;
                }
            }
            catch (Exception)
            {
                // 찾기 실패 = 배지 없음.
            }
        }

        if (IsDisposed || generation != _findGeneration)
        {
            return;
        }

        ClaudeInstalled = found is not null;
        AlreadyConnected = IsUsable(Context.Settings.Current.Ai, found is not null && loggedIn, Context.Platform);
    }

    /// <summary>지금 설정이 쓸 수 있는 대상인가: 프로필·기타 CLI·이 PC 에서 쓸 수 있는 시스템 CLI, 또는 Claude Code 이고 claude 가 있음.</summary>
    internal static bool IsUsable(Oranda.Core.Settings.AiSettings ai, bool claudeFound, AiHostPlatform platform) =>
        AiActiveProfile.Find(ai, AiProfilePresets.SystemClisFor(platform)).Kind switch
        {
            AiActiveKind.Profile or AiActiveKind.CustomCli or AiActiveKind.SystemCli => true,
            AiActiveKind.ClaudeCode => claudeFound,
            _ => false,
        };

    protected override Control BuildView()
    {
        var connected = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = ChooseText.AlreadyConnected(English), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
        };
        connected.Bind(Border.BorderBrushProperty, connected.GetResourceObservable("OrandaSuccessForegroundBrush"));
        connected.Bind(Visual.IsVisibleProperty, new Binding(nameof(AlreadyConnected)));

        var cards = new StackPanel { Spacing = 8 };
        foreach (var path in new[] { AiSetupPath.ClaudeAccount, AiSetupPath.CompanyServer, AiSetupPath.NotSure })
        {
            var card = Card(path);
            _cards[path] = card;
            cards.Children.Add(card);
        }

        var cardsBlock = new StackPanel
        {
            Spacing = 12,
            Children = { AiSetupUi.Text(ChooseText.Headline(English)), AiSetupUi.Caption(ChooseText.Intro(English)), connected, cards },
        };
        ((TextBlock)cardsBlock.Children[0]).FontWeight = FontWeight.SemiBold;
        cardsBlock.Bind(Visual.IsVisibleProperty, new Binding(nameof(ShowsCards)));

        var action = AiSetupUi.Button(BlockedAction, accent: true);
        action.Bind(ContentControl.ContentProperty, new Binding(nameof(BlockedAction)));
        action.Click += async (_, _) => await OpenRolesAsync().ConfigureAwait(true);
        var blocked = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Bound(new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, nameof(BlockedHeadline)),
                Bound(AiSetupUi.Caption(string.Empty), nameof(BlockedDetail)),
                action,
            },
        };
        blocked.Bind(Visual.IsVisibleProperty, new Binding(nameof(Blocked)));
        BlockedButton = action;
        MarkSelected();
        return new StackPanel { Spacing = 12, Children = { cardsBlock, blocked } };
    }

    /// <summary>안내 단추(시험용). 화면을 만든 뒤에만 있다.</summary>
    internal Button? BlockedButton { get; private set; }

    /// <summary>카드 단추(시험용).</summary>
    internal IReadOnlyDictionary<AiSetupPath, Button> Cards => _cards;

    private Button Card(AiSetupPath path)
    {
        var name = new TextBlock { Text = AiSetupText.PathTitle(path, English), FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        var when = AiSetupUi.Caption(ChooseText.When(path, English));
        var texts = new StackPanel { Spacing = 2, Children = { name, when } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        grid.Children.Add(texts);
        if (path == AiSetupPath.ClaudeAccount)
        {
            var badges = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Badge(ChooseText.Recommended(English), "OrandaButtonAccentBrush", null) } };
            badges.Children.Add(Badge(ChooseText.Installed(English), "OrandaSuccessForegroundBrush", nameof(ClaudeInstalled)));
            Grid.SetColumn(badges, 2);
            grid.Children.Add(badges);
        }

        var card = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10),
            BorderThickness = new Thickness(1.5),
        };
        Avalonia.Automation.AutomationProperties.SetName(card, AiSetupText.PathTitle(path, English));
        card.Click += (_, _) => Select(path);
        return card;
    }

    private static TextBlock Bound(TextBlock block, string path)
    {
        block.Bind(TextBlock.TextProperty, new Binding(path));
        return block;
    }

    private static Border Badge(string text, string brushKey, string? visiblePath)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold };
        var badge = new Border { Padding = new Thickness(8, 1), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = label, HorizontalAlignment = HorizontalAlignment.Right };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(brushKey));
        badge.Bind(Border.BorderBrushProperty, badge.GetResourceObservable(brushKey));
        if (visiblePath is not null)
        {
            badge.Bind(Visual.IsVisibleProperty, new Binding(visiblePath));
        }

        return badge;
    }

    private void MarkSelected()
    {
        foreach (var (path, card) in _cards)
        {
            var selected = Context.ChosenPath == path;
            card.Classes.Set(SelectedClass, selected);
            if (selected)
            {
                card.Bind(Button.BorderBrushProperty, card.GetResourceObservable("OrandaButtonAccentBrush"));
            }
            else
            {
                card.ClearValue(Button.BorderBrushProperty);
            }
        }
    }
}
