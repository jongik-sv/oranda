using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Controls;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 다른 창(SQL 모니터·계획 창·튜닝 도우미)이 「튜닝 보고서 만들기」 를 대상과 함께 여는 길. 대상을 맡기고 명령 ID 를 부른다(한 번 쓰면 비운다).
/// 레지스트리로 직접 부를 때는 매개변수로 <see cref="CursorRef"/> 나 sql_id 글을 넘겨도 된다.
/// </summary>
public static class TuningReportNavigation
{
    public const string CommandId = "oranda.tuning.report";

    private static (CursorRef Cursor, ISession? Session)? _pending;

    /// <summary>sql_id(와 그 sql_id 를 볼 세션)로 대화 상자를 연다. 세션이 null 이면 활성 탭의 세션.</summary>
    public static Task OpenAsync(CursorRef cursor, ISession? session = null)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        _pending = (cursor, session);
        return ShellCommandRunner.RunAsync(CommandId);
    }

    internal static (CursorRef Cursor, ISession? Session)? Take()
    {
        var p = _pending;
        _pending = null;
        return p;
    }
}

/// <summary>
/// TSK-06-07 「튜닝 보고서 만들기…」: 대상 sql_id 를 맡긴 것 → 매개변수 → 앞의 SQL 모니터 고른 SQL(그 모니터 세션) → 앞의 튜닝 도우미 문장
/// → 편집기 현재 문장(같은 세션의 마지막 실행 커서, 없으면 글로 셈한 sql_id) → 계획 창 마지막 커서 순으로 찾고, 대화 상자를 띄운다.
/// 저장하면 상태 줄 저장 알림(열기·폴더 열기)을 보이고, 고른 경우 기본 브라우저로 연다.
/// </summary>
[OrandaCommand(TuningReportNavigation.CommandId, CommandCategory.Sql, Icon = "exportFile", Enablement = "hasSession || dedicatedSession",
    Placements = ["menu:tuning/report@490"])]
public sealed class TuningReportCommandHandler : ICommandHandler
{
    private readonly ServiceContainer? _container;
    private (Guid SessionId, string Text, CursorRef Cursor)? _last;

    public TuningReportCommandHandler()
    {
    }

    public TuningReportCommandHandler(ServiceContainer container)
    {
        _container = container;
        _container.GetService<IEventBus>()?.Subscribe<StatementExecuted>(this, e =>
        {
            if (e.Result.Cursor is { } cursor)
            {
                _last = (e.SessionId, Normalize(e.Statement.Text), cursor);
            }
        });
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        var target = Resolve(_container, shell, context.Parameter);
        var vm = new TuningReportDialogViewModel(target.Session, _container.GetService<ITuningService>(), _container.GetService<IMonitorService>(), target.SqlId, target.EditorText)
        {
            SourceText = target.Source,
        };
        var owner = DialogShell.MainWindow;
        var dialog = new TuningReportDialog { DataContext = vm };
        LiteralTranslator.Translate(dialog);
        string? path;
        if (owner is { IsVisible: true })
        {
            path = await dialog.ShowDialog<string?>(owner).ConfigureAwait(true);
        }
        else
        {
            await DialogShell.ShowAsync(dialog, owner).ConfigureAwait(true);
            path = vm.ResultPath;
        }

        if (path is null)
        {
            return;
        }

        var name = Path.GetFileName(path);
        var opened = vm.OpenAfter && FileLauncher.Open(path);
        var text = (LiteralTranslator.IsEnglish ? "Report saved: " : "보고서 저장: ") + name
            + (vm.OpenAfter && !opened ? (LiteralTranslator.IsEnglish ? " (could not open a browser)" : " (브라우저를 열지 못했습니다)") : string.Empty);
        SaveFeedback.Show(new SaveNotice(text, SaveFeedbackKind.Success, path));
    }

    private sealed record Target(ISession? Session, string? SqlId, string? EditorText, string Source);

    private Target Resolve(ServiceContainer container, IShellUi shell, object? parameter)
    {
        var en = LiteralTranslator.IsEnglish;
        var session = ExecutionCommands.ActiveTabSession(container) ?? SessionRouter.CurrentOf(container);
        if (TuningReportNavigation.Take() is { } pending)
        {
            return new Target(pending.Session ?? session, pending.Cursor.SqlId, null, string.Empty);
        }

        if (ChildCursorsCommandHandler.FromParameter(parameter) is { } p)
        {
            return new Target(session, p.SqlId, null, string.Empty);
        }

        switch (shell.Docking.ActiveDocument?.Panel)
        {
            case SqlMonitorViewModel { SelectedSql: { } picked } monitor:
                return new Target(monitor.MonitorSession ?? session, picked.Row.Cursor.SqlId, null, en ? "Selected in the SQL monitor" : "SQL 모니터에서 고른 SQL");
            case TuningAssistantViewModel { Input: { Statement: { } st } input }:
                return FromStatement(input.Session ?? session, st.Text, en ? "Tuning assistant statement" : "튜닝 도우미 문장");
        }

        if (container.GetService<IEditorContext>()?.GetCurrentStatement() is { } current && !string.IsNullOrWhiteSpace(current.Text))
        {
            return FromStatement(session, current.Text, en ? "Editor statement" : "편집기 문장");
        }

        if (PlanViewModel.Current?.LastCursor is { } planCursor)
        {
            return new Target(session, planCursor.SqlId, null, en ? "Last cursor of the plan window" : "계획 창의 마지막 커서");
        }

        return new Target(session, null, null, string.Empty);
    }

    // 같은 세션에서 이 문장을 마지막으로 실행한 커서가 있으면 그 sql_id, 없으면 글로 셈한 sql_id(보낸 글과 같아야 맞는다).
    private Target FromStatement(ISession? session, string text, string source)
    {
        var en = LiteralTranslator.IsEnglish;
        if (session is not null && _last is { } last && last.SessionId == session.Id && last.Text == Normalize(text))
        {
            return new Target(session, last.Cursor.SqlId, text, source + (en ? " · last run" : " · 마지막 실행"));
        }

        return new Target(session, SqlIdHash.Compute(text.Trim().TrimEnd(';').TrimEnd()), text,
            source + (en ? " · sql_id computed from the text (run it once if it is not found)" : " · 글로 셈한 sql_id(못 찾으면 한 번 실행하세요)"));
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
