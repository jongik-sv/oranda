using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Shell;
using Oranda.Platform.Import;

namespace Oranda.App.Features.ConnectionImport;

/// <summary>
/// TSK-03-05 접속 가져오기·명령줄 접속. 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다(docs/design/ui-contract.md).
/// 명령 ID 는 commands.md 7장 「소유 Task = TSK-03-05」 행과 같다.
/// </summary>
public static class ConnectionImportCommands
{
    /// <summary>이 파일이 등록하는 명령 ID 전체.</summary>
    public static readonly string[] Ids = ["oranda.conn.import"];

    /// <summary>
    /// 가져온 접속을 저장한다. 비밀번호는 저장하지 않는다(<c>Secret</c> 도 버린다). 같은 이름이 이미 있으면 「이름 (2)」 로 바꿔 덧붙인다.
    /// </summary>
    public static async Task<ImportSummary> SaveAsync(IConnectionService service, IEnumerable<ImportedConnection> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(items);
        var existing = (await service.LoadAsync(ct).ConfigureAwait(false)).Value.Profiles;
        var names = new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var warnings = new List<string>();
        foreach (var item in items)
        {
            var name = UniqueName(item.Profile.Name, names);
            names.Add(name);
            var profile = item.Profile with { Name = name, Id = Guid.Empty, CredentialRef = null };
            await service.SaveProfileAsync(profile, null, false, ct).ConfigureAwait(false);
            added++;
            if (item.Warning is { Length: > 0 } warning && !warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }

        return new ImportSummary(added, warnings);
    }

    /// <summary>겹치지 않는 이름. 겹치면 「이름 (2)」, 「이름 (3)」 …</summary>
    public static string UniqueName(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 명령줄 <c>oranda user@alias [as sysdba]</c> 처리: 해당 프로필을 찾거나(없으면 비밀번호 없이 새로 저장) 접속 관리자를 그 프로필을 고른 채로 연다.
    /// 인자에 접속 대상이 없으면 아무것도 하지 않고 false. 앱 진입점(Program/App)이 시작 마무리 뒤에 부른다.
    /// </summary>
    public static async Task<bool> OpenFromCommandLineAsync(ServiceContainer container, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (CommandLineConnect.Parse(args) is not { } target)
        {
            return false;
        }

        var service = ConnectionManagerCommands.Resolve(container);
        var profiles = (await service.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
        var profile = CommandLineConnect.FindProfile(profiles, target);
        if (profile is null)
        {
            profile = await service.SaveProfileAsync(CommandLineConnect.ToProfile(target), null, false, ct).ConfigureAwait(true);
        }

        var vm = new ConnectionManagerViewModel(service);
        await vm.LoadAsync(ct).ConfigureAwait(true);
        vm.Selected = Flatten(vm.Tree).FirstOrDefault(n => n.Profile?.Id == profile.Id);
        var window = new ConnectionManagerWindow { DataContext = vm };
        var owner = container.GetService<ShellController>()?.MainWindow;
        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        return true;
    }

    private static IEnumerable<ConnectionTreeNode> Flatten(IEnumerable<ConnectionTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }
}

/// <summary>가져오기 결과 요약.</summary>
public sealed record ImportSummary(int Added, IReadOnlyList<string> Warnings)
{
    public string Message => Added == 0
        ? "가져올 접속이 없다."
        : $"접속 {Added}개를 가져왔다." + (Warnings.Count == 0 ? string.Empty : " " + string.Join(" ", Warnings));
}

/// <summary>「파일 &gt; 접속 가져오기」: 원본 형식별 파일 선택 → 가져오기 → 접속 관리자 열기.</summary>
[OrandaCommand("oranda.conn.import", CommandCategory.Connection, Placements = ["menu:file/connImport@450"])]
public sealed class ConnectionImportHandler(ServiceContainer container) : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var owner = container.GetService<ShellController>()?.MainWindow;
        if (owner is null)
        {
            return;
        }

        var importers = (container.GetService<IEnumerable<IConnectionImporter>>() ?? []).ToList();
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "접속 가져오기",
            AllowMultiple = false,
            FileTypeFilter =
            [
                .. importers.Select(i => new FilePickerFileType(i.SourceName) { Patterns = [.. i.FileExtensions.Select(e => "*" + e)] }),
            ],
        }).ConfigureAwait(true);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        var ext = System.IO.Path.GetExtension(path);
        var importer = importers.FirstOrDefault(i => i.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
        if (importer is null)
        {
            await ConnectionDialogs.MessageAsync(owner, "지원하지 않는 파일 형식이다: " + ext).ConfigureAwait(true);
            return;
        }

        string message;
        try
        {
            var items = await importer.ImportAsync(path, cancellationToken).ConfigureAwait(true);
            var service = ConnectionManagerCommands.Resolve(container);
            message = (await ConnectionImportCommands.SaveAsync(service, items, cancellationToken).ConfigureAwait(true)).Message;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            message = $"{importer.SourceName} 파일을 가져오지 못했다: {ex.Message}";
        }

        await ConnectionDialogs.MessageAsync(owner, message).ConfigureAwait(true);
    }
}
