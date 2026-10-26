using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>결과 목록의 한 줄(파일 줄 또는 내용 줄). 엔터·두 번 누름이 여는 대상.</summary>
public abstract record ScriptSearchRow(string FullPath, int Line);

/// <summary>결과의 파일 줄: 이름 + 흐린 상대 폴더 + (내용 검색이면) 일치 수.</summary>
public sealed record ScriptSearchFileRow(string FullPath, string Name, string Folder, int MatchCount) : ScriptSearchRow(FullPath, 0)
{
    public bool HasCount => MatchCount > 0;
}

/// <summary>결과의 내용 줄: 줄 번호(오른쪽 정렬) + 미리보기와 강조 위치.</summary>
public sealed record ScriptSearchLineRow(string FullPath, int Line, string Preview, IReadOnlyList<ScriptMatchRange> Ranges) : ScriptSearchRow(FullPath, Line);

/// <summary>검색 결과를 파일의 어느 줄에서 열라는 요청(편집기가 받는다).</summary>
public sealed record ScriptOpenAt(string Path, int Line);

// 스크립트 창 검색(TSK-10-01). 입력이 비면 트리 그대로, 입력하면 트리 대신 결과 목록(IsSearchActive).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "검색 취소 토큰은 타이머가 없어 정리할 핸들이 없고, 새 입력 때 취소만 한다.")]
public sealed partial class ScriptsViewModel
{
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    private string _searchText = string.Empty;

    /// <summary>켜면 파일 이름뿐 아니라 파일 안의 글도 찾는다.</summary>
    [ObservableProperty]
    private bool _searchContent;

    [ObservableProperty]
    private IReadOnlyList<ScriptSearchRow> _results = [];

    [ObservableProperty]
    private ScriptSearchRow? _selectedResult;

    [ObservableProperty]
    private bool _isSearchRunning;

    /// <summary>결과가 없을 때 목록 자리에 보이는 안내(다음 할 일). 결과가 있으면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyHint))]
    private string? _emptyHint;

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasEmptyHint => !string.IsNullOrEmpty(EmptyHint);

    /// <summary>입력이 멈춘 뒤 검색을 시작하기까지 기다리는 시간(ms).</summary>
    public int SearchDebounceMilliseconds { get; set; } = 250;

    /// <summary>가장 최근에 시작한 검색(시험이 끝을 기다린다). 취소된 검색은 조용히 끝난다.</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>결과 줄을 파일의 줄 위치로 열라는 요청. 받는 곳이 없으면 <see cref="OpenRequested"/>(줄 없이) 로 간다.</summary>
    public event EventHandler<ScriptOpenAt>? OpenAtRequested;

    partial void OnSearchTextChanged(string value) => StartSearch();

    partial void OnSearchContentChanged(bool value) => StartSearch();

    /// <summary>검색을 지우고 트리로 돌아간다(Esc).</summary>
    public void ClearSearch() => SearchText = string.Empty;

    /// <summary>폴더 목록이 바뀌었거나 새로 읽었을 때 보이는 결과를 다시 만든다. 검색 중이 아니면 아무 일도 없다.</summary>
    internal void RefreshSearch()
    {
        if (IsSearchActive)
        {
            StartSearch();
        }
        else
        {
            EmptyHint = NoFolderHint();
        }
    }

    /// <summary>트리 자리 안내: 연결한 폴더가 없으면 먼저 폴더를 연결하라고 알린다.</summary>
    private string? NoFolderHint()
        => Roots.Count == 0 ? UiText.Get("Scripts.search.noFolders", "연결한 폴더가 없습니다. 위의 「폴더 연결」 단추로 폴더를 연결하세요.") : null;

    /// <summary>기다리지 않고 지금 검색한다(엔터·아래 화살표). 끝날 때까지 기다리는 작업을 돌려준다.</summary>
    public Task SearchNowAsync()
    {
        if (IsSearchActive && IsSearchRunning)
        {
            StartSearch(immediate: true); // 입력 멈춤을 기다리지 않고 바로
        }

        return PendingSearch;
    }

    private void StartSearch(bool immediate = false)
    {
        _searchCts?.Cancel();
        if (!IsSearchActive)
        {
            _searchCts = null;
            PendingSearch = Task.CompletedTask;
            IsSearchRunning = false;
            Results = [];
            SelectedResult = null;
            EmptyHint = NoFolderHint();
            StatusMessage = FolderStatus();
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        IsSearchRunning = true;
        StatusMessage = UiText.Get("Scripts.search.running", "검색 중…");
        PendingSearch = RunSearchAsync(SearchText, SearchContent, EnsureList().Folders.ToArray(), immediate ? 0 : SearchDebounceMilliseconds, cts.Token);
    }

    private string FolderStatus() => Roots.Count == 0 ? "폴더를 연결하세요" : $"폴더 {Roots.Count}개 연결됨";

    private async Task RunSearchAsync(string text, bool content, string[] folders, int delay, CancellationToken ct)
    {
        try
        {
            if (delay > 0)
            {
                await Task.Delay(delay, ct).ConfigureAwait(true);
            }

            var progress = new Progress<ScriptSearchProgress>(p =>
            {
                if (!ct.IsCancellationRequested)
                {
                    StatusMessage = UiText.Format("Scripts.search.runningFiles", "검색 중… {0}개 파일", p.FilesScanned);
                }
            });
            var result = await ScriptSearch.RunAsync(new ScriptSearchQuery(text, content), folders, progress, ct: ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            ShowResult(result, text, content, folders.Length);
        }
        catch (OperationCanceledException)
        {
            // 새 입력이 오면 앞 검색은 버린다.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            IsSearchRunning = false;
            Results = [];
            EmptyHint = null;
            StatusMessage = UiText.Format("Scripts.search.failed", "검색하지 못했다: {0}", ex.Message.ReplaceLineEndings(" "));
        }
    }

    internal void ShowResult(ScriptSearchResult result, string text, bool content, int folderCount)
    {
        var rows = new List<ScriptSearchRow>(result.FileCount + result.MatchCount);
        foreach (var file in result.Files)
        {
            rows.Add(new ScriptSearchFileRow(file.Path, file.Name, file.Folder, file.Lines.Count));
            foreach (var line in file.Lines)
            {
                rows.Add(new ScriptSearchLineRow(file.Path, line.LineNumber, line.Preview, line.Ranges));
            }
        }

        Results = rows;
        SelectedResult = null;
        IsSearchRunning = false;
        EmptyHint = rows.Count > 0 ? null
            : folderCount == 0 ? NoFolderHint() ?? UiText.Get("Scripts.search.noFolders", "연결한 폴더가 없습니다. 위의 「폴더 연결」 단추로 폴더를 연결하세요.")
            : content ? UiText.Get("Scripts.search.noContentMatch", "일치하는 스크립트가 없습니다. 낱말을 줄이거나 다른 글자로 찾아 보세요.")
            : UiText.Get("Scripts.search.noNameMatch", "일치하는 스크립트가 없습니다. 파일 안의 글까지 찾으려면 「내용」을 켜세요.");

        var files = result.FileCount;
        var summary = content
            ? UiText.Format("Scripts.search.filesMatches", "{0}개 파일에서 {1}곳", files, result.MatchCount)
            : UiText.Format("Scripts.search.files", "{0}개 파일", files);
        if (result.LimitReached)
        {
            summary += UiText.Get("Scripts.search.limit", " (한도 도달)");
        }

        if (content && result.SkippedLarge > 0)
        {
            summary += " · " + UiText.Format("Scripts.search.skipped", "2 MB 넘는 파일 {0}개 건너뜀", result.SkippedLarge);
        }

        if (result.SkippedUnreadable > 0)
        {
            summary += " · " + UiText.Format("Scripts.search.unreadable", "읽지 못한 항목 {0}개", result.SkippedUnreadable);
        }

        StatusMessage = summary;
    }

    /// <summary>입력 칸 아래 첫 번째로 열 결과(내용 줄이 있으면 첫 내용 줄, 없으면 첫 파일 줄).</summary>
    public ScriptSearchRow? FirstResult => Results.OfType<ScriptSearchLineRow>().FirstOrDefault() ?? (Results.Count > 0 ? Results[0] : null);

    /// <summary>결과 줄을 연다. 내용 줄은 그 줄로 이동한다. 작업 공간·.xml 은 파일 열기 경로를 따른다(줄 이동 없음).</summary>
    public async Task OpenResultAsync(ScriptSearchRow? row, CancellationToken ct = default)
    {
        if (row is null)
        {
            return;
        }

        if (row.Line > 0 && !OpensWithFileCommand(row.FullPath) && OpenAtRequested is { } handler)
        {
            handler(this, new ScriptOpenAt(row.FullPath, row.Line));
            return;
        }

        await OpenAsync(new ScriptNode(Path.GetFileName(row.FullPath), row.FullPath, false), ct).ConfigureAwait(true);
    }
}
