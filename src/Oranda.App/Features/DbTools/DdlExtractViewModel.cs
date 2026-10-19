using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DbTools;

/// <summary>추출할 유형 한 칸(체크박스).</summary>
public sealed partial class DdlTypeChoice : ObservableObject
{
    public DdlTypeChoice(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>추출 결과 한 줄(화면용).</summary>
public sealed record DdlExtractRow(string Title, bool Succeeded, string? Error)
{
    public string Line => Succeeded ? "완료  " + Title : $"실패  {Title} — {Error}";
}

/// <summary>한 번의 추출 요청(화면 → 실행기). Oracle 어셈블리 형식이 화면 코드에 새지 않게 App 쪽 형식으로 둔다.</summary>
public sealed record DdlExtractPlan(
    string Owner,
    IReadOnlyList<string> ObjectTypes,
    string? NamePattern,
    bool InvalidOnly,
    DdlOptions Ddl,
    IReadOnlyList<(string From, string To)> Renames,
    string OutputDirectory,
    bool FilePerObject);

public sealed record DdlExtractOutcome(IReadOnlyList<DdlExtractRow> Rows, IReadOnlyList<string> Files);

/// <summary>
/// <c>oranda.panel.ddlExtract</c> — DDL 일괄 추출 마법사(필터·이름 바꾸기·저장 속성·출력 방식, TSK-07-04).
/// 객체마다 <see cref="IMetadataProvider.GetDdlAsync"/> 를 불러 파일로 쓴다. 상태를 바꾸지 않는 읽기 작업이다.
/// </summary>
[OrandaToolPanel("oranda.panel.ddlExtract", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class DdlExtractViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private IMetadataProvider? _meta;

    public DdlExtractViewModel()
    {
        foreach (var t in DefaultTypes)
        {
            TypeChoices.Add(new DdlTypeChoice(t, true));
        }
    }

    public DdlExtractViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 제공자와 실행기를 직접 넣는다.</summary>
    internal DdlExtractViewModel(IMetadataProvider meta)
        : this()
    {
        _meta = meta;
    }

    /// <summary>의존 순서(Oracle 쪽 <c>DdlBulkExporter.DefaultTypes</c> 와 같다 — 시험이 맞는지 본다).</summary>
    public static IReadOnlyList<string> DefaultTypes { get; } =
    [
        "SEQUENCE", "TYPE", "TABLE", "VIEW", "MATERIALIZED VIEW", "SYNONYM", "FUNCTION", "PROCEDURE",
        "PACKAGE", "PACKAGE BODY", "TYPE BODY", "TRIGGER",
    ];

    /// <summary>시험용: 추출 실행기를 바꾼다(기본 = Oracle 쪽 <c>DdlBulkExporter</c>).</summary>
    internal Func<IMetadataProvider, ISession, DdlExtractPlan, IProgress<(int Done, int Total)>?, CancellationToken, Task<DdlExtractOutcome>> Runner { get; set; }
        = RunWithExporterAsync;

    public ISession? Session { get; private set; }

    public ObservableCollection<DdlTypeChoice> TypeChoices { get; } = [];

    public ObservableCollection<DdlExtractRow> Results { get; } = [];

    [ObservableProperty]
    private string _owner = string.Empty;

    [ObservableProperty]
    private string _nameText = string.Empty;

    [ObservableProperty]
    private bool _invalidOnly;

    // 저장 속성(DdlOptions)
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

    /// <summary>한 줄에 하나, <c>이전=새</c>(스키마·객체 이름 바꾸기). 빈 줄·<c>#</c> 줄은 무시.</summary>
    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    /// <summary>true = 객체마다 파일(<c>유형/이름.sql</c>), false = 스키마 하나에 파일 하나.</summary>
    [ObservableProperty]
    private bool _filePerObject;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isBusy;

    public IReadOnlyList<string> Files { get; private set; } = [];

    public bool CanRun => Session is not null && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    /// <summary>접속을 정하고 스키마·출력 폴더 기본값을 채운다.</summary>
    public void Attach(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        if (string.IsNullOrWhiteSpace(Owner))
        {
            Owner = DbToolsSupport.DefaultOwner(session) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "oranda-ddl");
        }

        OnPropertyChanged(nameof(CanRun));
    }

    public DdlOptions CurrentOptions => new()
    {
        IncludeStorage = IncludeStorage,
        IncludeSegmentAttributes = IncludeSegmentAttributes,
        IncludeTablespace = IncludeTablespace,
        SqlTerminator = SqlTerminator,
        IncludeDependentObjects = IncludeDependentObjects,
    };

    /// <summary><c>이전=새</c> 줄을 읽는다. 형식이 틀린 줄은 건너뛴다.</summary>
    public static IReadOnlyList<(string From, string To)> ParseRenames(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            var from = line[..eq].Trim().Trim('"');
            var to = line[(eq + 1)..].Trim().Trim('"');
            if (from.Length > 0 && to.Length > 0)
            {
                list.Add((from, to));
            }
        }

        return list;
    }

    public DdlExtractPlan BuildPlan() => new(
        Owner.Trim().ToUpperInvariant(),
        [.. TypeChoices.Where(t => t.IsSelected).Select(t => t.Name)],
        SchemaFilter.ToLikePattern(NameText),
        InvalidOnly,
        CurrentOptions,
        ParseRenames(RenameText),
        OutputDirectory.Trim(),
        FilePerObject);

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Session is not { } session)
        {
            StatusText = "접속이 없다";
            return;
        }

        if (string.IsNullOrWhiteSpace(Owner))
        {
            StatusText = "스키마를 입력하세요";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            StatusText = "출력 폴더를 입력하세요";
            return;
        }

        var plan = BuildPlan();
        if (plan.ObjectTypes.Count == 0)
        {
            StatusText = "유형을 하나 이상 고르세요";
            return;
        }

        var meta = _meta ??= (IMetadataProvider?)_services?.GetService(typeof(IMetadataProvider));
        if (meta is null)
        {
            StatusText = "메타데이터 제공자가 없다";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        Files = [];
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total);
            var outcome = await Runner(meta, session, plan, progress, ct);
            foreach (var row in outcome.Rows)
            {
                Results.Add(row);
            }

            Files = outcome.Files;
            var failed = outcome.Rows.Count(r => !r.Succeeded);
            Progress = 100;
            StatusText = outcome.Rows.Count == 0
                ? "조건에 맞는 객체가 없다"
                : $"{outcome.Rows.Count - failed}개 추출" + (failed > 0 ? $", {failed}개 실패" : string.Empty) + $" → 파일 {outcome.Files.Count}개 ({plan.OutputDirectory})";
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
        }
        catch (Exception ex)
        {
            StatusText = "추출하지 못했다: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Oracle 어셈블리 형식은 이 메서드 안에서만 쓴다(처음 부를 때 올라온다).
    private static async Task<DdlExtractOutcome> RunWithExporterAsync(IMetadataProvider meta, ISession session, DdlExtractPlan plan,
                                                                      IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var request = new Oranda.Oracle.Schema.DdlExtractRequest
        {
            Owner = plan.Owner,
            ObjectTypes = plan.ObjectTypes,
            NamePattern = plan.NamePattern,
            InvalidOnly = plan.InvalidOnly,
            Ddl = plan.Ddl,
            Renames = [.. plan.Renames.Select(r => new Oranda.Oracle.Schema.DdlRename(r.From, r.To))],
            OutputDirectory = plan.OutputDirectory,
            Layout = plan.FilePerObject ? Oranda.Oracle.Schema.DdlExtractLayout.FilePerObject : Oranda.Oracle.Schema.DdlExtractLayout.SingleFile,
        };
        var result = await Oranda.Oracle.Schema.DdlBulkExporter.ExtractAsync(meta, session, request, progress, ct);
        return new DdlExtractOutcome(
            [.. result.Items.Select(i => new DdlExtractRow($"{i.Ref.ObjectType} {i.Ref.Owner}.{i.Ref.Name}", i.Succeeded, i.Error))],
            result.Files);
    }
}
