using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ConnectionImport;

/// <summary>TSK-13-13 접속 내보내기 명령 ID.</summary>
public static class ConnectionExportCommands
{
    /// <summary>이 파일이 등록하는 명령 ID 전체.</summary>
    public static readonly string[] Ids = ["oranda.conn.export"];
}

/// <summary>
/// 접속 내보내기·Oranda 파일 가져오기 흐름(TSK-13-13). 고르기 창과 저장 경로는 함수로 받아 시험에서 바꿔 끼운다.
/// </summary>
public static class ConnectionTransfer
{
    /// <summary>
    /// 저장된 접속을 고르기 창에 보이고, 고른 것만 저장 경로에 쓴다. 쓴 개수, 접속이 없으면 0(창을 띄우지 않는다), 취소면 null.
    /// </summary>
    public static async Task<int?> ExportAsync(
        IConnectionService service, Func<ConnectionPickViewModel, Task<bool>> pick, Func<Task<string?>> savePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(savePath);
        var profiles = (await service.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
        if (profiles.Count == 0)
        {
            return 0;
        }

        var model = ConnectionPickViewModel.ForExport(profiles);
        if (!await pick(model).ConfigureAwait(true) || model.Chosen.Count == 0 || await savePath().ConfigureAwait(true) is not { } path)
        {
            return null;
        }

        var chosen = model.Chosen;
        await File.WriteAllTextAsync(path, ConnectionExport.Write(chosen, DateTimeOffset.UtcNow), ct).ConfigureAwait(true);
        return chosen.Count;
    }

    /// <summary>
    /// Oranda 내보내기 파일 본문을 읽어 고르기 창(이미 있음 끔)을 보이고 고른 것을 새 Id 로 저장한다. 취소면 null.
    /// 형식이 틀리면 <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<ImportSummary?> ImportFileAsync(
        IConnectionService service, string json, Func<ConnectionPickViewModel, Task<bool>> pick, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pick);
        var incoming = ConnectionExport.Read(json);
        var existing = (await service.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
        var model = ConnectionPickViewModel.ForImport(incoming, existing);
        if (!await pick(model).ConfigureAwait(true))
        {
            return null;
        }

        var items = model.Items.Where(i => i.IsChecked && i.CanCheck).Select(i => new ImportedConnection(ConnectionExport.Strip(i.Profile)));
        return await ConnectionImportCommands.SaveAsync(service, items, ct).ConfigureAwait(true);
    }

    /// <summary>「접속 n개를 내보냈다: 경로」.</summary>
    public static string ExportedMessage(int count, string path) =>
        UiText.Get("Connection.transfer.exported", "접속 {0}개를 내보냈다: {1}")
            .Replace("{0}", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{1}", path, StringComparison.Ordinal);
}

/// <summary>「파일 &gt; 접속 내보내기」: 접속 체크 목록 → 저장 대화상자 → 쓰기 → 결과 알림. 접속 관리자 「내보내기」 단추도 같은 흐름.</summary>
[OrandaCommand("oranda.conn.export", CommandCategory.Connection, Placements = ["menu:file/connExport@460"])]
public sealed class ConnectionExportHandler(ServiceContainer container) : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (container.GetService<ShellController>()?.MainWindow is { } owner)
        {
            await RunAsync(container, owner, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>내보내기를 돌리고 결과를 <paramref name="owner"/> 위에 알린다. 하나라도 썼으면 true. 접속 관리자에서는 그 창을 주인으로 넘긴다.</summary>
    public static async Task<bool> RunAsync(ServiceContainer container, Window owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(owner);
        var service = ConnectionManagerCommands.Resolve(container);
        string? path = null;
        string message;
        int? written;
        try
        {
            written = await ConnectionTransfer.ExportAsync(
                service,
                model => ConnectionPickDialog.ShowAsync(owner, model),
                async () => path = await PickSavePathAsync(owner).ConfigureAwait(true),
                cancellationToken).ConfigureAwait(true);
            if (written is null)
            {
                return false;
            }

            message = written == 0
                ? UiText.Get("Connection.transfer.nothing", "내보낼 접속이 없다.")
                : ConnectionTransfer.ExportedMessage(written.Value, path ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            written = 0;
            message = UiText.Get("Connection.transfer.writeFailed", "접속 파일을 쓰지 못했다: ") + ex.Message;
        }

        await ConnectionDialogs.MessageAsync(owner, message).ConfigureAwait(true);
        return written > 0;
    }

    private static async Task<string?> PickSavePathAsync(Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiText.Get("Connection.transfer.exportTitle", "접속 내보내기"),
            SuggestedFileName = "oranda-connections.json",
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(UiText.Get("Connection.transfer.fileType", "Oranda 접속 파일")) { Patterns = ["*.json"] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }
}

/// <summary>Oranda 내보내기 파일인지 보고 가져오기 흐름을 고른다(SQL Developer 도 .json 을 쓴다).</summary>
internal static class OrandaFileSniffer
{
    /// <summary>.json 이고 본문이 Oranda 내보내기 파일이면 본문, 아니면 null.</summary>
    public static async Task<string?> ReadIfExportAsync(string path, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(true);
        return ConnectionExport.LooksLikeExport(text) ? text : null;
    }
}
