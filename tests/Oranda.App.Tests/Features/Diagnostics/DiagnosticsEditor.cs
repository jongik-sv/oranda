using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Language;

namespace Oranda.App.Tests.Features.Diagnostics;

/// <summary>
/// 진단 시험의 편집기: 시험이 고른 언어 서비스로 표시기를 붙인 SQL 편집기를 창에 올린다.
/// 표시기는 창을 띄우기 <b>전에</b> 붙인다. <see cref="EditorDiagnosticsStartup.Install"/>(DiagnosticsTests 의 시작 작업 시험이 부른다)은
/// 프로세스가 끝날 때까지 남는 Loaded 처리기를 달아, 그 뒤 띄운 편집기마다 기본 언어 서비스(시험 프로세스에서는 null = 분석 안 함)로 표시기를 먼저 붙인다.
/// 띄운 뒤 <see cref="EditorDiagnostics.Attach"/> 하면 그 표시기를 돌려받아 시험의 언어 서비스가 한 번도 불리지 않는다
/// — 그 시험이 앞서 돌았는지에 따라 전체 실행에서만 갈리던 실패(ci-flaky F9: 분석 호출 0회·LastCallAt 0·오류 0개).
/// </summary>
internal static class DiagnosticsEditor
{
    public static (Window Window, SqlEditorView View, SqlEditorViewModel Vm, EditorDiagnostics Diagnostics) Show(
        string text, Func<ILanguageService?> language, double width = 900, double height = 600)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var (window, diagnostics) = ShowAttached(view, language, width, height);
        return (window, view, vm, diagnostics);
    }

    /// <summary>이미 만든 편집기에 표시기를 붙이고 창에 올린다(붙인 뒤에 띄운다).</summary>
    public static (Window Window, EditorDiagnostics Diagnostics) ShowAttached(
        SqlEditorView view, Func<ILanguageService?> language, double width = 900, double height = 600)
    {
        var diagnostics = EditorDiagnostics.Attach(view, language);
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(diagnostics, EditorDiagnostics.Of(view));
        return (window, diagnostics);
    }
}
