using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Language;

namespace Oranda.App.Features.Diagnostics;

/// <summary>
/// 첫 프레임 뒤 SQL 편집기마다 <see cref="EditorDiagnostics"/> 를 붙인다(TSK-04-03). 시작 경로(첫 프레임 전)에서는 돌지 않고(D-006),
/// 언어 서비스(Oranda.Language·ANTLR)는 첫 분석 때 백그라운드 스레드에서 비로소 찾는다.
/// 생성자는 <see cref="IServiceProvider"/> 만 받는다(등록되지 않은 형식을 물어 지연 어셈블리를 깨우지 않게). 소유: TSK-04-03.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class EditorDiagnosticsStartup : IStartupTask
{
    private static int s_classHandlerAdded;
    private readonly IServiceProvider? _services;

    public EditorDiagnosticsStartup()
    {
    }

    public EditorDiagnosticsStartup(IServiceProvider services)
    {
        _services = services;
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        if (_services is { } services)
        {
            EditorDiagnostics.LanguageServiceProvider = () => services.GetService(typeof(ILanguageService)) as ILanguageService;
        }

        return Dispatcher.UIThread.InvokeAsync(Install).GetTask();
    }

    /// <summary>앞으로 열리는 편집기와 이미 열린 편집기 모두에 붙인다. UI 스레드에서 부른다. 여러 번 불러도 된다.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_classHandlerAdded, 1) == 0)
        {
            SqlEditorView.LoadedEvent.AddClassHandler<SqlEditorView>((view, _) => EditorDiagnostics.Attach(view), RoutingStrategies.Direct);
        }

        foreach (var tab in SqlEditorHost.Current.Tabs)
        {
            if (tab.Surface is SqlEditorView view)
            {
                EditorDiagnostics.Attach(view);
            }
        }
    }
}
