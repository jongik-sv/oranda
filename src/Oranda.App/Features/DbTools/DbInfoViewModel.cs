using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Schema;

namespace Oranda.App.Features.DbTools;

/// <summary>파라미터 변경 적용 범위 선택(화면용). 값은 <c>Oranda.Core.Schema.ParameterScope</c> 와 같은 순서.</summary>
public enum DbParameterScope
{
    Memory,
    Spfile,
    Both,
}

/// <summary>
/// <c>oranda.panel.dbInfo</c> — DB 정보(인스턴스·버전·옵션·파라미터·NLS)와 파라미터 변경 DDL 미리보기(TSK-07-04).
/// 데이터는 <see cref="IDatabaseInfoProvider"/> 만 쓴다. 변경 DDL 은 만들기만 하고 실행하지 않는다.
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.dbInfo", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class DbInfoViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private IDatabaseInfoProvider? _provider;
    private readonly bool _providerInjected;
    private int _generation;

    public DbInfoViewModel()
    {
    }

    public DbInfoViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 제공자를 직접 넣는다.</summary>
    internal DbInfoViewModel(IDatabaseInfoProvider provider, IServiceProvider? services = null)
    {
        _provider = provider;
        _providerInjected = true;
        _services = services;
    }

    /// <summary>
    /// 시험용: 파라미터 변경 DDL 을 만드는 방법. 기본(null)은 세션의 DBMS 용 <see cref="ISchemaStatements.ParameterChangeDdl"/>
    /// (서비스 공급자의 선택기로 고른다).
    /// </summary>
    internal Func<string, string, DbParameterScope, string>? BuildParameterDdl { get; set; }

    public ISession? Session { get; internal set; }

    public ObservableCollection<DetailSection> Sections { get; } = [];

    [ObservableProperty]
    private DetailSection? _selectedSection;

    /// <summary>선택한 구역 안에서 거르는 글(모든 열, 대소문자 무시).</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>선택한 구역(거름 적용)을 열 너비 맞춘 텍스트로.</summary>
    [ObservableProperty]
    private string _sectionText = string.Empty;

    /// <summary>선택한 구역에서 거름을 통과한 행(표 컨트롤이 그린다).</summary>
    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<object?>> _visibleRows = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _parameterName = string.Empty;

    [ObservableProperty]
    private string _parameterValue = string.Empty;

    [ObservableProperty]
    private DbParameterScope _parameterScope = DbParameterScope.Both;

    /// <summary>미리보기 DDL(이름이 비었거나 잘못되면 빈 문자열).</summary>
    [ObservableProperty]
    private string _previewDdl = string.Empty;

    [ObservableProperty]
    private string _previewError = string.Empty;

    private Oranda.Core.Connection.Dbms? _scopeDefaultFor;

    /// <summary>이 DBMS 의 파라미터 변경 적용 범위 기본값: Oracle 은 Both, 그 밖은 Memory.</summary>
    public static DbParameterScope DefaultScope(Oranda.Core.Connection.Dbms dbms) =>
        dbms == Oranda.Core.Connection.Dbms.Oracle ? DbParameterScope.Both : DbParameterScope.Memory;

    partial void OnSelectedSectionChanged(DetailSection? value) => RefreshText();

    partial void OnFilterTextChanged(string value) => RefreshText();

    partial void OnParameterNameChanged(string value) => RefreshPreview();

    partial void OnParameterValueChanged(string value) => RefreshPreview();

    partial void OnParameterScopeChanged(DbParameterScope value) => RefreshPreview();

    /// <summary>표에서 파라미터 이름을 골랐을 때(이름·현재 값을 변경 입력칸에 채운다).</summary>
    public void PickParameter(string name, string? currentValue)
    {
        ParameterName = name;
        ParameterValue = currentValue ?? string.Empty;
    }

    /// <summary>서버에서 DB 정보를 읽어 구역을 채운다.</summary>
    public async Task LoadAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;

        // 적용 범위 기본값은 DBMS 를 따른다: Oracle 은 Both(그대로), 그 밖(Tibero 는 SPFILE·BOTH 가 없다)은 Memory.
        // 세션의 DBMS 가 처음 정해지거나 바뀔 때만 맞추고, 같은 DBMS 로 다시 읽을 때는 사용자가 고른 값을 두며, Oracle 첫 읽기에서는 아무것도 바꾸지 않는다.
        var dbms = session.Profile.Dbms;
        if (_scopeDefaultFor != dbms)
        {
            if (dbms != Oranda.Core.Connection.Dbms.Oracle || _scopeDefaultFor is not null)
            {
                ParameterScope = DefaultScope(dbms);
            }

            _scopeDefaultFor = dbms;
        }

        RefreshPreview(); // 세션의 DBMS 가 정해졌으니 미리보기 문장을 다시 만든다.
        var gen = Interlocked.Increment(ref _generation);
        // 이 DBMS 에 DB 정보 구현이 없으면(Tibero 등) 부르지 않고 이유만 보인다. 제공자를 직접 넣은 시험 생성자는 거르지 않는다.
        if (!_providerInjected
            && DbmsServiceKeys.Reason(_services, session.Profile.Dbms, DbmsServiceArea.DatabaseInfo, key => UiText.Current?.TryLookup(key)) is { } notImplemented)
        {
            Sections.Clear();
            SelectedSection = null;
            StatusText = notImplemented;
            return;
        }

        var provider = ResolveProvider();
        if (provider is null)
        {
            StatusText = "DB 정보 제공자가 없다";
            return;
        }

        IsBusy = true;
        try
        {
            var keep = SelectedSection?.Title;
            var sections = await provider.GetAsync(session, ct);
            if (gen != _generation)
            {
                return;
            }

            Sections.Clear();
            foreach (var s in sections)
            {
                Sections.Add(s);
            }

            SelectedSection = Sections.FirstOrDefault(s => s.Title == keep) ?? Sections.FirstOrDefault();
            var failed = Sections.Count(s => s.Columns is [{ } c] && c == "오류");
            StatusText = failed == 0 ? $"{session.Profile.Name} — {Sections.Count}개 구역" : $"{Sections.Count}개 구역 중 {failed}개는 권한 등으로 읽지 못했다";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "DB 정보를 읽지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private void RefreshText()
    {
        if (SelectedSection is not { } section)
        {
            SectionText = string.Empty;
            VisibleRows = [];
            return;
        }

        var filter = FilterText?.Trim();
        var rows = string.IsNullOrEmpty(filter)
            ? section.Rows
            : [.. section.Rows.Where(r => r.Any(c => ObjectDetailSection.Cell(c).Contains(filter, StringComparison.OrdinalIgnoreCase)))];
        SectionText = new ObjectDetailSection(section with { Rows = rows }).Text;
        VisibleRows = rows;
    }

    private void RefreshPreview()
    {
        if (string.IsNullOrWhiteSpace(ParameterName))
        {
            PreviewDdl = string.Empty;
            PreviewError = string.Empty;
            return;
        }

        try
        {
            PreviewDdl = (BuildParameterDdl ?? DefaultBuildParameterDdl)(ParameterName, ParameterValue ?? string.Empty, ParameterScope);
            PreviewError = string.Empty;
        }
        catch (ArgumentException ex)
        {
            PreviewDdl = string.Empty;
            PreviewError = ex.Message;
        }
        catch (NotSupportedException ex)
        {
            // 이 DBMS 에 없는 적용 범위이거나, 이 DBMS 용 구현이 없다.
            PreviewDdl = string.Empty;
            PreviewError = ex.Message;
        }
    }

    private IDatabaseInfoProvider? ResolveProvider()
        => _provider ??= (IDatabaseInfoProvider?)_services?.GetService(typeof(IDatabaseInfoProvider));

    // 세션의 DBMS 용 문장 만들기. 접속이 없거나 구현이 없으면 NotSupportedException(미리보기 오류로 보인다).
    private string DefaultBuildParameterDdl(string name, string value, DbParameterScope scope)
        => (DbmsServices.For<ISchemaStatements>(_services, Session)
                ?? throw new NotSupportedException(Session is null
                    ? "접속이 없어 파라미터 변경 DDL 을 만들 수 없다"
                    : $"이 DBMS({Session.Profile.Dbms}) 용 파라미터 변경 DDL 구현이 없다"))
            .ParameterChangeDdl(name, value, (Oranda.Core.Schema.ParameterScope)(int)scope);
}
