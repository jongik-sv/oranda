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

// ConnectionManagerViewModel 의 일부: 선택·트리 만들기(ConnectionManagerViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConnectionManagerViewModel
{
    partial void OnSelectedChanged(ConnectionTreeNode? oldValue, ConnectionTreeNode? newValue)
    {
        if (_rebuilding || newValue?.Profile is not { } profile)
        {
            // 폴더를 고르거나 트리를 다시 만드는 중이면 편집기를 그대로 둔다.
            return;
        }

        if (Editor is { IsNew: false } current && current.Original.Id == profile.Id)
        {
            return;
        }

        if (Editor is { IsDirty: true })
        {
            PendingSelection = SwitchAfterAskingAsync(profile.Id);
            return;
        }

        Editor = ProfileEditorViewModel.From(profile);
    }

    /// <summary>저장 안 한 편집을 정리(저장·버리기)한 뒤 고른 접속으로 바꾼다. 취소하면 앞의 선택으로 되돌린다.</summary>
    private async Task SwitchAfterAskingAsync(Guid target)
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            SelectQuietly(Editor is { IsNew: false } editor ? FindNode(editor.Original.Id) : null);
            return;
        }

        if (FindNode(target) is { Profile: { } profile } node)
        {
            SelectQuietly(node);
            Editor = ProfileEditorViewModel.From(profile);
        }
    }

    private ConnectionTreeNode? FindNode(Guid id) => AllNodes().FirstOrDefault(n => n.Profile?.Id == id);

    /// <summary>편집기를 바꾸지 않고 트리 선택만 바꾼다.</summary>
    private void SelectQuietly(ConnectionTreeNode? node)
    {
        _rebuilding = true;
        try
        {
            Selected = node;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    partial void OnEditorChanged(ProfileEditorViewModel? oldValue, ProfileEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.KnownGroups = _allFolders;
        }

        UpdateModifiedMarker();
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfileEditorViewModel.IsDirty):
                UpdateModifiedMarker();
                SaveCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ProfileEditorViewModel.HasRequiredFields):
                ConnectCommand.NotifyCanExecuteChanged();
                TestCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>편집 중인 접속 줄에 「저장 안 함」 표시를 붙이거나 뗀다.</summary>
    private void UpdateModifiedMarker()
    {
        var dirtyId = Editor is { IsNew: false, IsDirty: true } editor ? editor.Original.Id : (Guid?)null;
        foreach (var node in AllNodes())
        {
            node.IsModified = node.Profile is { } p && p.Id == dirtyId;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyView(null, reloadEditor: false);

    partial void OnSortOrderChanged(ConnectionSortOrder value) => ApplyView(null, reloadEditor: false);

    /// <summary>찾기 글자가 프로필에 맞는지. 접속 문자열에 호스트·포트·서비스·TNS 별칭이 다 들어 있다. 설명도 찾는다.</summary>
    public static bool Matches(ConnectionProfile profile, string? search)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var text = search.Trim();
        return Contains(profile.Name) || Contains(profile.User) || Contains(profile.DataSource) || Contains(profile.Group) || Contains(profile.ProxyUser)
            || Contains(profile.Comment);

        bool Contains(string? field) => field is not null && field.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>트리 전체 노드(폴더 포함, 위에서 아래 순서).</summary>
    public IEnumerable<ConnectionTreeNode> AllNodes() => Flatten(Tree);

    private static IEnumerable<ConnectionTreeNode> Flatten(IEnumerable<ConnectionTreeNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    private void Rebuild(ConnectionsDocument doc, Guid? select)
    {
        AdoptDocument(doc);
        ApplyView(select, reloadEditor: true);
    }

    private void AdoptDocument(ConnectionsDocument doc)
    {
        _profiles = [.. doc.Profiles];
        _allFolders = ConnectionFolders.All(doc);
        OnPropertyChanged(nameof(AllFolders));
        if (Editor is { } editor)
        {
            editor.KnownGroups = _allFolders;
        }
    }

    /// <summary>
    /// 찾기·정렬·접힘 상태로 트리를 다시 만든다. <paramref name="select"/> 가 없으면 지금 편집 중인 프로필을 다시 고른다.
    /// <paramref name="reloadEditor"/> 가 false 면 편집기(저장 안 한 값 포함)는 그대로 둔다.
    /// <paramref name="selectFolder"/> 가 없고 폴더가 골라져 있었다면 같은 경로의 폴더를 다시 고른다.
    /// </summary>
    private void ApplyView(Guid? select, bool reloadEditor, string? selectFolder = null)
    {
        var keep = select ?? (Editor is { IsNew: false } current ? current.Original.Id : null);
        var keepFolder = selectFolder ?? (select is null && Selected is { IsFolder: true } f ? f.Path : null);
        var filtering = !string.IsNullOrWhiteSpace(SearchText);
        ConnectionTreeNode? selectNode = null;
        ConnectionTreeNode? folderNode = null;
        var visible = _profiles.Where(p => Matches(p, SearchText));
        var ordered = (SortOrder == ConnectionSortOrder.RecentlyConnected
            ? visible.OrderByDescending(p => p.LastConnectedAt.HasValue).ThenByDescending(p => p.LastConnectedAt).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            : visible.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        // 찾는 중에는 맞는 접속이 있는 폴더만, 아니면 빈 폴더까지 모두 보인다.
        IReadOnlyList<string> paths = filtering
            ? ConnectionFolders.All(new ConnectionsDocument { Profiles = ordered })
            : _allFolders;

        _rebuilding = true;
        try
        {
            foreach (var folder in Flatten(Tree).Where(n => n.IsFolder))
            {
                folder.PropertyChanged -= OnFolderChanged;
            }

            Tree.Clear();
            // 폴더는 자식을 다 채운 뒤에 트리에 넣는다(화면이 폴더 줄의 개수 글자를 한 번에 읽는다).
            // 같은 단계에서는 폴더가 위(이름순), 접속이 아래(정렬 선택대로).
            foreach (var path in ChildFolders(paths, null))
            {
                Tree.Add(BuildFolder(path));
            }

            foreach (var p in ordered.Where(p => ConnectionFolders.Normalize(p.Group) is null))
            {
                Tree.Add(NewProfileNode(p));
            }

            Selected = selectNode ?? folderNode;
        }
        finally
        {
            _rebuilding = false;
        }

        // 찾기 글자에 걸려 목록에서 빠진 접속이어도 편집기는 저장된 값으로 다시 채운다(저장 뒤 변경 표시가 남지 않게).
        if (reloadEditor && (selectNode?.Profile ?? _profiles.FirstOrDefault(p => p.Id == keep)) is { } profile)
        {
            Editor = ProfileEditorViewModel.From(profile);
        }

        UpdateModifiedMarker();

        ConnectionTreeNode NewProfileNode(ConnectionProfile p)
        {
            var node = new ConnectionTreeNode(p.Name, p);
            if (p.Id == keep)
            {
                selectNode = node;
            }

            return node;
        }

        ConnectionTreeNode BuildFolder(string path)
        {
            var folder = new ConnectionTreeNode(ConnectionFolders.NameOf(path), null, path);
            foreach (var child in ChildFolders(paths, path))
            {
                folder.Children.Add(BuildFolder(child));
            }

            foreach (var p in ordered.Where(p => ConnectionFolders.PathEquals(ConnectionFolders.Normalize(p.Group), path)))
            {
                folder.Children.Add(NewProfileNode(p));
            }

            folder.ProfileCount = folder.Children.Sum(c => c.IsFolder ? c.ProfileCount : 1);
            if (keepFolder is not null && ConnectionFolders.PathEquals(keepFolder, path))
            {
                folderNode = folder;
            }

            // 찾는 중에는 맞는 접속이 보이도록 모두 펼친다(사용자가 접어 둔 상태는 바꾸지 않는다).
            folder.IsExpanded = filtering || !_collapsed.Contains(path)
                || (selectNode is not null && Flatten(folder.Children).Contains(selectNode))
                || (folderNode is not null && Flatten(folder.Children).Contains(folderNode));
            folder.PropertyChanged += OnFolderChanged;
            return folder;
        }
    }

    /// <summary><paramref name="parent"/> 바로 아래 폴더 경로, 이름순(null = 맨 위).</summary>
    private static IEnumerable<string> ChildFolders(IReadOnlyList<string> paths, string? parent) =>
        paths.Where(p => ConnectionFolders.PathEquals(ConnectionFolders.Parent(p), parent))
            .OrderBy(ConnectionFolders.NameOf, StringComparer.OrdinalIgnoreCase);

    private void OnFolderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_rebuilding || e.PropertyName != nameof(ConnectionTreeNode.IsExpanded) || sender is not ConnectionTreeNode folder || !string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        if (folder.IsExpanded)
        {
            _collapsed.Remove(folder.Path);
        }
        else
        {
            _collapsed.Add(folder.Path);
        }
    }
}
