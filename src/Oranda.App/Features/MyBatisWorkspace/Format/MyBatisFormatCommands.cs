using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.MyBatisWorkspace.Format;

/// <summary>
/// 「쿼리 포맷」 명령: 활성 MyBatis 쿼리 탭의 요소 하나(바깥 태그는 원문, 본문 SQL 만)를 포맷한다. 단축키는 일반 포맷과 같은 키를 나눠 쓴다
/// (키맵에서 일반 포맷은 <c>sqlEditorFocus</c>·<c>plsqlEditorFocus</c> 두 항목, 이 명령은 <c>mybatisDocFocus</c>).
/// 적용은 일반 포맷과 같은 <see cref="FormatterProfilesCommands.Apply"/>(바뀐 부분만 한 번에 교체 — 실행 취소 1단계)이고, 실패하면 원문을 두고 이유를 메시지 상자로 알린다.
/// 옵션은 설정의 기본 프로필이다. 서비스는 실행할 때 꺼낸다(시작 경로에서 Language 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand(MyBatisFormatCommands.FormatStatement, CommandCategory.Format, Icon = "format", When = "mybatisDocFocus", Enablement = "mybatisDocActive && mybatisStatementSelected && !readOnlyDoc",
    Placements = ["toolbar:mybatis/format@35", "context:mybatisEditor/format@20", "menu:edit/format/mybatisFormat@320"])]
public sealed class MyBatisFormatCommands : ICommandHandler
{
    public const string FormatStatement = "oranda.mybatis.formatStatement";

    private readonly IServiceProvider? _services;

    public MyBatisFormatCommands()
    {
    }

    public MyBatisFormatCommands(IServiceProvider services) => _services = services;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>(시험이 바꾼다).</summary>
    internal SqlEditorHost? Host { get; init; }

    /// <summary>시험용: 포매터.</summary>
    internal ISqlFormatter? Formatter { get; init; }

    /// <summary>시험용: 지금 쓸 옵션.</summary>
    internal Func<CancellationToken, Task<FormatterOptions>>? OptionsProvider { get; init; }

    /// <summary>시험용·셸: 알림 표시. 기본은 메인 창 메시지 상자.</summary>
    internal Func<string, Task>? Notify { get; init; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = Host ?? SqlEditorHost.Current;

        // 활성 탭이 그 작업 공간의 쿼리 탭일 때만 — 「쿼리」 창에서 고른 id 만 보고 다른 탭을 포맷하지 않는다.
        if (host.Active is not { IsReadOnly: false } tab || host.ActiveWorkspace?.MyBatis?.StatementIdOf(tab) is null)
        {
            return;
        }

        var formatter = Formatter ?? _services?.GetService(typeof(IMyBatisStatementFormatter)) as ISqlFormatter;
        if (formatter is null)
        {
            return;
        }

        var options = await GetOptionsAsync(cancellationToken).ConfigureAwait(true);

        // 옵션을 읽는 사이에 탭이 바뀌거나 닫혔으면 포맷하지 않는다.
        if (!ReferenceEquals(host.Active, tab) || host.ActiveWorkspace?.MyBatis?.StatementIdOf(tab) is null)
        {
            return;
        }

        if (!FormatterProfilesCommands.Apply(tab, formatter, options, null, out var message) && message is not null)
        {
            await NotifyAsync(message).ConfigureAwait(true);
        }
    }

    private async Task<FormatterOptions> GetOptionsAsync(CancellationToken ct)
    {
        if (OptionsProvider is not null)
        {
            return await OptionsProvider(ct).ConfigureAwait(true);
        }

        return await FormatterIds.ActiveOptionsAsync(
            _services?.GetService(typeof(ISettingsStore)) as ISettingsStore,
            _services?.GetService(typeof(IFormatterProfileStore)) as IFormatterProfileStore,
            ct).ConfigureAwait(true);
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
