using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 객체 상세의 Java 소스·클래스 동작(TSK-14-23, D-055): 「다시 컴파일」(<c>ALTER JAVA SOURCE … COMPILE</c>·<c>ALTER JAVA CLASS … RESOLVE</c>)과
/// Java 소스의 「편집기로 열기」(DDL 을 새 SQL 탭에, 실행은 사용자가). 컴파일은 실행 엔진으로 보내므로 운영 접속 확인·읽기 전용 차단·실행 기록이
/// 다른 DDL 과 같다. 서버에 JVM 이 없거나 읽기 전용 접속이면 단추를 막고 이유를 보인다. 클래스 파일·jar 올리기(loadjava)는 하지 않는다.
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    private JavaErrorSource? _javaErrors;

    /// <summary>JAVA SOURCE·JAVA CLASS 를 보고 있다(「다시 컴파일」 줄이 보인다).</summary>
    public bool IsJavaCompilable => Target is { ObjectType: "JAVA SOURCE" or "JAVA CLASS" };

    /// <summary>JAVA SOURCE 를 보고 있다(「편집기로 열기」가 보인다).</summary>
    public bool IsJavaSource => Target is { ObjectType: "JAVA SOURCE" };

    /// <summary>「다시 컴파일」을 누를 수 있다(JVM 이 있고 읽기 전용 접속이 아니다).</summary>
    [ObservableProperty]
    private bool _canCompileJava;

    /// <summary>「다시 컴파일」을 막은 이유(누를 수 있으면 빈 글).</summary>
    [ObservableProperty]
    private string _javaCompileReason = string.Empty;

    /// <summary>시험용: 컴파일 전 확인(기본 = 편집기 대화상자의 확인 창).</summary>
    internal Func<string, CancellationToken, Task<bool>>? Confirm { get; set; }

    /// <summary>시험용: 실행 엔진(기본 = 서비스의 <see cref="IExecutionService"/>).</summary>
    internal IExecutionService? Exec { get; set; }

    /// <summary>시험용: 새 SQL 탭에 글을 넣는 방법(글, 접속 프로필). 기본 = <see cref="SqlEditor.SqlEditorHost"/> 의 새 문서.</summary>
    internal Func<string, Guid?, bool>? EditorOpener { get; set; }

    /// <summary>시험용: 컴파일 오류 창을 앞으로 가져오는 방법.</summary>
    internal Action? ShowErrorsPanel { get; set; }

    /// <summary>시험용: 컴파일 뒤 스키마 브라우저를 새로 읽는 방법(기본 = 도킹의 스키마 브라우저 다시 읽기).</summary>
    internal Func<Task>? RefreshSchemaBrowser { get; set; }

    /// <summary>마지막 컴파일이 올린 오류(시험·창 확인용).</summary>
    internal IReadOnlyList<CompileError> JavaErrors => _javaErrors?.Errors ?? (IReadOnlyList<CompileError>)[];

    private async Task UpdateJavaStateAsync(IMetadataProvider meta, ISession session, CancellationToken ct)
    {
        OnPropertyChanged(nameof(IsJavaCompilable));
        OnPropertyChanged(nameof(IsJavaSource));
        if (!IsJavaCompilable)
        {
            CanCompileJava = false;
            JavaCompileReason = string.Empty;
            return;
        }

        if (session.Profile.ReadOnly)
        {
            CanCompileJava = false;
            JavaCompileReason = "읽기 전용 접속이라 컴파일할 수 없다";
            return;
        }

        bool available;
        try
        {
            available = await meta.IsJavaAvailableAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // JVM 유무를 모르면 컴파일하지 않는다(이유를 보인다).
        catch
#pragma warning restore CA1031
        {
            available = false;
        }

        CanCompileJava = available;
        JavaCompileReason = available ? string.Empty : "서버에 Java(JVM)가 없어 컴파일할 수 없다";
    }

    /// <summary>
    /// 「다시 컴파일」: 확인한 뒤 <c>ALTER JAVA …</c> 를 실행 엔진으로 보내고, <c>ALL_ERRORS</c> 의 오류를 컴파일 오류 창에 올린 다음
    /// 상세(상태)와 스키마 브라우저를 새로 읽는다. 오류 없이 끝나면 「컴파일됨」. 보냈으면 true.
    /// </summary>
    public async Task<bool> CompileJavaAsync(CancellationToken ct = default)
    {
        if (Target is not { ObjectType: "JAVA SOURCE" or "JAVA CLASS" } target || _session is not { } session)
        {
            return false;
        }

        if (!CanCompileJava)
        {
            StatusText = JavaCompileReason.Length > 0 ? JavaCompileReason : "컴파일할 수 없다";
            return false;
        }

        if (BuildJavaCompile(target) is not { } stmt)
        {
            return false;
        }

        if ((Exec ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService) is not { } exec)
        {
            StatusText = "실행 서비스가 없다";
            return false;
        }

        var kind = target.ObjectType == "JAVA SOURCE" ? "Java 소스" : "Java 클래스";
        var question = $"{kind} {target.Owner}.{target.Name} 을(를) 다시 컴파일할까요?\n\n{stmt.Text}";
        var confirm = Confirm ?? ((message, token) => SqlEditor.SqlEditorHost.Current.Dialogs.ConfirmAsync(message, token));
        if (!await confirm(question, ct))
        {
            StatusText = "컴파일하지 않았다";
            return false;
        }

        // DDL(암묵 커밋)이라 활성 SQL 작업 공간이 같은 접속이면 그 세션으로 보낸다(TSK-14-08, 무효 객체 컴파일과 같다).
        var runSession = ConnectionManager.SessionRouter.ForTool(_services, session) ?? session;
        IsBusy = true;
        try
        {
            var outcome = await DbTools.DbToolsSupport.RunAsync(exec, runSession, stmt, ct);
            if (outcome.Status is ExecutionStatus.Denied or ExecutionStatus.Cancelled)
            {
                StatusText = outcome.Message ?? "실행하지 않았다";
                return false;
            }

            // 「오류가 있는 채로 컴파일됨」(ORA-24344)·「다시 컴파일해야 한다」(ORA-29535)는 오류 목록을 본다. 그 밖의 실패는 그대로 알린다.
            var failure = outcome.Succeeded || outcome.Message is { } m && (m.Contains("24344", StringComparison.Ordinal) || m.Contains("29535", StringComparison.Ordinal))
                ? null
                : outcome.Message ?? "실패";
            var errors = await ReadJavaErrorsAsync(session, target, ct);
            PublishJavaErrors(target, errors);

            if (ResolveMeta() is not null)
            {
                await OpenAsync(session, target, IsDdlSelected, ct); // 상태(VALID/INVALID)·오류 구역을 새로 읽는다
            }

            await (RefreshSchemaBrowser ?? DefaultRefreshSchemaBrowser)();

            var errorCount = errors.Count(e => !e.IsWarning);
            StatusText = failure is not null ? "컴파일하지 못했다: " + failure
                : errorCount > 0 ? $"컴파일 오류 {errorCount}개 — 컴파일 오류 창에서 확인"
                : "컴파일됨";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 「편집기로 열기」(JAVA SOURCE): DDL 탭의 글을 새 SQL 탭에 넣는다(이 접속으로, 실행하지 않는다). 끝에 <c>/</c> 단독 줄이 없으면 붙인다
    /// (편집기가 Java 코드 전체를 한 문장으로 읽는 끝 표지). 열었으면 true.
    /// </summary>
    public bool OpenInEditor()
    {
        if (!IsJavaSource || string.IsNullOrWhiteSpace(DdlText))
        {
            return false;
        }

        var text = DdlText.TrimEnd();
        if (!text.Split('\n').Any(line => line.Trim() == "/"))
        {
            text += "\n/";
        }

        var ok = (EditorOpener ?? OpenInNewEditor)(text + "\n", _session?.Profile.Id);
        StatusText = ok ? "새 SQL 탭에 열었다(실행은 직접 한다)" : "편집기를 열지 못했다";
        return ok;
    }

    private static bool OpenInNewEditor(string text, Guid? profileId)
    {
        var tab = SqlEditor.SqlEditorHost.Current.NewDocument(text);
        tab.ConnectionProfileId = profileId;
        return true;
    }

    // Oracle 어셈블리 형식은 이 작은 메서드 안에서만 쓴다(처음 부를 때 올라온다).
    private static SqlStatement? BuildJavaCompile(DbObjectRef target) => Oranda.Oracle.Schema.SchemaStatements.Compile(target);

    private async Task<IReadOnlyList<CompileError>> ReadJavaErrorsAsync(ISession session, DbObjectRef target, CancellationToken ct)
    {
        if (ResolveMeta() is not { } meta)
        {
            return [];
        }

        try
        {
            return await meta.GetCompileErrorsAsync(session, target, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // 오류 목록을 못 읽어도 컴파일 결과는 그대로 알린다.
        catch
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private void PublishJavaErrors(DbObjectRef target, IReadOnlyList<CompileError> errors)
    {
        if (errors.Count == 0)
        {
            _javaErrors?.Errors.Clear();
            return;
        }

        _javaErrors ??= new JavaErrorSource($"{target.Owner}.{target.Name} ({target.ObjectType})");
        _javaErrors.Errors.Clear();
        foreach (var e in errors)
        {
            _javaErrors.Errors.Add(e);
        }

        CompileErrorsViewModel.Publish(_javaErrors);
        if (ShowErrorsPanel is { } show)
        {
            show();
        }
        else
        {
            (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.compileErrors");
        }
    }

    private Task DefaultRefreshSchemaBrowser()
        => ObjectDetailCommandHandler.FindSchemaBrowser(_services) is { } browser ? browser.ApplyFilterAsync() : Task.CompletedTask;

    /// <summary>객체 상세에서 컴파일한 Java 객체의 오류(오류 위치로 옮길 편집기가 없다 — 줄·열만 보인다).</summary>
    private sealed class JavaErrorSource(string title) : ICompileErrorSource
    {
        public string Title { get; } = title;

        public ObservableCollection<CompileError> Errors { get; } = [];

        public bool NavigateTo(CompileError item) => false;
    }
}
