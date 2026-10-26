using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.HistoryScripts;

// 새 스크립트(TSK-10-02): 만들 폴더 판정 · 이름 입력 창 연결 · 만든 뒤 트리에서 고르고 열기.
public sealed partial class ScriptsViewModel
{
    /// <summary>이름 입력 창을 띄우고 만든 파일 경로(취소면 null)를 돌려준다. 보기가 정하고, 시험은 대신 끼운다.</summary>
    public Func<NewScriptRequest, Task<string?>>? NewScriptPrompt { get; set; }

    /// <summary>「내용으로 채우기」에 쓸 활성 편집기 글. 기본은 활성 SQL 탭이다.</summary>
    public Func<string?> ActiveEditorText { get; set; } = () => SqlEditorHost.Current.Active?.Text;

    /// <summary>
    /// 새 스크립트를 만들 폴더(고를 수 없으면 null). 검색 결과 목록이 보이는 중에 결과 줄을 골랐으면 그 파일의 폴더,
    /// 아니면 트리에서 고른 곳(선택 폴더·고른 파일의 폴더·연결 폴더가 하나일 때 그 폴더).
    /// </summary>
    public string? NewScriptFolder
    {
        get
        {
            if (IsSearchActive && SelectedResult is { } row && Path.GetDirectoryName(row.FullPath) is { Length: > 0 } resultFolder)
            {
                return resultFolder;
            }

            var node = SelectedNode;
            return NewScriptFile.ResolveTargetFolder(node?.FullPath, node?.IsDirectory ?? false, Roots.Select(r => r.FullPath).ToList());
        }
    }

    /// <summary>「새 스크립트」 단추·메뉴를 쓸 수 있는지.</summary>
    public bool CanNewScript => NewScriptFolder is not null;

    /// <summary>단추 툴팁: 쓸 수 있으면 무엇을 하는지, 없으면 무엇을 해야 켜지는지.</summary>
    public string NewScriptTip
    {
        get
        {
            if (NewScriptFolder is { } folder)
            {
                return UiText.Format("Scripts.new.tip", "새 스크립트 — {0} 에 .sql 파일을 만든다(로컬 파일만, DB 에는 쓰지 않는다)", folder);
            }

            return Roots.Count == 0
                ? UiText.Get("Scripts.new.tipLink", "스크립트 폴더를 먼저 연결하세요")
                : UiText.Get("Scripts.new.tipPick", "폴더를 먼저 고르세요");
        }
    }

    partial void OnSelectedResultChanged(ScriptSearchRow? value) => NotifyNewScriptState();

    /// <summary>선택·연결 폴더가 바뀌었을 때 단추 상태를 다시 읽게 알린다.</summary>
    private void NotifyNewScriptState()
    {
        OnPropertyChanged(nameof(NewScriptFolder));
        OnPropertyChanged(nameof(CanNewScript));
        OnPropertyChanged(nameof(NewScriptTip));
    }

    /// <summary>
    /// 이름 입력 창을 띄워 새 스크립트를 만들고, 만든 폴더를 다시 읽어 새 파일을 고른 뒤 편집기에서 연다.
    /// 만들 폴더가 없거나 창이 취소되면 아무것도 하지 않는다.
    /// </summary>
    public async Task NewScriptAsync()
    {
        if (NewScriptFolder is not { } folder || NewScriptPrompt is not { } prompt)
        {
            return;
        }

        var fill = ActiveEditorText();
        var request = new NewScriptRequest(
            folder,
            NewScriptFile.SuggestName(folder),
            !string.IsNullOrWhiteSpace(fill),
            (name, useFill) => NewScriptFile.Create(folder, name, useFill ? fill : null));
        if (await prompt(request).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        if (IsSearchActive)
        {
            ClearSearch(); // 결과 목록 대신 트리로 돌아가 새 파일을 고른다(검색 중 상태 문구도 이때 지워진다)
        }

        StatusMessage = UiText.Format("Scripts.new.created", "{0} 을(를) 만들었다", Path.GetFileName(path));
        var node = RevealFile(path);
        if (node is not null)
        {
            SelectedNode = node;
            await OpenAsync(node).ConfigureAwait(true);
        }
        else
        {
            OpenRequested?.Invoke(this, path); // 연결 폴더 밖이어도 파일은 열어 준다
        }
    }

    /// <summary>파일이 든 폴더까지 펼치고 그 폴더를 다시 읽어 파일 노드를 찾는다(트리에 없으면 null).</summary>
    internal ScriptNode? RevealFile(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is null)
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // 가장 깊이 겹치는 연결 폴더(연결 폴더가 서로 안에 들어 있어도 가까운 쪽).
        var root = Roots
            .Where(r => IsInside(r.FullPath, dir, comparison))
            .OrderByDescending(r => r.FullPath.Length)
            .FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        var node = root;
        var relative = Path.GetRelativePath(root.FullPath, dir);
        var segments = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            Expand(node);
            node = node.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, segment, comparison));
            if (node is null)
            {
                return null;
            }
        }

        node.IsExpanded = true;
        node.LoadChildren(); // 새 파일이 보이게 이 폴더만 다시 읽는다
        return node.Children.FirstOrDefault(c => !c.IsDirectory && string.Equals(c.Name, Path.GetFileName(path), comparison));
    }

    private static void Expand(ScriptNode node)
    {
        if (!node.IsExpanded)
        {
            node.IsExpanded = true; // 펼치면 읽는다
        }
        else if (!node.IsLoaded)
        {
            node.LoadChildren();
        }
    }

    private static bool IsInside(string root, string dir, StringComparison comparison)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var d = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(r, d, comparison)
            || (d.StartsWith(r, comparison) && d.Length > r.Length && (d[r.Length] == Path.DirectorySeparatorChar || d[r.Length] == Path.AltDirectorySeparatorChar));
    }
}
