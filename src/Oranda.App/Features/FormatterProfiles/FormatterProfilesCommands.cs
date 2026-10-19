using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Core.Text;

namespace Oranda.App.Features.FormatterProfiles;

/// <summary>TSK-05-03 포맷 명령의 공통 동작(순수 함수 — UI 없이 시험한다).</summary>
public static class FormatterProfilesCommands
{
    public const string FormatDocument = "oranda.format.document";

    public const string FormatStatement = "oranda.format.statement";

    public const string ShowProfiles = "oranda.format.profiles";

    public const string PanelId = "oranda.panel.formatterProfiles";

    /// <summary>엔진이 실패를 돌려준 이유를 사람이 읽는 말로.</summary>
    public static string Describe(FormatResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var reason = result.Failure switch
        {
            FormatFailure.TokenMismatch => "포맷 전후 토큰이 달라졌다",
            FormatFailure.CommentCountMismatch => "포맷 전후 주석 수가 달라졌다",
            FormatFailure.NotIdempotent => "두 번 포맷한 결과가 달라 안전하지 않다",
            FormatFailure.InternalError => "포매터 내부 오류",
            _ => "알 수 없는 이유",
        };
        return string.IsNullOrWhiteSpace(result.Message) ? reason : $"{reason} ({result.Message})";
    }

    /// <summary>
    /// 편집기 탭에 포맷을 적용한다. 실패하면 문서를 건드리지 않고 이유를 <paramref name="message"/> 로 돌려준다.
    /// 성공하면 바뀐 부분만 한 번의 편집(실행 취소 1단계)으로 바꾼다.
    /// </summary>
    /// <returns>true = 문서가 바뀜.</returns>
    public static bool Apply(SqlEditorViewModel tab, ISqlFormatter formatter, FormatterOptions options, TextRange? range, out string? message)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(options);
        message = null;
        if (tab.IsReadOnly)
        {
            message = "읽기 전용 문서라 포맷하지 않는다.";
            return false;
        }

        var before = tab.Text;
        var result = formatter.Format(before, options, range);
        if (!result.Succeeded)
        {
            message = "포맷을 적용하지 않았다(원문 유지): " + Describe(result);
            return false;
        }

        if (string.Equals(result.Text, before, StringComparison.Ordinal))
        {
            message = "이미 포맷되어 있다.";
            return false;
        }

        // 바뀐 가운데만 교체해 캐럿·접기·북마크가 덜 흔들리게 한다.
        var after = result.Text;
        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        var oldLength = before.Length - prefix - suffix;
        var newText = after.Substring(prefix, after.Length - prefix - suffix);
        var delta = after.Length - before.Length;
        int selStart = tab.SelectionStart, selLength = tab.SelectionLength;
        if (selLength > 0)
        {
            selLength = Math.Max(0, selLength + delta);
        }
        else
        {
            selStart = Math.Min(tab.CaretOffset, after.Length);
        }

        return tab.Apply(new TextOpResult(prefix, oldLength, newText, selStart, selLength));
    }
}

/// <summary>
/// <c>oranda.format.document</c>(선택 영역이 있으면 그 범위, 없으면 문서 전체)·<c>oranda.format.statement</c>(현재 문장)·
/// <c>oranda.format.profiles</c>(프로필 화면 열기) 처리기. 포맷에 쓰는 옵션은 설정의 기본 프로필이다.
/// 생성자는 <see cref="IServiceProvider"/> 하나뿐이고 서비스는 실행할 때 꺼낸다(시작 경로에서 Language 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand(FormatterProfilesCommands.FormatDocument, CommandCategory.Format, Title = "포맷(선택 영역 또는 문서)", When = "editorFocus", Enablement = "!readOnlyDoc",
    Placements = ["menu:edit/formatDocument@400", "context:editor/formatDocument@400", "toolbar:main/format@60"])]
[OrandaCommand(FormatterProfilesCommands.FormatStatement, CommandCategory.Format, Title = "현재 문장 포맷", When = "editorFocus", Enablement = "!readOnlyDoc",
    Placements = ["menu:edit/formatStatement@410"])]
[OrandaCommand(FormatterProfilesCommands.ShowProfiles, CommandCategory.Format, Title = "포매터 프로필",
    Placements = ["menu:tools/formatterProfiles@300"])]
public sealed class FormatterProfilesCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;
    private SqlEditorHost? _host;

    public FormatterProfilesCommandHandler()
    {
    }

    public FormatterProfilesCommandHandler(IServiceProvider services) => _services = services;

    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>시험용: 포매터.</summary>
    internal ISqlFormatter? Formatter { get; init; }

    /// <summary>시험용: 지금 쓸 옵션.</summary>
    internal Func<CancellationToken, Task<FormatterOptions>>? OptionsProvider { get; init; }

    /// <summary>시험용·셸: 알림 표시. 기본은 메인 창 메시지 상자.</summary>
    internal Func<string, Task>? Notify { get; init; }

    /// <summary>시험용: 프로필 화면(문서 탭) 열기.</summary>
    internal Func<string, ToolPanelViewModelBase, string, ToolPanelViewModelBase?>? OpenDocument { get; init; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CommandId == FormatterProfilesCommands.ShowProfiles)
        {
            await ShowProfilesAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        var tab = Host.Active;
        if (tab is null || tab.IsReadOnly)
        {
            return;
        }

        var formatter = Formatter ?? _services?.GetService(typeof(ISqlFormatter)) as ISqlFormatter;
        if (formatter is null)
        {
            return;
        }

        TextRange? range = null;
        if (context.CommandId == FormatterProfilesCommands.FormatStatement)
        {
            if (tab.GetStatementAtCaret() is not { } statement)
            {
                await NotifyAsync("캐럿 위치에 포맷할 문장이 없다.").ConfigureAwait(true);
                return;
            }

            range = statement.Range;
        }
        else if (tab.HasSelection)
        {
            range = new TextRange(tab.SelectionStart, tab.SelectionLength);
        }

        var options = await GetOptionsAsync(cancellationToken).ConfigureAwait(true);
        if (!FormatterProfilesCommands.Apply(tab, formatter, options, range, out var message) && message is not null)
        {
            await NotifyAsync(message).ConfigureAwait(true);
        }
    }

    private async Task ShowProfilesAsync(CancellationToken ct)
    {
        var vm = _services is null ? new FormatterProfilesViewModel() : new FormatterProfilesViewModel(_services);
        ToolPanelViewModelBase? opened = OpenDocument is not null
            ? OpenDocument(FormatterProfilesCommands.PanelId, vm, "포매터 프로필")
            : (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(FormatterProfilesCommands.PanelId, vm, "포매터 프로필")?.Panel as ToolPanelViewModelBase;
        if (opened is FormatterProfilesViewModel shown)
        {
            await shown.LoadAsync(ct).ConfigureAwait(true);
        }
    }

    private async Task<FormatterOptions> GetOptionsAsync(CancellationToken ct)
    {
        if (OptionsProvider is not null)
        {
            return await OptionsProvider(ct).ConfigureAwait(true);
        }

        var id = FormatterIds.Default;
        if (_services?.GetService(typeof(ISettingsStore)) is ISettingsStore settings)
        {
            id = (await settings.LoadAsync(ct).ConfigureAwait(true)).Value.FormatterProfileId;
        }

        if (_services?.GetService(typeof(IFormatterProfileStore)) is IFormatterProfileStore store)
        {
            var all = await store.ListAsync(ct).ConfigureAwait(true);
            return (all.FirstOrDefault(p => p.Id == id) ?? (all.Count > 0 ? all[0] : null))?.Options ?? new FormatterOptions();
        }

        return new FormatterOptions();
    }

    private Task NotifyAsync(string message)
    {
        if (Notify is not null)
        {
            return Notify(message);
        }

        return (_services?.GetService(typeof(ShellController)) as ShellController)?.MainWindow is Window owner
            ? ConnectionDialogs.MessageAsync(owner, message)
            : Task.CompletedTask;
    }
}
