using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>명령 정의와 그 처리기 형식.</summary>
public sealed record CommandRegistration(CommandDefinition Definition, Type HandlerType);

/// <summary>
/// 어셈블리 스캔으로 모은 셸 기여(명령·도구 창·상태 표시줄 구간).
/// 기능 Task 는 특성만 달면 되고 이 파일이나 셸 파일에 줄을 더하지 않는다.
/// </summary>
public sealed record ShellContributions(
    IReadOnlyList<CommandRegistration> Commands,
    IReadOnlyList<ToolPanelDefinition> Panels,
    IReadOnlyList<StatusBarItemDefinition> StatusBarItems)
{
    /// <summary>
    /// 주어진 어셈블리에서 <see cref="OrandaCommandAttribute"/>·<see cref="OrandaToolPanelAttribute"/>·
    /// <see cref="OrandaStatusBarItemAttribute"/> 를 단 공개·내부 클래스를 모은다.
    /// 특성을 단 형식이 계약 인터페이스를 구현하지 않으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public static ShellContributions Scan(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var commands = new List<CommandRegistration>();
        var panels = new List<ToolPanelDefinition>();
        var items = new List<StatusBarItemDefinition>();

        foreach (var type in assemblies.Distinct().SelectMany(a => a.GetTypes()))
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            foreach (var a in type.GetCustomAttributes<OrandaCommandAttribute>(inherit: false))
            {
                Require(type, typeof(ICommandHandler), a.Id);
                commands.Add(new CommandRegistration(ToDefinition(a), type));
            }

            if (type.GetCustomAttribute<OrandaToolPanelAttribute>(inherit: false) is { } p)
            {
                panels.Add(ToDefinition(p, type));
            }

            if (type.GetCustomAttribute<OrandaStatusBarItemAttribute>(inherit: false) is { } s)
            {
                Require(type, typeof(IStatusBarItem), s.Id);
                items.Add(new StatusBarItemDefinition(s.Id, s.Order, s.CollapseRank, type));
            }
        }

        return new ShellContributions(commands, panels, items);
    }

    public static CommandDefinition ToDefinition(OrandaCommandAttribute a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new CommandDefinition
        {
            Id = a.Id,
            Title = a.Title ?? "Cmd." + a.Id,
            Category = a.Category,
            Icon = a.Icon,
            When = a.When,
            Enablement = a.Enablement,
            Placements = a.Placements ?? [],
            Aliases = a.Aliases ?? [],
            ShowInPalette = a.ShowInPalette,
        };
    }

    public static ToolPanelDefinition ToDefinition(OrandaToolPanelAttribute a, Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new ToolPanelDefinition
        {
            Id = a.Id,
            Title = a.Title ?? "Panel." + a.Id,
            Kind = a.Kind,
            DefaultLocation = a.DefaultLocation,
            ViewModelType = viewModelType,
            DefaultSize = a.DefaultSize > 0 ? a.DefaultSize : null,
            AllowMultiple = a.AllowMultiple,
            Order = a.Order,
            Icon = a.Icon,
            DevBuildOnly = a.DevBuildOnly,
        };
    }

    private static void Require(Type type, Type contract, string id)
    {
        if (!contract.IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{type.FullName} 에 '{id}' 특성이 있지만 {contract.Name} 를 구현하지 않는다.");
        }
    }
}
