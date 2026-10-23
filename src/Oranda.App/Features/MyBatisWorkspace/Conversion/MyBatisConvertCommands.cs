using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Platform.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>
/// 「MyBatis 작업 공간으로 바꾸기」 명령의 처리기 자리(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결한다.
/// 특성 문자열·제목 파일·키맵은 고치지 않고 몸만 TSK-03-05(design.md 11장)가 채운다.
/// 대상 = 문서 탭 우클릭이면 그 문서(<see cref="DockTabMenu.TargetPanel"/>), 메뉴면 활성 작업 공간. 원래 작업 공간·파일은 바꾸지 않는다.
/// </summary>
[OrandaCommand("oranda.mybatis.convertWorkspace", CommandCategory.File, Icon = "swap", Enablement = "sqlWorkspaceActive && !mybatisDocActive && mybatisConvertReady", Placements = ["menu:file/mybatisConvert@97", "context:documentTab/mybatisConvert@40"])]
public sealed class MyBatisConvertCommands : ICommandHandler
{
    // 시작 시험 제약(OutlineViewModel 주석 참고): 생성자는 매개변수 없음 또는 IServiceProvider 만, Language 형 필드 없음.
    private readonly IServiceProvider? _services;

    public MyBatisConvertCommands()
    {
    }

    public MyBatisConvertCommands(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = SqlEditorHost.Current;
        var workspace = DockTabMenu.TargetPanel as SqlWorkspaceViewModel ?? host.ActiveWorkspace;
        if (workspace is null || workspace.MyBatis is not null
            || (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner)
        {
            return;
        }

        var formatter = _services?.GetService(typeof(ISqlFormatter)) as ISqlFormatter;
        var options = formatter is null ? null : await DefaultFormatAsync(cancellationToken).ConfigureAwait(true);
        var writer = new ConvertWriter(
            _services?.GetService(typeof(IMapperFileWriter)) as IMapperFileWriter ?? new MapperFileWriter(),
            _services?.GetService(typeof(IMyBatisScenarioStore)) as IMyBatisScenarioStore);
        await RunAsync(host, workspace, vm => ConvertDialog.ShowAsync(owner, vm), writer, formatter, options, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 바꾸기 흐름(시험이 대화 상자 대신 <paramref name="show"/> 를 넣는다). 탭을 읽어 대화 상자를 띄우고, 고른 매퍼에 쓴 뒤 MyBatis 작업 공간으로 연다.
    /// 대상 매퍼가 이미 MyBatis 문서로 열려 있으면: 저장 안 한 고침이 있으면 막고 「먼저 저장」, 없으면 파일에 쓰고 그 작업 공간으로 옮겨 간다
    /// (다시 읽기는 TSK-03-03 파일 감시, 2026-10-23 조정자 답 Q2).
    /// </summary>
    internal static async Task RunAsync(
        SqlEditorHost host,
        SqlWorkspaceViewModel workspace,
        Func<ConvertDialogViewModel, Task<ConvertDialogResult?>> show,
        ConvertWriter writer,
        ISqlFormatter? formatter,
        FormatterOptions? formatOptions,
        CancellationToken ct)
    {
        var title = UiText.Get("MbConvert.title", "MyBatis 작업 공간으로 바꾸기");
        var tabs = workspace.Tabs.Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => new ConvertSourceTab(t.Title, t.Text, [.. t.Binds.Rows.Select(r => new ConvertBind(r.Name, r.Type, r.Direction, r.Text))]))
            .ToList();
        if (tabs.Count == 0)
        {
            await host.Dialogs.ShowMessageAsync(title, UiText.Get("MbConvert.error.noTabs", "바꿀 SQL 탭이 없습니다."), ct).ConfigureAwait(true);
            return;
        }

        var name = workspace.SourcePath is { } source ? Path.GetFileName(source) : workspace.Name;
        var vm = new ConvertDialogViewModel(
            name, tabs, SuggestPath(workspace), SuggestNamespace(workspace.Name), ConvertWriter.ReadExisting,
            ConvertEngine.Analyze, ConvertEngine.Convert, ConvertEngine.IsValidId, formatter, formatOptions);
        if (await show(vm).ConfigureAwait(true) is not { } result)
        {
            return;
        }

        var path = Path.GetFullPath(result.Path);
        if (host.Workspaces.FirstOrDefault(w => string.Equals(w.SourcePath, path, StringComparison.OrdinalIgnoreCase)) is { MyBatis.IsDirty: true })
        {
            await host.Dialogs.ShowMessageAsync(title, UiText.Get("MbConvert.error.dirty", "이 매퍼가 열려 있고 저장 안 한 고침이 있습니다. 먼저 저장하십시오."), ct)
                .ConfigureAwait(true);
            return;
        }

        var written = result.Target == ConvertTargetKind.NewFile
            ? await writer.WriteNewAsync(path, result.Namespace, result.Statements, result.Options.SaveScenario, ct).ConfigureAwait(true)
            : await writer.AppendAsync(path, result.Statements, result.Options.SaveScenario, ct).ConfigureAwait(true);
        if (written.Error is { } error)
        {
            await host.Dialogs.ShowMessageAsync(title, error, ct).ConfigureAwait(true);
            return;
        }

        if (written.ScenarioError is { } scenarioError)
        {
            // 매퍼는 이미 썼다: 알리고 그대로 연다.
            await host.Dialogs.ShowMessageAsync(title, scenarioError, ct).ConfigureAwait(true);
        }

        // 03-01 열기 훅: 새 매퍼는 MyBatis 작업 공간으로 열고, 이미 열린 매퍼는 그 작업 공간을 앞으로 가져온다.
        await host.OpenWorkspaceFileAsync(path, ct).ConfigureAwait(true);
    }

    /// <summary>새 매퍼 경로 제안: 원래 작업 공간 파일 옆, 이름 = 작업 공간 이름 + Mapper.xml(파일에서 열지 않았으면 문서 폴더).</summary>
    internal static string SuggestPath(SqlWorkspaceViewModel workspace)
    {
        var dir = workspace.SourcePath is { } source ? Path.GetDirectoryName(source) : null;
        dir ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var stem = string.Concat(workspace.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsWhiteSpace(c)));
        return Path.Combine(dir, (stem.Length == 0 ? "Sql" : stem) + "Mapper.xml");
    }

    /// <summary>namespace 제안: 작업 공간 이름의 영문자·숫자로 만든 이름 + Mapper. 없으면 「Mapper」.</summary>
    internal static string SuggestNamespace(string workspaceName)
    {
        var words = new string([.. workspaceName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stem = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return stem.Length == 0 || char.IsAsciiDigit(stem[0]) ? "Mapper" : stem + "Mapper";
    }

    /// <summary>기본 포매터 프로필(<c>AppSettings.FormatterProfileId</c>)의 옵션. 못 읽으면 「Oranda 기본」.</summary>
    private async Task<FormatterOptions> DefaultFormatAsync(CancellationToken ct)
    {
        if (_services?.GetService(typeof(ISettingsStore)) is not ISettingsStore settings
            || _services.GetService(typeof(IFormatterProfileStore)) is not IFormatterProfileStore profiles)
        {
            return FormatterOptions.OrandaDefault;
        }

        try
        {
            var id = (await settings.LoadAsync(ct).ConfigureAwait(true)).Value.FormatterProfileId;
            var profile = (await profiles.ListAsync(ct).ConfigureAwait(true)).FirstOrDefault(p => p.Id == id);
            return profile?.Options ?? FormatterOptions.OrandaDefault;
        }
        catch (IOException)
        {
            return FormatterOptions.OrandaDefault;
        }
    }
}
