using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// 문서 도구 막대(design.md 1장, MB-03): 명령 레지스트리 중 <c>toolbar:mybatis/…@n</c> 자리를 가진 명령을 순서대로 아이콘 단추로 그린다.
/// 실행은 주 도구 막대와 같이 <see cref="ICommandRegistry.ExecuteAsync"/> 한 길이다(<see cref="RegistryCommand"/>).
/// 단추의 활성 여부는 <see cref="ICommandRegistry.CanExecute"/> 로 정하고, 문맥 키가 바뀌면 다시 본다.
/// 화면에 붙어 있는 동안만 레지스트리·문맥 키·언어 변경을 구독한다(문장 탭 View 는 탭마다 하나씩 오래 산다).
/// </summary>
public sealed class MyBatisDocumentToolbar : Border
{
    /// <summary>레지스트리에서 이 막대로 모으는 자리 이름(<c>toolbar:mybatis/…</c>).</summary>
    internal const string Surface = "mybatis";

    /// <summary>저장 단추 툴팁에 보이는 단축키는 일반 저장 명령의 키다(MyBatis 저장은 자기 키가 없다, design.md 4.4).</summary>
    private const string SaveCommandId = "oranda.mybatis.save";
    private const string SaveShortcutCommandId = "oranda.file.save";

    private readonly ICommandRegistry _registry;
    private readonly ContextKeyService _keys;
    private readonly IShellText _text;
    private readonly KeymapService _keymap;
    private readonly StackPanel _panel = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
    private readonly List<RegistryCommand> _commands = [];

    public MyBatisDocumentToolbar(ICommandRegistry registry, ContextKeyService keys, IShellText text, KeymapService keymap)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keymap);
        _registry = registry;
        _keys = keys;
        _text = text;
        _keymap = keymap;
        Child = _panel; // 띠 모양(panel-toolbar)은 분할 단추와 함께 놓는 탭 머리 줄이 그린다(TSK-03-10).
        Rebuild();
    }

    /// <summary>지금 그려진 단추들(시험·계측용).</summary>
    public IReadOnlyList<Button> Buttons => [.. _panel.Children.OfType<Button>()];

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keys.Changed += OnKeysChanged;
        _registry.CommandsChanged += OnCommandsChanged;
        _text.LanguageChanged += OnCommandsChanged;
        Rebuild(); // 그동안 바뀐 키맵·언어를 툴팁에 반영한다.
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _keys.Changed -= OnKeysChanged;
        _registry.CommandsChanged -= OnCommandsChanged;
        _text.LanguageChanged -= OnCommandsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeysChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RaiseCanExecuteChanged);

    private void OnCommandsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void RaiseCanExecuteChanged()
    {
        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        _commands.Clear();
        AutomationProperties.SetName(this, UiText.Get("MyBatis.editor.toolbar", "MyBatis 문서 도구 모음"));

        var items = new List<(Placement Placement, CommandDefinition Command)>();
        foreach (var command in _registry.Commands)
        {
            foreach (var raw in command.Placements)
            {
                if (Placement.TryParse(raw, out var placement) && placement is { Surface: "toolbar" } && placement.Path[0] == Surface)
                {
                    items.Add((placement, command));
                }
            }
        }

        int? lastGroup = null;
        foreach (var (placement, command) in items.OrderBy(i => i.Placement.Order).ThenBy(i => i.Command.Id, StringComparer.Ordinal))
        {
            if (lastGroup is { } group && group != placement.Group)
            {
                _panel.Children.Add(new Separator { Classes = { "toolbar" } });
            }

            lastGroup = placement.Group;
            _panel.Children.Add(CreateButton(command));
        }

        RaiseCanExecuteChanged();
    }

    private Button CreateButton(CommandDefinition definition)
    {
        var title = _text.Title(definition);
        var shortcut = _keymap.ShortcutText(definition.Id == SaveCommandId ? SaveShortcutCommandId : definition.Id);
        var command = new RegistryCommand(definition.Id, _registry, _keys);
        _commands.Add(command);

        // 아이콘이 있으면 아이콘만 그리고 이름은 툴팁으로 보인다(주 도구 막대와 같다). 없으면 글자 단추.
        var icon = ShellIcons.Create(definition.Icon);
        var button = new Button
        {
            Content = icon ?? (object)title,
            Command = command,
            Classes = { "toolbar", icon is null ? "toolbar-text" : "toolbar-icon" },
            Tag = definition.Id,
        };
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, shortcut is null ? title : $"{title} ({KeyBindingTable.ForScreen(shortcut)})");
        return button;
    }
}
