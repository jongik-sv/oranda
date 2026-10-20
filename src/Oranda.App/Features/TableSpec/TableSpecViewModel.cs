using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.TableSpec;

/// <summary>출력 형식 한 칸(콤보 상자).</summary>
public sealed record TableSpecFormatChoice(TableSpecFormat Format, string Title);

/// <summary>
/// <c>oranda.panel.tableSpec</c> — 테이블 정의서 만들기(소유자·이름 패턴 → xlsx·Markdown·HTML, TSK-13-06).
/// 표 문맥 메뉴에서 열면 그 표 하나로 채워지고, 도구 메뉴에서 열면 소유자·이름 패턴으로 고른다. 읽기만 한다.
/// </summary>
[OrandaToolPanel("oranda.panel.tableSpec", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class TableSpecViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private IMetadataProvider? _meta;
    private ITableSpecWriter? _writer;
    private DbObjectRef? _exact;

    public TableSpecViewModel()
    {
    }

    public TableSpecViewModel(IServiceProvider services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>시험용: 제공자와 쓰기 도구를 직접 넣는다.</summary>
    internal TableSpecViewModel(IMetadataProvider meta, ITableSpecWriter writer)
    {
        _meta = meta;
        _writer = writer;
    }

    public static IReadOnlyList<TableSpecFormatChoice> Formats { get; } =
    [
        new(TableSpecFormat.Xlsx, "Excel (.xlsx)"),
        new(TableSpecFormat.Markdown, "Markdown (.md)"),
        new(TableSpecFormat.Html, "HTML (.html)"),
    ];

    public ISession? Session { get; private set; }

    public ObservableCollection<string> Results { get; } = [];

    [ObservableProperty]
    private string _owner = string.Empty;

    [ObservableProperty]
    private string _nameText = string.Empty;

    [ObservableProperty]
    private TableSpecFormatChoice _format = Formats[0];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isBusy;

    public bool CanRun => Session is not null && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    partial void OnOwnerChanged(string value)
    {
        if (_exact is { } e && !string.Equals(value.Trim(), e.Owner, StringComparison.OrdinalIgnoreCase))
        {
            _exact = null;
        }
    }

    /// <summary>이름을 고쳐 쓰면 표 하나 고정이 풀리고 이름 패턴으로 찾는다.</summary>
    partial void OnNameTextChanged(string value)
    {
        if (_exact is { } e && !string.Equals(value, e.Name, StringComparison.Ordinal))
        {
            _exact = null;
        }
    }

    /// <summary>접속을 정한다. 표를 주면 소유자·이름을 그 표로 채우고 그 표 하나만 대상으로 한다.</summary>
    public void Attach(ISession session, DbObjectRef? table = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        if (table is not null)
        {
            Owner = table.Owner;
            NameText = table.Name;
            _exact = table with { SubName = null };
        }
        else if (string.IsNullOrWhiteSpace(Owner))
        {
            Owner = DbToolsSupport.DefaultOwner(session) ?? string.Empty;
        }

        OnPropertyChanged(nameof(CanRun));
    }

    /// <summary>저장 대화상자에 제안할 파일 이름.</summary>
    public string SuggestedFileName
    {
        get
        {
            var stem = _exact is { } e ? $"{e.Owner}_{e.Name}" : string.IsNullOrWhiteSpace(Owner) ? "table" : Owner.Trim().ToUpperInvariant();
            return stem + "_테이블정의서" + Extension(Format.Format);
        }
    }

    public static string Extension(TableSpecFormat format) => format switch
    {
        TableSpecFormat.Xlsx => ".xlsx",
        TableSpecFormat.Markdown => ".md",
        _ => ".html",
    };

    /// <summary>대상 표: 표 하나로 열었으면 그 표, 아니면 소유자·이름 패턴에 맞는 표(<c>TABLE</c> 만).</summary>
    public async Task<IReadOnlyList<DbObjectRef>> ResolveTargetsAsync(CancellationToken ct = default)
    {
        if (_exact is { } e)
        {
            return [e];
        }

        var meta = ResolveMeta();
        if (Session is not { } session || meta is null)
        {
            return [];
        }

        var found = await meta.ListObjectsAsync(session, new ObjectQuery
        {
            Owner = Owner.Trim().ToUpperInvariant(),
            ObjectTypes = ["TABLE"],
            NamePattern = SchemaFilter.ToLikePattern(NameText),
        }, ct);
        return [.. found.OrderBy(o => o.Name, StringComparer.Ordinal)];
    }

    /// <summary>정의서를 만들어 <paramref name="path"/> 에 쓴다. 성공하면 true.</summary>
    public async Task<bool> GenerateAsync(string path, CancellationToken ct = default)
    {
        if (Session is not { } session)
        {
            StatusText = "접속이 없다";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Owner))
        {
            StatusText = "스키마를 입력하세요";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "저장할 파일을 정하세요";
            return false;
        }

        var meta = ResolveMeta();
        var writer = _writer ??= (ITableSpecWriter?)_services?.GetService(typeof(ITableSpecWriter));
        if (meta is null || writer is null)
        {
            StatusText = "메타데이터 제공자가 없다";
            return false;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        try
        {
            var targets = await ResolveTargetsAsync(ct);
            if (targets.Count == 0)
            {
                StatusText = "조건에 맞는 표가 없다";
                return false;
            }

            var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total);
            var title = targets.Count == 1 ? $"{targets[0].Owner}.{targets[0].Name} 테이블 정의서" : $"{targets[0].Owner} 테이블 정의서";
            var doc = await TableSpecBuilder.BuildAsync(meta, session, title, targets, progress, ct);
            // 임시 파일에 쓴 뒤 바꿔 넣는다: 쓰다 실패해도 기존 파일이 남고 반쯤 쓴 파일이 생기지 않는다.
            var temp = path + ".tmp";
            try
            {
                await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await writer.WriteAsync(doc, Format.Format, file, ct);
                }

                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }

            foreach (var t in doc.Tables)
            {
                Results.Add($"{t.Owner}.{t.Name}  열 {t.Columns.Count}개 · 인덱스 {t.Indexes.Count}개");
            }

            Progress = 100;
            StatusText = $"표 {doc.Tables.Count}개 → {path}";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했다";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = "정의서를 만들지 못했다: " + ex.Message.ReplaceLineEndings(" ");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IMetadataProvider? ResolveMeta() => _meta ??= (IMetadataProvider?)_services?.GetService(typeof(IMetadataProvider));
}
