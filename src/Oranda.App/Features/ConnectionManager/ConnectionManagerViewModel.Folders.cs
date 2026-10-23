using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

// ConnectionManagerViewModel 의 일부: 폴더(ConnectionManagerViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConnectionManagerViewModel
{
    // ---- 폴더 ----

    /// <summary>
    /// 새 폴더를 만들고 이름 편집 상태로 둔다. 폴더를 골랐으면 그 안, 접속을 골랐으면 그 접속의 폴더 안, 아니면 맨 위.
    /// 이름은 「새 폴더」, 겹치면 「새 폴더 (2)」.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditFolders))]
    private async Task NewFolderAsync()
    {
        var parent = Selected switch
        {
            { IsFolder: true } folder => folder.Path,
            { Profile: { } p } => ConnectionFolders.Normalize(p.Group),
            _ => null,
        };

        string? created = null;
        if (!await TryEditFoldersAsync(doc =>
            {
                created = ConnectionFolders.Combine(parent, ConnectionFolders.UniqueName(ConnectionFolders.All(doc), parent));
                return ConnectionFolders.Create(doc, created);
            }).ConfigureAwait(true) || created is null)
        {
            return;
        }

        await ReloadAsync(null, created, created).ConfigureAwait(true);
        if (FindFolder(created) is { } node)
        {
            BeginEdit(node);
        }

        SetStatus(StatusKind.Info, $"폴더 '{ConnectionFolders.NameOf(created)}' 을(를) 만들었습니다. 이름을 입력하고 Enter 를 누르세요.");
    }

    private bool CanEditFolders() => !IsBusy;

    /// <summary>고른 폴더의 이름 편집을 시작한다(F2).</summary>
    [RelayCommand(CanExecute = nameof(CanRenameFolder))]
    private void RenameFolder()
    {
        if (Selected is { IsFolder: true } folder)
        {
            BeginEdit(folder);
        }
    }

    private bool CanRenameFolder() => Selected is { IsFolder: true } && !IsBusy;

    private void BeginEdit(ConnectionTreeNode folder)
    {
        folder.EditText = folder.Title;
        folder.IsEditing = true;
        FolderEditRequested?.Invoke(this, folder);
    }

    /// <summary>입력칸의 이름으로 폴더 이름을 바꾼다(Enter·초점 잃음). 안의 접속과 하위 폴더 경로가 함께 바뀐다.</summary>
    public async Task CommitFolderEditAsync(ConnectionTreeNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsEditing)
        {
            return;
        }

        folder.IsEditing = false;
        var name = folder.EditText.Trim();
        if (name == folder.Title)
        {
            return;
        }

        if (!ConnectionFolders.IsValidName(name))
        {
            SetStatus(StatusKind.Error, $"폴더 이름은 비어 있거나 '{ConnectionFolders.Separator}' 를 포함할 수 없습니다.");
            return;
        }

        await MoveFolderAsync(folder.Path, ConnectionFolders.Combine(ConnectionFolders.Parent(folder.Path), name)).ConfigureAwait(true);
    }

    /// <summary>이름 편집을 취소한다(Esc). 폴더는 지금 이름 그대로 남는다.</summary>
    public static void CancelFolderEdit(ConnectionTreeNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        folder.IsEditing = false;
    }

    private async Task DeleteFolderAsync(ConnectionTreeNode folder)
    {
        var path = folder.Path;
        var profiles = _profiles.Count(p => ConnectionFolders.IsSameOrUnder(ConnectionFolders.Normalize(p.Group), path));
        var subfolders = _allFolders.Count(f => ConnectionFolders.IsSameOrUnder(f, path) && !ConnectionFolders.PathEquals(f, path));
        if (profiles + subfolders > 0)
        {
            var where = ConnectionFolders.Parent(path) is { } up ? $"'{up}' 폴더" : "맨 위";
            var ok = Confirm is null || await Confirm($"'{path}' 폴더를 지울까요? 안의 접속 {profiles}개와 하위 폴더는 지우지 않고 {where}(으)로 옮깁니다.").ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }

        if (!await ResolveUnsavedIfInFolderAsync(path).ConfigureAwait(true)
            || !await TryEditFoldersAsync(doc => ConnectionFolders.Delete(doc, path)).ConfigureAwait(true))
        {
            return;
        }

        await ReloadAsync(null, null, ConnectionFolders.Parent(path)).ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"폴더 '{path}' 을(를) 지웠습니다.");
    }

    /// <summary>「폴더로 이동…」: 폴더 목록(「(폴더 없음)」 포함)에서 골라 고른 줄을 옮긴다.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToFolder))]
    private async Task MoveToFolderAsync()
    {
        if (Selected is not { } node || AskFolder is null)
        {
            return;
        }

        var candidates = _allFolders.Where(f => node.IsFolder is false || !ConnectionFolders.IsSameOrUnder(f, node.Path)).ToList();
        var current = node.IsFolder ? ConnectionFolders.Parent(node.Path) : ConnectionFolders.Normalize(node.Profile?.Group);
        if (await AskFolder(candidates, current).ConfigureAwait(true) is { } choice)
        {
            await MoveNodeAsync(node, choice.Path).ConfigureAwait(true);
        }
    }

    private bool CanMoveToFolder() => HasAnySelection && !IsBusy && AskFolder is not null;

    /// <summary>끌어 놓을 수 있는지: 접속은 다른 폴더로만, 폴더는 자기·자기 하위·지금 부모가 아닌 곳으로만.</summary>
    public static bool CanDrop(ConnectionTreeNode node, string? targetFolder)
    {
        ArgumentNullException.ThrowIfNull(node);
        var target = ConnectionFolders.Normalize(targetFolder);
        return node.IsFolder
            ? !ConnectionFolders.IsSameOrUnder(target, node.Path) && !ConnectionFolders.PathEquals(ConnectionFolders.Parent(node.Path), target)
            : !ConnectionFolders.PathEquals(ConnectionFolders.Normalize(node.Profile?.Group), target);
    }

    /// <summary>접속 또는 폴더를 폴더로 옮긴다(끌어 놓기·「폴더로 이동」). <paramref name="targetFolder"/> 가 null 이면 맨 위.</summary>
    public async Task<bool> MoveNodeAsync(ConnectionTreeNode node, string? targetFolder)
    {
        ArgumentNullException.ThrowIfNull(node);
        var target = ConnectionFolders.Normalize(targetFolder);
        if (!CanDrop(node, target))
        {
            if (node.IsFolder && ConnectionFolders.IsSameOrUnder(target, node.Path))
            {
                SetStatus(StatusKind.Error, "폴더를 자기 자신이나 그 하위로 옮길 수 없습니다.");
            }

            return false;
        }

        if (node.Profile is { } profile)
        {
            if (Editor is { IsNew: false, IsDirty: true } editor && editor.Original.Id == profile.Id && !await ResolveUnsavedAsync().ConfigureAwait(true))
            {
                return false;
            }

            if (!await TryEditFoldersAsync(doc => ConnectionFolders.MoveProfile(doc, profile.Id, target)).ConfigureAwait(true))
            {
                return false;
            }

            await ReloadAsync(profile.Id, null, target).ConfigureAwait(true);
            SetStatus(StatusKind.Info, $"'{profile.Name}' 을(를) {(target is null ? "맨 위" : $"'{target}' 폴더")}(으)로 옮겼습니다.");
            return true;
        }

        return await MoveFolderAsync(node.Path, ConnectionFolders.Combine(target, node.Title)).ConfigureAwait(true);
    }

    private async Task<bool> MoveFolderAsync(string from, string to)
    {
        if (!await ResolveUnsavedIfInFolderAsync(from).ConfigureAwait(true)
            || !await TryEditFoldersAsync(doc => ConnectionFolders.Move(doc, from, to)).ConfigureAwait(true))
        {
            return false;
        }

        await ReloadAsync(null, to, ConnectionFolders.Parent(to)).ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"폴더 '{from}' 을(를) '{to}' (으)로 바꿨습니다.");
        return true;
    }

    /// <summary>편집 중인 접속이 폴더 안에 있고 저장 안 한 값이 있으면 먼저 정리한다(폴더가 바뀌면 편집 값의 그룹이 어긋난다).</summary>
    private async Task<bool> ResolveUnsavedIfInFolderAsync(string folderPath) =>
        Editor is not { IsNew: false, IsDirty: true } editor
        || !ConnectionFolders.IsSameOrUnder(ConnectionFolders.Normalize(editor.Original.Group), folderPath)
        || await ResolveUnsavedAsync().ConfigureAwait(true);

    private async Task<bool> TryEditFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit)
    {
        try
        {
            await _service.UpdateFoldersAsync(edit, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is ConnectionValidationException or ConnectionFolderException or System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return false;
        }
    }

    /// <summary>저장소를 다시 읽어 트리를 만들고 지정한 줄을 고른다. <paramref name="reveal"/> 폴더와 그 상위 폴더는 펼친다.</summary>
    private async Task ReloadAsync(Guid? selectProfile, string? selectFolder, string? reveal)
    {
        for (var path = reveal; path is not null; path = ConnectionFolders.Parent(path))
        {
            _collapsed.Remove(path);
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        FileNotice = doc.Notice;
        AdoptDocument(doc.Value);

        // 저장 안 한 편집이 없을 때만 편집기를 저장된 값으로 다시 채운다(옮긴 접속의 그룹 칸이 바로 바뀌게).
        ApplyView(selectProfile, reloadEditor: Editor is not { IsDirty: true }, selectFolder);
    }

    /// <summary>경로의 폴더 줄(없으면 null).</summary>
    public ConnectionTreeNode? FindFolder(string path) => AllNodes().FirstOrDefault(n => n.IsFolder && ConnectionFolders.PathEquals(n.Path, path));

    /// <summary>화면 코드가 상태 줄에 오류를 알린다.</summary>
}
