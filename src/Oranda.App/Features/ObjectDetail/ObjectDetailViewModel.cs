using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>상세 탭 하나(열·제약·인덱스 …). 표를 글자 맞춤 텍스트로 만들어 보여 준다(읽기 전용·복사 가능).</summary>
public sealed class ObjectDetailSection
{
    public ObjectDetailSection(DetailSection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Title = source.Title;
        Columns = source.Columns;
        Rows = source.Rows;
        Text = Format(source);
    }

    public string Title { get; }

    /// <summary>「데이터」 탭(표 대신 데이터 그리드를 보인다, <see cref="ObjectDataViewModel"/>).</summary>
    public bool IsData { get; private init; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }

    /// <summary>열 너비를 맞춘 표 텍스트(첫 줄 = 머리).</summary>
    public string Text { get; }

    /// <summary>「데이터」 탭 항목(표 내용 없음).</summary>
    internal static ObjectDetailSection CreateData() =>
        new(new DetailSection(ObjectDataViewModel.SectionTitle, [], [])) { IsData = true };

    internal static string Cell(object? v) => v switch
    {
        null => string.Empty,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()?.ReplaceLineEndings(" ") ?? string.Empty,
    };

    private static string Format(DetailSection s)
    {
        var table = s.Rows.Select(r => r.Select(Cell).ToArray()).ToList();
        var widths = s.Columns.Select((c, i) => Math.Max(c.Length, table.Count == 0 ? 0 : table.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        var sb = new StringBuilder();

        void Line(IReadOnlyList<string> cells)
        {
            for (var i = 0; i < widths.Length; i++)
            {
                var text = i < cells.Count ? cells[i] : string.Empty;
                sb.Append(text).Append(' ', widths[i] - text.Length);
                if (i < widths.Length - 1)
                {
                    sb.Append("  ");
                }
            }

            sb.AppendLine();
        }

        Line(s.Columns);
        Line([.. widths.Select(w => new string('-', w))]);
        foreach (var r in table)
        {
            Line(r);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// <c>oranda.panel.objectDetail</c> — 객체 상세 탭(열·제약·인덱스·파티션·권한·의존·통계·크기)과 DDL 보기(TSK-07-03),
/// 표·뷰의 「데이터」 탭(<see cref="ObjectDataViewModel"/>).
/// 상세는 <see cref="IMetadataProvider"/> 만 쓴다. 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.objectDetail", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class ObjectDetailViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private IMetadataProvider? _meta;
    private ISession? _session;
    private ObjectDataViewModel? _data;
    private int _generation;
    private bool _opening;

    public ObjectDetailViewModel()
    {
    }

    public ObjectDetailViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 제공자를 직접 넣는다.</summary>
    internal ObjectDetailViewModel(IMetadataProvider meta) => _meta = meta;

    public DbObjectRef? Target { get; private set; }

    public ObservableCollection<ObjectDetailSection> Sections { get; } = [];

    /// <summary>
    /// 「데이터」 탭(표·뷰·머티리얼라이즈드 뷰, 구역 목록의 「열」 바로 뒤). 상세를 열 때는 읽지 않고 그 탭을 처음 고를 때 읽는다.
    /// 실행 쪽은 <see cref="ObjectDetailCommands.DataRunner"/>(상세 문서의 세션), 편집은 서비스의 <see cref="IRowEditor"/>.
    /// </summary>
    public ObjectDataViewModel Data => _data ??= new ObjectDataViewModel(
        s => DataRunner is { } run ? run(s) : ObjectDetailCommands.DataRunner?.Invoke(s),
        () => RowEditor ?? _services?.GetService(typeof(IRowEditor)) as IRowEditor,
        ResolveMeta);

    /// <summary>시험용: 「데이터」 탭의 실행 쪽을 바꾼다(기본 = <see cref="ObjectDetailCommands.DataRunner"/>).</summary>
    internal Func<ISession, ObjectDataRunner?>? DataRunner { get; set; }

    /// <summary>시험용: 「데이터」 탭의 행 편집기를 바꾼다(기본 = 서비스의 <see cref="IRowEditor"/>).</summary>
    internal IRowEditor? RowEditor { get; set; }

    [ObservableProperty]
    private string _title = "객체 상세";

    [ObservableProperty]
    private ObjectDetailSection? _selectedSection;

    /// <summary>true 면 「데이터」 탭이 선택돼 있다(오른쪽에 표 대신 데이터 그리드).</summary>
    [ObservableProperty]
    private bool _isDataSelected;

    /// <summary>true 면 DDL 탭이 선택돼 있다.</summary>
    [ObservableProperty]
    private bool _isDdlSelected;

    [ObservableProperty]
    private string _ddlText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // DDL 옵션(SCH-04). 바뀌면 DDL 을 다시 만든다.
    [ObservableProperty]
    private bool _includeStorage;

    [ObservableProperty]
    private bool _includeSegmentAttributes;

    [ObservableProperty]
    private bool _includeTablespace;

    [ObservableProperty]
    private bool _sqlTerminator = true;

    [ObservableProperty]
    private bool _includeDependentObjects;

    public DdlOptions CurrentOptions => new()
    {
        IncludeStorage = IncludeStorage,
        IncludeSegmentAttributes = IncludeSegmentAttributes,
        IncludeTablespace = IncludeTablespace,
        SqlTerminator = SqlTerminator,
        IncludeDependentObjects = IncludeDependentObjects,
    };

    private string? DdlError { get; set; }

    partial void OnIncludeStorageChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeSegmentAttributesChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeTablespaceChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnSqlTerminatorChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnIncludeDependentObjectsChanged(bool value) => ReloadDdlOnOptionChange();

    partial void OnSelectedSectionChanged(ObjectDetailSection? value)
    {
        IsDataSelected = value is { IsData: true };
        if (IsDataSelected)
        {
            _ = Data.EnsureLoadedAsync(); // 처음 고를 때만 읽는다(실패는 데이터 탭 안내에)
        }
    }

    private void ReloadDdlOnOptionChange()
    {
        if (!_opening && Target is not null && _session is not null)
        {
            _ = LoadDdlAsync();
        }
    }

    /// <summary>객체를 열어 상세와 DDL 을 채운다. 어느 한쪽이 실패해도 다른 쪽은 보여 준다.</summary>
    public async Task OpenAsync(ISession session, DbObjectRef target, bool showDdl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);

        _session = session;
        Target = target;
        Title = $"{target.Owner}.{target.Name}";
        IsDdlSelected = showDdl;
        Sections.Clear();
        SelectedSection = null;
        DdlText = string.Empty;
        DdlError = null;
        Data.Reset(session, target);
        var gen = Interlocked.Increment(ref _generation);

        var meta = ResolveMeta();
        if (meta is null)
        {
            StatusText = "메타데이터 제공자가 없다";
            return;
        }

        IsBusy = true;
        _opening = true;
        try
        {
            string? error = null;
            try
            {
                var detail = await meta.GetObjectDetailAsync(session, target, ct);
                if (gen != _generation)
                {
                    return;
                }

                foreach (var s in detail.Sections)
                {
                    Sections.Add(new ObjectDetailSection(s));
                }

                // 「데이터」 탭은 「열」 바로 뒤(SQL Developer 테이블 편집기의 Columns → Data 순서). 고르기 전에는 읽지 않는다.
                if (ObjectDataViewModel.Supports(target))
                {
                    var columns = Sections.FirstOrDefault(s => s.Title == "열");
                    Sections.Insert(columns is null ? Sections.Count : Sections.IndexOf(columns) + 1, ObjectDetailSection.CreateData());
                }

                SelectedSection = Sections.FirstOrDefault(s => s.Title == "열") ?? Sections.FirstOrDefault(s => !s.IsData);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = "상세를 읽지 못했다: " + ex.Message;
            }

            await LoadDdlCoreAsync(meta, gen, ct);
            if (gen == _generation)
            {
                StatusText = error ?? DdlError ?? $"{target.ObjectType} {Title}";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _opening = false;
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>옵션이 바뀐 뒤 DDL 만 다시 읽는다.</summary>
    public async Task LoadDdlAsync(CancellationToken ct = default)
    {
        if (ResolveMeta() is not { } meta)
        {
            return;
        }

        var gen = _generation;
        IsBusy = true;
        try
        {
            await LoadDdlCoreAsync(meta, gen, ct);
            if (gen == _generation && DdlError is not null)
            {
                StatusText = DdlError;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadDdlCoreAsync(IMetadataProvider meta, int gen, CancellationToken ct)
    {
        if (_session is null || Target is null)
        {
            return;
        }

        try
        {
            var ddl = await meta.GetDdlAsync(_session, Target, CurrentOptions, ct);
            if (gen == _generation)
            {
                DdlText = ddl;
                DdlError = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                DdlText = string.Empty;
                DdlError = "DDL 을 읽지 못했다: " + ex.Message;
            }
        }
    }

    private IMetadataProvider? ResolveMeta()
        => _meta ??= (IMetadataProvider?)_services?.GetService(typeof(IMetadataProvider));
}
