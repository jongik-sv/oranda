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

// ConnectionManagerViewModel 의 일부: 접속 목록·편집 명령(추가·삭제·복제·가져오기·복사)(ConnectionManagerViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConnectionManagerViewModel
{
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var doc = await _service.LoadAsync(ct).ConfigureAwait(true);
        FileNotice = doc.Notice;
        Rebuild(doc.Value, null);
    }

    [RelayCommand]
    private async Task AddNewAsync()
    {
        if (!await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        SelectQuietly(null);
        Editor = ProfileEditorViewModel.ForNew();
        SetStatus(StatusKind.Info, "새 접속을 입력하세요.");
    }

    /// <summary>
    /// 저장하지 않은 편집이 있으면 저장·버리기·취소를 묻는다. 진행해도 되면 true(저장 성공 또는 버림), 취소하거나 저장에 실패하면 false.
    /// 창을 닫기 전, 새 접속·복제 전, 다른 접속을 고를 때 쓴다.
    /// </summary>
    public async Task<bool> ResolveUnsavedAsync()
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        var name = editor.IsNew ? (string.IsNullOrWhiteSpace(editor.Name) ? "새 접속" : editor.Name.Trim()) : editor.Original.Name;
        var choice = AskUnsaved is null
            ? UnsavedChoice.Discard
            : await AskUnsaved($"'{name}' 의 바뀐 내용을 저장할까요?").ConfigureAwait(true);
        switch (choice)
        {
            case UnsavedChoice.Save:
                return await SaveCurrentAsync().ConfigureAwait(true);
            case UnsavedChoice.Discard:
                Editor = editor.IsNew ? null : ProfileEditorViewModel.From(editor.Original);
                return true;
            default:
                return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is { IsFolder: true } folder)
        {
            await DeleteFolderAsync(folder).ConfigureAwait(true);
            return;
        }

        if (Selected?.Profile is not { } profile)
        {
            return;
        }

        var ok = Confirm is null || await Confirm($"'{profile.Name}' 접속을 지울까요? 저장된 비밀번호도 함께 지웁니다.").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _service.DeleteProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (ConnectionValidationException ex)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return;
        }

        Editor = null;
        await LoadAsync().ConfigureAwait(true);
        SetStatus(StatusKind.Info, $"'{profile.Name}' 접속을 지웠습니다.");
    }

    /// <summary>고른 접속을 복제한다(이름 뒤 「 (사본)」, 비밀번호는 복사하지 않는다). 사본을 골라 둔다.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private async Task DuplicateAsync()
    {
        if (Selected?.Profile is not { } profile || !await ResolveUnsavedAsync().ConfigureAwait(true))
        {
            return;
        }

        ConnectionProfile copy;
        try
        {
            copy = await _service.DuplicateProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ConnectionValidationException or System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusKind.Error, ex.Message);
            return;
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        Rebuild(doc.Value, copy.Id);
        SetStatus(StatusKind.Info, $"'{profile.Name}' 을(를) '{copy.Name}' (으)로 복제했습니다. 비밀번호는 복사하지 않았습니다.");
    }

    /// <summary>접속 가져오기 흐름을 돌리고, 가져왔으면 목록을 다시 읽는다(편집 중인 값은 그대로 둔다).</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (ImportConnections is null || !await ImportConnections().ConfigureAwait(true))
        {
            return;
        }

        var doc = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        FileNotice = doc.Notice;
        AdoptDocument(doc.Value);
        ApplyView(null, reloadEditor: false);
    }

    private bool CanImport() => ImportConnections is not null;

    /// <summary>접속 내보내기 흐름을 돌린다(저장된 접속을 내보내므로 편집 중인 값은 넣지 않는다).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (ExportConnections is not null)
        {
            await ExportConnections().ConfigureAwait(true);
        }
    }

    private bool CanExport() => ExportConnections is not null;

    /// <summary>고른 접속의 저장된 연결 문자열을 클립보드에 넣는다(비밀번호 없음).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyConnectionStringAsync()
    {
        if (Selected?.Profile is { } profile)
        {
            await CopyToClipboardAsync(DataSourceComposer.Describe(profile)).ConfigureAwait(true);
        }
    }

    /// <summary>폼 아래 연결 문자열 미리보기를 클립보드에 넣는다(비밀번호 없음).</summary>
    [RelayCommand]
    private Task CopyPreviewAsync() => CopyToClipboardAsync(Editor?.ConnectionStringPreview ?? string.Empty);

    private async Task CopyToClipboardAsync(string text)
    {
        if (text.Length == 0)
        {
            SetStatus(StatusKind.Error, "복사할 연결 문자열이 없습니다.");
            return;
        }

        if (CopyText is not null)
        {
            await CopyText(text).ConfigureAwait(true);
        }

        SetStatus(StatusKind.Info, "연결 문자열을 복사했습니다(비밀번호는 넣지 않습니다).");
    }
}
