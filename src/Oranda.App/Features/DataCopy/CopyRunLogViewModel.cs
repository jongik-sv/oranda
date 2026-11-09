using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>작업 로그 한 줄(화면 글자와 색 구분).</summary>
public sealed class CopyLogLineViewModel(CopyLogEntry entry)
{
    public CopyLogEntry Entry { get; } = entry;

    public string Text { get; } = CopyLogText.Format(entry);

    public bool IsStatement => Entry.Kind == CopyLogKind.Statement;

    public bool IsWarning => Entry.Kind == CopyLogKind.Warning;

    public bool IsError => Entry.Kind == CopyLogKind.Error;
}

/// <summary>
/// 실행·검증 단계의 「작업 로그」: 실행기가 보낸 문장·결과를 시간 순서로 쌓는다. 메모리 상한 <see cref="MaxLines"/> 줄을 넘으면
/// 앞에서 <see cref="DropChunk"/> 줄씩 버리고 버린 줄 수를 보인다. 복사·저장은 화면에 남은 줄 전체 글이다.
/// </summary>
public sealed partial class CopyRunLogViewModel : ViewModelBase
{
    /// <summary>화면에 남기는 최대 줄 수.</summary>
    public const int MaxLines = 5000;

    /// <summary>상한에 닿으면 한 번에 버리는 줄 수(줄마다 버리면 목록 갱신이 잦다).</summary>
    public const int DropChunk = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropped), nameof(DroppedText))]
    private long _dropped;

    public ObservableCollection<CopyLogLineViewModel> Lines { get; } = [];

    public bool HasLines => Lines.Count > 0;

    public bool HasDropped => Dropped > 0;

    public string DroppedText => HasDropped
        ? string.Create(CultureInfo.InvariantCulture, $"… 앞의 {Dropped:N0}줄은 버렸습니다(최근 {MaxLines:N0}줄까지만 남깁니다).")
        : string.Empty;

    public void Append(CopyLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Lines.Count >= MaxLines)
        {
            for (var i = 0; i < DropChunk && Lines.Count > 0; i++)
            {
                Lines.RemoveAt(0);
            }

            Dropped += DropChunk;
        }

        var had = HasLines;
        Lines.Add(new CopyLogLineViewModel(entry));
        if (!had)
        {
            OnPropertyChanged(nameof(HasLines));
        }
    }

    /// <summary>화면(실행기 밖)에서 남기는 줄: 실행기가 예외로 끝났을 때 등.</summary>
    public void Append(string phase, string text, CopyLogKind kind = CopyLogKind.Info) =>
        Append(new CopyLogEntry(DateTimeOffset.Now, phase, null, text, kind));

    public void Clear()
    {
        Lines.Clear();
        Dropped = 0;
        OnPropertyChanged(nameof(HasLines));
    }

    /// <summary>복사·저장할 전체 글(버린 줄이 있으면 첫 줄에 적는다). 줄 끝은 <see cref="Environment.NewLine"/>.</summary>
    public string AllText()
    {
        var sb = new StringBuilder();
        if (HasDropped)
        {
            sb.AppendLine(DroppedText);
        }

        foreach (var line in Lines)
        {
            sb.AppendLine(line.Text);
        }

        return sb.ToString();
    }
}
