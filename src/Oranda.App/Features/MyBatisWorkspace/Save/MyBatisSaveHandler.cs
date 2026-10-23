using System.Globalization;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Format;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Platform.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 매퍼 저장 훅(MB-06, design.md 5.1·5.2·5.6). 바뀐 문장만 <c>Apply</c> 로 새 원문을 만들고 <see cref="IMapperFileWriter"/> 로 원자적으로 쓴다.
/// 파일 전체를 다시 직렬화하는 경로는 없다: 바꾸지 않은 부분은 Apply 가 바이트까지 그대로 둔다.
/// 쓰기 전에 디스크가 바뀌었으면 3방향 병합을 먼저 하고(<see cref="MapperWatchService.CheckNow"/>), 양쪽이 고친 문장이 남았으면 쓰지 않는다.
/// 읽기 전용·쓰기 오류는 알리고 고침을 남긴다(세션 그대로).
/// </summary>
[OrandaService(typeof(IMyBatisSaveHandler))]
public sealed class MyBatisSaveHandler : IMyBatisSaveHandler
{
    private readonly MapperWatchService _watch;
    private readonly SettingsService? _settings;
    private readonly HashSet<SqlWorkspaceViewModel> _saving = new(ReferenceEqualityComparer.Instance);

    // 매개변수 = IServiceProvider 만(시작 시험 제약, design.md 9장). Language 형은 저장할 때만 쓴다.
    public MyBatisSaveHandler(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _watch = services.GetService(typeof(MapperWatchService)) as MapperWatchService ?? new MapperWatchService();
        _settings = services.GetService(typeof(SettingsService)) as SettingsService;
        if ((services.GetService(typeof(IEnumerable<IMapperFileWriter>)) as IEnumerable<IMapperFileWriter>)?.LastOrDefault() is { } writer)
        {
            Writer = writer;
        }
    }

    /// <summary>시험용.</summary>
    internal MyBatisSaveHandler(MapperWatchService watch)
    {
        _watch = watch;
    }

    /// <summary>원자적 쓰기. 등록된 구현이 없으면 02-01 구현.</summary>
    internal IMapperFileWriter Writer { get; set; } = new MapperFileWriter();

    internal ISaveDiffPresenter Presenter { get; set; } = new SaveDiffWindowPresenter();

    /// <summary>「저장할 때마다 이 창 보기」(설정 <c>myBatis.showSaveDiffEveryTime</c>).</summary>
    internal Func<bool>? ReadShowEveryTime { get; set; }

    internal Func<bool, Task>? WriteShowEveryTime { get; set; }

    public MapperWatchService Watch => _watch;

    public async Task<bool> SaveAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.MyBatis is not null && MyBatisFormatAllCommands.IsFormatting(workspace))
        {
            // 매퍼 전체 포맷이 도는 동안 저장하면 저장 끝의 기준 다시 싣기가 막 적용한 포맷 글을 덮는다(Ctrl+S·모두 저장·닫기 확인도 이 길).
            await TellAsync(workspace, Text("MyBatis.formatAll.saveDeferred", "매퍼 전체 포맷 중이라 저장을 미뤘습니다. 포맷이 끝난 뒤 다시 저장하십시오."), ct).ConfigureAwait(true);
            return false;
        }

        if (workspace.MyBatis is null || !_saving.Add(workspace))
        {
            return false;
        }

        // 저장하는 동안(특히 「바뀌는 부분」 창이 떠 있는 동안) 감시가 세션 기준을 바꾸지 않게 미룬다.
        _watch.BeginSave(workspace);
        try
        {
            return await SaveCoreAsync(workspace, ct).ConfigureAwait(true);
        }
        finally
        {
            _saving.Remove(workspace);
            _watch.EndSave(workspace);
        }
    }

    public async Task<bool> ConfirmCloseAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.MyBatis is not { IsDirty: true } session)
        {
            return true;
        }

        switch (await workspace.Host.Dialogs.AskSaveChangesAsync(MapperWatchService.FileName(session), ct).ConfigureAwait(true))
        {
            case SaveChoice.Save:
                // 「바뀌는 부분」 창에서 일부만 저장했으면 남은 고침이 사라지지 않게 닫지 않는다.
                return await SaveAsync(workspace, ct).ConfigureAwait(true) && !session.IsDirty;
            case SaveChoice.DontSave:
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> SaveCoreAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        var session = workspace.MyBatis!;
        if (_watch.StateOf(workspace) is { Banner: MapperBannerKind.Conflict })
        {
            await TellAsync(workspace, Text("MyBatis.save.resolveFirst", "파일이 밖에서 바뀌어 양쪽이 고친 문장이 있습니다. 알림 띠에서 먼저 고르십시오."), ct).ConfigureAwait(true);
            return false;
        }

        var edits = session.PendingEdits();
        if (edits.Count == 0)
        {
            return true;
        }

        var path = session.MapperPath;
        var snap = MapperDisk.Read(path);
        string diskHash;
        bool merged = false;
        if (!snap.Exists)
        {
            if (await AskMissingTargetAsync(workspace, session, ct).ConfigureAwait(true) is not { } target)
            {
                return false;
            }

            path = target;
            diskHash = session.BaselineHash; // 일부러 다시 쓴다: 「밖에서 바뀜」 표시를 내지 않는다
        }
        else if (snap.Document is null)
        {
            await TellAsync(workspace, Format("MyBatis.save.unreadable", "{0} 을(를) 매퍼로 읽지 못해 저장하지 않았습니다: {1}", Path.GetFileName(path), snap.Error), ct).ConfigureAwait(true);
            return false;
        }
        else
        {
            if (!string.Equals(snap.Hash, session.BaselineHash, StringComparison.Ordinal))
            {
                // design.md 5.1 의 2번: 다르면 병합 먼저. 남은 고침은 새 기준 위의 편집이다.
                _watch.EnsureTracked(workspace);
                if (!_watch.CheckNow(workspace))
                {
                    await TellAsync(workspace, Text("MyBatis.save.resolveFirst", "파일이 밖에서 바뀌어 양쪽이 고친 문장이 있습니다. 알림 띠에서 먼저 고르십시오."), ct).ConfigureAwait(true);
                    return false;
                }

                edits = session.PendingEdits();
                if (edits.Count == 0)
                {
                    return true;
                }

                merged = true;
            }

            // 병합했으면 검사 상자에 「밖에서 바뀜」을 보인다(기준과 다른 값이면 ChangedOnDisk).
            diskHash = merged ? string.Empty : session.BaselineHash;
        }

        var doc = session.Document;
        IReadOnlyList<MapperSaveItem> items;
        try
        {
            items = MapperSavePlan.Build(doc, edits, MapperDisk.Parser);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            await TellAsync(workspace, Format("MyBatis.save.cannotApply", "고친 문장을 매퍼에 넣지 못해 저장하지 않았습니다: {0}", ex.Message), ct).ConfigureAwait(true);
            return false;
        }

        // 원문에서 읽은 id 를 세션이 모르면(규칙에 안 맞는 id·입력 중 빈 id) 저장 뒤 Rebase 가 그 문장을 맞대지 못한다: 쓰지 않는다.
        var unknown = items.Where(i => i.Kind != MapperSaveItemKind.Deleted && session.ElementOf(i.CurrentId) is null).Select(i => i.CurrentId).ToList();
        if (unknown.Count > 0)
        {
            await TellAsync(workspace, Format("MyBatis.save.badId", "문장 id 를 쓸 수 없습니다: {0}. id 를 고친 뒤 다시 저장하십시오.", string.Join(", ", unknown)), ct).ConfigureAwait(true);
            return false;
        }

        MapperWriteCheck CheckOf(IReadOnlyList<MapperSaveItem> chosen)
        {
            try
            {
                return doc.Check(doc.Apply([.. chosen.Select(i => i.Edit)]), diskHash);
            }
            catch (ArgumentException ex)
            {
                return new MapperWriteCheck(ex.Message, [], false);
            }
        }

        IReadOnlyList<MapperSaveItem> chosen = items;
        var check = CheckOf(items);
        bool show = ShowEveryTime();
        if (show || !check.CanWrite)
        {
            int unchanged = Math.Max(0, doc.Elements.Count - items.Count(i => i.Kind != MapperSaveItemKind.Added));
            var answer = await Presenter.ShowAsync(new SaveDiffRequest(Path.GetFileName(path), items, unchanged, CheckOf, show), ct).ConfigureAwait(true);
            if (answer is null || answer.Checked.Count == 0)
            {
                return false;
            }

            if (answer.ShowEveryTime != show)
            {
                await SetShowEveryTimeAsync(answer.ShowEveryTime).ConfigureAwait(true);
            }

            // 지금 id 가 같은 항목(지운 문장과 같은 id 의 새 문장 등)은 함께 저장한다: 세션 Rebase 는 id 로 맞대므로 하나만 쓰면 다른 쪽 고침이 사라진다.
            var ids = answer.Checked.Select(i => i.CurrentId).ToHashSet(StringComparer.Ordinal);
            chosen = [.. items.Where(i => ids.Contains(i.CurrentId))];
            check = CheckOf(chosen);
        }

        if (!check.CanWrite)
        {
            var reason = check.XmlError ?? Format("MyBatis.save.duplicateIds", "문장 id 가 겹칩니다: {0}", string.Join(", ", check.DuplicateIds));
            await TellAsync(workspace, Format("MyBatis.save.checkFailed", "저장 전 검사를 통과하지 못해 쓰지 않았습니다: {0}", reason), ct).ConfigureAwait(true);
            return false;
        }

        // 창을 기다리는 동안 파일이 또 바뀌었으면 쓰지 않는다(옛 기준으로 만든 원문이 밖의 고침을 덮는다). 미룬 감시가 끝난 뒤 병합한다.
        if (!ReferenceEquals(session.Document, doc) || (snap.Exists && !string.Equals(MapperDisk.Read(path).Hash, doc.Hash, StringComparison.Ordinal)))
        {
            await TellAsync(workspace, Text("MyBatis.save.changedWhileSaving", "저장하는 동안 파일이 밖에서 바뀌어 쓰지 않았습니다. 바뀐 내용을 반영한 뒤 다시 저장하십시오."), ct).ConfigureAwait(true);
            return false;
        }

        var newText = doc.Apply([.. chosen.Select(i => i.Edit)]);
        bool written = false;
        _watch.BeginWrite(workspace, MapperHash.Compute(newText));
        try
        {
            try
            {
                await Writer.WriteAsync(path, newText, doc.Format, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 읽기 전용·권한·인코딩(euc-kr 로 못 쓰는 글자) 오류: 알리고 고침을 남긴다(02-01 후속 메모).
                await TellAsync(workspace, Format("MyBatis.save.writeFailed", "{0} 에 쓰지 못했습니다. 고침은 남아 있습니다.\n{1}", Path.GetFileName(path), ex.Message), ct).ConfigureAwait(true);
                return false;
            }

            if (!string.Equals(path, session.MapperPath, StringComparison.Ordinal))
            {
                session.Relocate(path);
            }

            session.Rebase(newText, [.. chosen.Select(i => i.CurrentId)]);
            written = true;
            return true;
        }
        finally
        {
            _watch.EndWrite(workspace, written);
        }
    }

    // design.md 5.3 「디스크에서 지워짐·옮겨짐」: 같은 경로에 다시 쓰기 / 경로 골라 쓰기. file.saveAs 는 쓰지 않는다.
    private static async Task<string?> AskMissingTargetAsync(SqlWorkspaceViewModel workspace, IMyBatisWorkspaceSession session, CancellationToken ct)
    {
        var same = Text("MyBatis.save.missing.samePath", "같은 경로에 다시 쓰기");
        var pick = Text("MyBatis.save.missing.pickPath", "경로 골라 쓰기");
        var title = Format("MyBatis.save.missing.title", "{0} 이(가) 지워졌거나 옮겨졌습니다", MapperWatchService.FileName(session));
        var dialogs = workspace.Host.Dialogs;
        var answer = await dialogs.ChooseAsync(title, [same, pick], ct).ConfigureAwait(true);
        if (answer == same)
        {
            return session.MapperPath;
        }

        if (answer == pick && await dialogs.PickSaveFileAsync(MapperWatchService.FileName(session), ct).ConfigureAwait(true) is { } picked)
        {
            return Path.GetFullPath(picked);
        }

        return null;
    }

    private bool ShowEveryTime() => ReadShowEveryTime?.Invoke() ?? _settings?.Current.MyBatis.ShowSaveDiffEveryTime ?? true;

    private async Task SetShowEveryTimeAsync(bool value)
    {
        if (WriteShowEveryTime is { } write)
        {
            await write(value).ConfigureAwait(true);
            return;
        }

        if (_settings is null)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { MyBatis = s.MyBatis with { ShowSaveDiffEveryTime = value } }).ConfigureAwait(true);
        }
        catch (IOException)
        {
            // 설정을 못 써도 이번 저장은 그대로 한다.
        }
    }

    private static Task TellAsync(SqlWorkspaceViewModel workspace, string message, CancellationToken ct) =>
        workspace.Host.Dialogs.ShowMessageAsync(Text("MyBatis.save.title", "매퍼에 저장"), message, ct);

    private static string Text(string key, string fallback) => UiText.Get(key, fallback);

    private static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);
}
