using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

// ConvertDialogViewModel 의 일부: 기존 매퍼 읽기(디바운스·낡은 읽기 버리기)와 제안 id 다시 받기(ConvertDialogViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConvertDialogViewModel
{
    /// <summary>
    /// 기존 매퍼 읽기를 시작한다(새 파일이거나 경로가 비면 비운다). 경로를 칠 때마다 읽지 않도록 디바운스하고, 읽기는 백그라운드에서 한다.
    /// 읽는 동안 이전 읽기는 낡은 것이 되어 결과를 버리고, 확인 단추는 꺼진다.
    /// </summary>
    private void LoadExisting()
    {
        var version = ++_readVersion; // 앞선 읽기는 낡은 것이 된다
        if (Target == ConvertTargetKind.NewFile || string.IsNullOrWhiteSpace(Path))
        {
            IsReadingExisting = false;
            ExistingError = null;
            if (Target == ConvertTargetKind.ExistingMapper)
            {
                Namespace = string.Empty;
            }

            _existingIds = new HashSet<string>(StringComparer.Ordinal);
            ResuggestIds();
            _readTask = Task.CompletedTask;
            return;
        }

        IsReadingExisting = true;
        _readTask = ReadExistingAsync(Path.Trim(), version);
    }

    private async Task ReadExistingAsync(string path, int version)
    {
        await Task.Delay(_readDelay).ConfigureAwait(true);
        if (version != _readVersion)
        {
            return; // 기다리는 동안 경로가 또 바뀌었다
        }

        var read = await Task.Run(() => SafeRead(path)).ConfigureAwait(true);

        // 읽는 동안 경로·대상이 바뀌었으면 낡은 결과라 버린다(UI 문맥으로 돌아온 뒤에 반영한다).
        if (version != _readVersion)
        {
            return;
        }

        _existingIds = new HashSet<string>(read.Error is null ? read.StatementIds : [], StringComparer.Ordinal);
        ExistingError = read.Error;
        Namespace = read.Namespace ?? string.Empty;
        IsReadingExisting = false;
        ResuggestIds();
        Recompute();
    }

    private ExistingMapperRead SafeRead(string path)
    {
        try
        {
            return _readExisting(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new ExistingMapperRead(null, [], ex.Message);
        }
    }

    /// <summary>
    /// 기존 매퍼 id 집합이 바뀌었으면 분석을 다시 불러 제안 id 를 새로 받는다. 손으로 고친 줄은 그대로 둔다.
    /// </summary>
    private void ResuggestIds()
    {
        if (_suggestionBasis.SetEquals(_existingIds))
        {
            return;
        }

        _suggestionBasis = new HashSet<string>(_existingIds, StringComparer.Ordinal);
        var fresh = new Dictionary<(int Tab, int Statement), ConvertCandidate>();
        foreach (var c in _analyze(_tabs, _suggestionBasis))
        {
            fresh[(c.TabIndex, c.StatementIndex)] = c;
        }

        foreach (var line in Lines)
        {
            if (fresh.TryGetValue((line.Candidate.TabIndex, line.Candidate.StatementIndex), out var c))
            {
                line.ApplySuggestedId(c.SuggestedId);
            }
        }
    }
}
