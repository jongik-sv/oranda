using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.RecentFiles;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Settings;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Shell;

/// <summary>
/// 문서가 하나도 없을 때 문서 영역에 보이는 시작 화면(SQL Developer 의 Start Page 자리).
/// 자주 쓰는 명령을 아이콘·단축키와 함께 늘어놓는다. 단추는 명령 레지스트리로만 실행한다.
/// 옆(좁으면 아래)에 최근 파일·최근 작업 공간 두 목록을 둔다(파일 메뉴 하위 메뉴와 같은 줄, <see cref="RecentFilesService"/>).
/// 새 설치면 맨 위에 「주로 하는 일은?」 카드(TSK-09-09, D-018), 접속 프로필이 없으면 첫 줄이 강조한 「접속 추가…」 다.
/// </summary>
public sealed class StartPage : UserControl
{
    private const string ConnectionManagerCommandId = "oranda.conn.manage";

    private static readonly string[] Commands =
    [
        "oranda.file.newSqlEditor",
        "oranda.file.newWorkspace",
        "oranda.file.newPlsqlEditor",
        ConnectionManagerCommandId,
        "oranda.file.open",
        "oranda.file.openWorkspaceFile",
        "oranda.view.commandPalette",
        "oranda.tools.settings",
    ];

    private readonly RecentFilesService? _recent;
    private readonly StackPanel _recentFiles = new() { Spacing = 2 };
    private readonly StackPanel _recentWorkspaces = new() { Spacing = 2 };
    private readonly string? _fileIcon;
    private readonly string? _workspaceIcon;
    private readonly IShellText _text;
    private readonly IFeatureGate? _features;
    private readonly SettingsService? _settings;
    private readonly IEventBus? _events;
    private readonly IConnectionStore? _connections;
    private readonly TextBlock _openHint;
    private readonly Border _roleCard;
    private readonly Border _firstRunCard = new() { IsVisible = false }; // 새 설치일 때만 안을 짓는다(대부분의 실행은 만들지 않는다)
    private CheckBox? _plsqlAddon;
    private CheckBox? _myBatisAddon;
    private readonly TextBlock _restartLine;
    private readonly Button _addConnection;
    private Button? _manageRow;
    private IDisposable? _settingsSubscription;
    private IDisposable? _sessionSubscription;
    private bool _noProfiles;

    /// <summary>
    /// <paramref name="services"/> 를 주면 기능 상태를 따른다(TSK-07-06, D-011·D-013·D-018): 새 설치면 「주로 하는 일은?」 카드, 아무것도 고른 적 없으면
    /// 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 카드를 보이고, MyBatis 기능이 꺼졌으면 아래 안내에서 매퍼를 뺀다. 접속 프로필이 0개면 첫 줄을 「접속 추가…」 로 강조하고,
    /// 첫 접속에 SQL 탭을 여는 <see cref="FirstConnectEditor"/> 를 붙인다. 주지 않으면 지금 화면 그대로(모든 기능 켬).
    /// </summary>
    public StartPage(ICommandRegistry registry, IContextKeys keys, IShellText text, Func<string, string?> shortcut, RecentFilesService? recent = null,
        IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(text);
        _recent = recent;
        _text = text;
        _features = services?.GetService(typeof(IFeatureGate)) as IFeatureGate;
        _settings = services?.GetService(typeof(SettingsService)) as SettingsService;
        _events = services?.GetService(typeof(IEventBus)) as IEventBus;
        _connections = services?.GetService(typeof(IConnectionStore)) as IConnectionStore;
        if (services is not null)
        {
            FirstConnectEditor.Install(services);
        }

        _openHint = OpenHint();
        _roleCard = RoleCard(text);
        _restartLine = new TextBlock { Opacity = 0.65, TextWrapping = TextWrapping.Wrap, MaxWidth = 712, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        _restartLine.Bind(TextBlock.FontSizeProperty, _restartLine.GetResourceObservable("OrandaFontSizeSmall"));
        _addConnection = AddConnectionButton(registry, keys, text);

        var list = new StackPanel { Spacing = 2, Width = 340 };
        list.Children.Add(_addConnection);
        var byId = new Dictionary<string, CommandDefinition>(StringComparer.Ordinal);
        foreach (var c in registry.Commands)
        {
            byId[c.Id] = c;
        }

        foreach (var id in Commands)
        {
            if (!byId.TryGetValue(id, out var def))
            {
                continue;
            }

            var row = new DockPanel { LastChildFill = true };
            var keyText = new TextBlock { Text = Keymaps.KeyBindingTable.ForScreen(shortcut(id)) ?? string.Empty, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(keyText, Avalonia.Controls.Dock.Right);
            row.Children.Add(keyText);
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    ShellIcons.Create(def.Icon ?? "palette", 18) ?? new Control(),
                    new TextBlock { Text = text.Title(def), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            var button = new Button
            {
                Content = row,
                Command = new RegistryCommand(id, registry, keys),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Classes = { "tool", "start-item" },
            };
            Avalonia.Automation.AutomationProperties.SetName(button, text.Title(def));
            list.Children.Add(button);
            if (id == ConnectionManagerCommandId)
            {
                _manageRow = button;
            }
        }

        _fileIcon = "fileSql"; // 「파일 열기」 의 폴더 아이콘이 아니라 파일로 읽히게
        _workspaceIcon = byId.GetValueOrDefault("oranda.file.openWorkspaceFile")?.Icon;
        var columns = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 32, LineSpacing = 18, Children = { list } };
        if (recent is not null)
        {
            columns.Children.Add(new StackPanel
            {
                Width = 340,
                Spacing = 4,
                Children =
                {
                    Heading(text.Lookup(RecentFilesService.FilesMenuKey)),
                    _recentFiles,
                    Heading(text.Lookup(RecentFilesService.WorkspacesMenuKey), top: 12),
                    _recentWorkspaces,
                },
            });
            FillRecent();
        }

        // 창이 아주 작아도(배율 250 %) 문서 영역 밖으로 넘치지 않게 스크롤 안에 둔다(글은 말줄임 허용).
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = Body(_firstRunCard, columns, _roleCard, _restartLine, _openHint),
        };
        ApplyFeatures();
        ApplyConnections();
    }

    /// <summary>목록·기능 상태가 바뀌면 다시 채운다. 시작 화면은 문서 영역 호스트마다 새로 생기므로 붙어 있는 동안만 듣는다.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_recent is not null)
        {
            _recent.Changed += OnRecentChanged;
            FillRecent();
        }

        if (_features is not null)
        {
            _features.Changed += OnFeaturesChanged;
        }

        _settingsSubscription ??= _events?.Subscribe<SettingsChanged>(this, s =>
        {
            if (s.Section == FeaturesSettings.SectionName)
            {
                OnFeaturesChanged(this, EventArgs.Empty);
            }
        });
        // 접속하면 프로필이 생겼다: 「접속 추가…」 강조를 다시 본다.
        _sessionSubscription ??= _events?.Subscribe<SessionOpened>(this, opened => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshConnectionsAsync()));
        ApplyFeatures();
        _ = RefreshConnectionsAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_recent is not null)
        {
            _recent.Changed -= OnRecentChanged;
        }

        if (_features is not null)
        {
            _features.Changed -= OnFeaturesChanged;
        }

        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
        _sessionSubscription?.Dispose();
        _sessionSubscription = null;
    }

    /// <summary>Role 을 고르라는 카드(새 설치의 「주로 하는 일은?」 또는 D-013 의 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」)가 보이는가(시험용).</summary>
    internal bool ShowsRoleLink => _roleCard.IsVisible || _firstRunCard.IsVisible;

    /// <summary>새 설치 카드 「주로 하는 일은?」 가 보이는가(시험용, D-018).</summary>
    internal bool ShowsFirstRunCard => _firstRunCard.IsVisible;

    /// <summary>D-013 한 줄 카드 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 가 보이는가(시험용).</summary>
    internal bool ShowsDefaultRoleCard => _roleCard.IsVisible;

    /// <summary>첫 줄이 강조한 「접속 추가…」 인가(접속 프로필 0개, 시험용).</summary>
    internal bool ShowsAddConnection => _addConnection.IsVisible;

    /// <summary>재시작 안내 줄(빈 글자 = 안 보임, 시험용).</summary>
    internal string RestartLineText => _restartLine.IsVisible ? _restartLine.Text ?? string.Empty : string.Empty;

    /// <summary>아래 안내 글(시험용).</summary>
    internal string OpenHintText => _openHint.Text ?? string.Empty;

    // 게이트·설정 알림은 스레드 보장이 없다: UI 스레드로 넘긴다.
    private void OnFeaturesChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyFeatures();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyFeatures);
        }
    }

    /// <summary>
    /// 기능 상태를 화면에 옮긴다(D-011·D-013·D-018): Role·「모든 기능」 을 고른 적 없을 때(<see cref="FeatureCatalog.IsUnchosen"/>) 새 설치면
    /// 「주로 하는 일은?」 카드, 아니면(또는 「나중에」 뒤) 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 카드. 재시작해야 반영되는 기능이 있으면 그 줄,
    /// MyBatis 기능 끔이면 매퍼를 뺀 안내. 서비스 없이 만들었으면 카드를 숨기고 지금 안내 그대로.
    /// </summary>
    private void ApplyFeatures()
    {
        var unchosen = _settings is not null && FeatureCatalog.IsUnchosen(_settings.Current.Features);
        var firstRun = unchosen && _settings!.IsFirstRun && !_settings.FirstRunCardDismissed;
        if (firstRun && _firstRunCard.Child is null)
        {
            _firstRunCard.Child = FirstRunCard(_text);
        }

        _firstRunCard.IsVisible = firstRun;
        _roleCard.IsVisible = unchosen && !firstRun;
        var pending = _features?.PendingRestart;
        _restartLine.IsVisible = pending is { Count: > 0 };
        _restartLine.Text = pending is { Count: > 0 } ? FeatureSettingsPage.RestartText(_text, pending) : string.Empty;
        _openHint.Text = _features is not null && !_features.IsEnabled(FeatureIds.MyBatis)
            ? _text.Lookup(FeatureTexts.StartMyBatisOff)
            : "MyBatis 매퍼(.xml)나 작업 공간(.md)을 열면 문장마다 탭이 생깁니다.";
    }

    /// <summary>접속 프로필 수를 다시 읽는다(접속 저장소 파일만 읽고 접속 서비스·Oracle 어셈블리는 깨우지 않는다).</summary>
    internal async Task RefreshConnectionsAsync()
    {
        if (_connections is null)
        {
            return;
        }

        try
        {
            var doc = await _connections.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            _noProfiles = doc.Value.Profiles.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("시작 화면 접속 수 읽기 실패: {0}", ex.Message);
            return;
        }

        ApplyConnections();
    }

    /// <summary>접속 프로필 0개: 첫 줄 = 강조한 「접속 추가…」, 같은 일을 하는 「접속 관리자」 줄은 숨긴다(UX-13).</summary>
    private void ApplyConnections()
    {
        _addConnection.IsVisible = _noProfiles;
        if (_manageRow is not null)
        {
            _manageRow.IsVisible = !_noProfiles;
        }
    }

    /// <summary>「접속 추가…」: 강조 단추(accent) 두 줄 — 아이콘·제목, 흐린 「접속하면 SQL 탭이 바로 열립니다」. 접속 관리자는 접속이 없으면 새 접속 상태로 열린다.</summary>
    private Button AddConnectionButton(ICommandRegistry registry, IContextKeys keys, IShellText text)
    {
        var title = text.Lookup(FeatureTexts.StartAddConnection);
        var detail = new TextBlock { Text = text.Lookup(FeatureTexts.StartAddConnectionDetail), Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.FontSizeProperty, detail.GetResourceObservable("OrandaFontSizeSmall"));
        var icon = ShellIcons.Create("connect", 18) ?? new Control();
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        var button = new Button
        {
            Content = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            detail,
                        },
                    },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Classes = { "accent" },
            IsVisible = false,
        };
        Avalonia.Automation.AutomationProperties.SetName(button, title);
        button.Click += async (_, _) =>
        {
            try
            {
                await registry.ExecuteAsync(ConnectionManagerCommandId, keys).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("시작 화면 접속 추가 중 예외: {0}", ex);
            }

            await RefreshConnectionsAsync().ConfigureAwait(true);
        };
        return button;
    }

    /// <summary>
    /// 「주로 하는 일은?」 카드(새 설치에서만, 모달 아님, D-018). 주 업무 단추 5개(설정 「기능」 탭과 같은 이름·한 줄 설명) + 덧붙임 확인란 2개 + 「나중에」.
    /// 단추 = <see cref="PrimaryWork.Apply"/> 로 저장(게이트가 바로 메뉴를 바꾼다), 「나중에」 = 설정을 그대로 저장해 파일을 만들고 D-013 한 줄 카드로 바꾼다.
    /// 모양은 D-013 카드·설정 탭 안내 띠와 같다(테마 브러시만).
    /// </summary>
    private Border FirstRunCard(IShellText text)
    {
        var title = text.Lookup(FeatureTexts.StartFirstRunTitle);
        var icon = ShellIcons.Create("info", 16) ?? new Control();
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 8, 0);
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        var detail = Small(text.Lookup(FeatureTexts.StartFirstRunDetail));

        // 칸 크기를 고정해 줄마다 높이가 같고, 넓으면 세 개·좁으면 두 개·한 개씩 감싼다.
        var choices = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, ItemWidth = 208, ItemHeight = 64 };
        foreach (var kind in PrimaryWork.Choices)
        {
            choices.Children.Add(ChoiceButton(text, kind));
        }

        _plsqlAddon = AddonBox(text.Lookup(FeatureTexts.AddonPlsql), text.Lookup(FeatureCatalog.FindRole(RoleIds.PlsqlDev)!.DescriptionKey));
        _myBatisAddon = AddonBox(text.Lookup(FeatureTexts.AddonMyBatis), text.Lookup(FeatureCatalog.FindRole(RoleIds.MyBatisDev)!.DescriptionKey));
        var addons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 20, LineSpacing = 2, Children = { _plsqlAddon, _myBatisAddon } };
        var later = new Button
        {
            Content = text.Lookup(FeatureTexts.StartLater),
            Classes = { "tool" },
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        later.Bind(ForegroundProperty, later.GetResourceObservable("OrandaFocusBrush"));
        ToolTip.SetTip(later, text.Lookup(FeatureTexts.StartLaterTip));
        Avalonia.Automation.AutomationProperties.SetName(later, text.Lookup(FeatureTexts.StartLater) + " — " + text.Lookup(FeatureTexts.StartLaterTip));
        later.Click += (_, _) => _ = LaterAsync();
        var hint = Small(text.Lookup(FeatureTexts.StartAddonHint));
        DockPanel.SetDock(later, Avalonia.Controls.Dock.Right);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 18),
            MaxWidth = 712, // 두 열(340 + 32 + 340)과 같은 폭
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                                    detail,
                                },
                            },
                            choices,
                            new StackPanel { Spacing = 2, Children = { addons, new DockPanel { LastChildFill = true, Children = { later, hint } } } },
                        },
                    },
                },
            },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("OrandaSeparatorBrush"));
        Avalonia.Automation.AutomationProperties.SetName(card, title);
        return card;
    }

    /// <summary>주 업무 단추 한 개: 이름(굵게) + 흐린 한 줄 설명. 화면 읽기 이름 = 「이름 - 설명」.</summary>
    private Button ChoiceButton(IShellText text, PrimaryWorkKind kind)
    {
        var name = text.Lookup(PrimaryWork.NameKey(kind));
        var description = PrimaryWork.DescriptionKey(kind) is { } key ? text.Lookup(key) : string.Empty;
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    Clipped(Small(description)), // 칸 높이에 맞게 두 줄까지, 전체는 툴팁
                },
            },
            Tag = kind,
        };
        Avalonia.Automation.AutomationProperties.SetName(button, $"{name} - {description}");
        ToolTip.SetTip(button, description);
        button.Click += (_, _) => _ = ChooseAsync(kind);
        return button;
    }

    private static TextBlock Clipped(TextBlock block)
    {
        block.MaxLines = 2;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    private static CheckBox AddonBox(string label, string tip)
    {
        var box = new CheckBox { Content = label, MinHeight = 0, Padding = new Thickness(6, 2, 0, 2) };
        ToolTip.SetTip(box, tip);
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        return box;
    }

    private static TextBlock Small(string value)
    {
        var block = new TextBlock { Text = value, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable("OrandaFontSizeSmall"));
        return block;
    }

    /// <summary>카드 단추: 고른 주 업무 + 덧붙임을 설정에 적는다. 카드는 「고른 적 없음」 이 풀려 바로 사라진다.</summary>
    internal async Task ChooseAsync(PrimaryWorkKind kind)
    {
        if (_settings is null)
        {
            return;
        }

        var choice = new PrimaryWorkChoice(kind, _plsqlAddon?.IsChecked == true, _myBatisAddon?.IsChecked == true);
        try
        {
            await _settings.UpdateAsync(s => s with { Features = PrimaryWork.Apply(s.Features, choice) }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("시작 화면 주 업무 저장 실패: {0}", ex);
        }

        ApplyFeatures();
    }

    /// <summary>「나중에」: 기본 Role(SQL 개발자) 그대로 설정 파일을 써서 다음 실행부터 새 설치가 아니게 하고, 이번 실행은 D-013 한 줄 카드로 바꾼다.</summary>
    internal async Task LaterAsync()
    {
        if (_settings is null)
        {
            return;
        }

        _settings.FirstRunCardDismissed = true;
        ApplyFeatures();
        try
        {
            await _settings.UpdateAsync(s => s).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("시작 화면 설정 저장 실패: {0}", ex);
        }
    }

    /// <summary>
    /// 「지금 SQL 개발자 메뉴입니다 — 바꾸기…」 카드(D-013). 아래 흐린 줄은 빠진 메뉴를 어디서 켜는지 알린다. 모달이 아니라 시작 화면 안의 카드다.
    /// 링크는 <c>oranda.tools.roles</c>(설정 창 「기능」 탭)를 실행한다. 명령이 없으면 조용히 아무 일도 안 한다(<see cref="ShellCommandRunner"/>).
    /// 모양은 설정 「기능」 탭의 안내 띠(<c>feat-banner</c>)와 같다(테마 브러시만).
    /// </summary>
    private static Border RoleCard(IShellText text)
    {
        var role = FeatureSettingsPage.DefaultRoleName(text);
        var headline = string.Format(CultureInfo.CurrentCulture, text.Lookup(FeatureTexts.StartDefaultRole), role);
        var linkText = text.Lookup(FeatureTexts.StartChangeRole);
        var link = new Button
        {
            Content = linkText,
            Classes = { "tool" },
            Padding = new Thickness(4, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Bind(ForegroundProperty, link.GetResourceObservable("OrandaFocusBrush"));
        // 「바꾸기…」 만으로는 무엇을 바꾸는지 모른다: 화면 읽기 이름에는 앞 글을 붙인다.
        Avalonia.Automation.AutomationProperties.SetName(link, headline + " — " + linkText);
        link.Click += (_, _) => _ = ShellCommandRunner.RunAsync(FeatureCommandIds.Roles);
        var icon = ShellIcons.Create("info", 16) ?? new Control();
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 8, 0);
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        var detail = new TextBlock
        {
            Text = text.Lookup(FeatureTexts.StartDefaultRoleDetail),
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };
        detail.Bind(TextBlock.FontSizeProperty, detail.GetResourceObservable("OrandaFontSizeSmall"));
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 16, 0, 0),
            MaxWidth = 712, // 두 열(340 + 32 + 340)과 같은 폭
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                Children =
                                {
                                    new TextBlock { Text = headline, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                                    new TextBlock { Text = "—", Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center },
                                    link,
                                },
                            },
                            detail,
                        },
                    },
                },
            },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("OrandaSeparatorBrush"));
        Avalonia.Automation.AutomationProperties.SetName(card, headline);
        return card;
    }

    private static TextBlock OpenHint() =>
        new()
        {
            Opacity = 0.5,
            FontSize = 11,
            Margin = new Thickness(0, 16, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, // 작은 창에서는 스크롤로 본다
            MaxWidth = 340,
        };

    private void OnRecentChanged(object? sender, EventArgs e) => FillRecent();

    private void FillRecent()
    {
        if (_recent is null)
        {
            return;
        }

        Fill(_recentFiles, _recent.MenuItems(RecentKind.File), _fileIcon);
        Fill(_recentWorkspaces, _recent.MenuItems(RecentKind.Workspace), _workspaceIcon);
    }

    /// <summary>최근 줄: 이름 + 흐린 폴더 경로(가운데 줄임), 툴팁 = 전체 경로. 비었으면 흐린 안내 한 줄.</summary>
    private static void Fill(StackPanel panel, IReadOnlyList<MenuSourceItem> lines, string? icon)
    {
        panel.Children.Clear();
        foreach (var line in lines)
        {
            if (line.Run is not { } run)
            {
                panel.Children.Add(new TextBlock { Text = line.Title, Opacity = 0.5, Margin = new Thickness(6, 2), TextTrimming = TextTrimming.CharacterEllipsis });
                continue;
            }

            var name = new TextBlock { Text = line.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
            var folder = new TextBlock { Text = line.Detail, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            // 가로 StackPanel 은 폭을 무한으로 주어 말줄임이 안 된다: 아이콘·이름은 왼쪽에 붙이고 경로가 남은 폭을 채운다.
            var iconControl = ShellIcons.Create(icon ?? "palette", 18) ?? new Control();
            iconControl.Margin = new Thickness(0, 0, 10, 0);
            name.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(iconControl, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(name, Avalonia.Controls.Dock.Left);
            var button = new Button
            {
                Content = new DockPanel { LastChildFill = true, Children = { iconControl, name, folder } },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Classes = { "tool", "start-item" },
            };
            ToolTip.SetTip(button, line.ToolTip);
            Avalonia.Automation.AutomationProperties.SetName(button, line.Title);
            button.Click += (_, _) => _ = RunAsync(run);
            panel.Children.Add(button);
        }
    }

    private static async Task RunAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("시작 화면 최근 항목 열기 중 예외: {0}", ex);
        }
    }

    private static TextBlock Heading(string title, double top = 0) =>
        new() { Text = title, FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(6, top, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };

    private static StackPanel Body(Control firstRunCard, Panel columns, Control roleRow, TextBlock restartLine, TextBlock openHint) =>
        new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Oranda", FontSize = 28, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "Oracle 개발·운영 도구", Opacity = 0.6, Margin = new Thickness(0, 0, 0, 18), TextTrimming = TextTrimming.CharacterEllipsis },
                firstRunCard,
                columns,
                roleRow,
                restartLine,
                openHint,
            },
        };

    /// <summary>
    /// 문서 영역 모델(<see cref="Docking.DockWorkspace.EmptyDocumentContent"/>)에 넣는 표식 <see cref="StartPageContent"/> 을 화면으로 바꾸는 템플릿.
    /// 호스트(문서 영역 템플릿)마다 시작 화면을 새로 만든다 — 모델에 Control 자체를 두면 템플릿이 다시 만들어질 때 옛 호스트가 붙든 같은 Control 을
    /// 새 호스트에 붙이려다 「already has a visual parent」 예외가 난다(2026-10-24).
    /// </summary>
    public static IDataTemplate HostTemplate(Func<StartPage> create) => new StartPageTemplate(create);

    // FuncDataTemplate<T> 는 형식 검사가 null 도 맞다고 본다. 그러면 메인 창 안에서 내용이 null 인 곳(값을 안 고른 콤보의 선택 칸 등)에도
    // 시작 화면이 그려진다(2026-10-24, 시나리오 0개 문장의 시나리오 콤보). 그래서 표식만 맞게 직접 판정한다.
    private sealed class StartPageTemplate(Func<StartPage> create) : IDataTemplate
    {
        public bool Match(object? data) => data is StartPageContent;

        public Control? Build(object? param) => param is StartPageContent ? create() : null;
    }
}

/// <summary>문서가 하나도 없을 때 문서 영역에 보일 시작 화면의 표식(데이터). 화면은 <see cref="StartPage.HostTemplate"/> 이 호스트마다 만든다.</summary>
public sealed class StartPageContent
{
    public static StartPageContent Instance { get; } = new();

    private StartPageContent()
    {
    }

    /// <summary>템플릿이 없는 곳(메인 창 밖 — 떠 있는 창 등)에 놓여도 클래스 이름 글자가 보이지 않게 빈 글.</summary>
    public override string ToString() => string.Empty;
}
