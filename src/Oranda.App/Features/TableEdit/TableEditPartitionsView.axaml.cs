using Avalonia.Controls;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 편집 페이지(설계 18.4, 시안 ⑥). DataContext = <see cref="TableEditSession"/>. 도구줄 단추는 세션 동작을 부르고, 칸 글은 칸을 벗어날 때 모델에 들어간다
/// (닫기·실행 앞의 <see cref="TableEditSession.FlushPendingInput"/> 은 이 화면을 품은 <see cref="TableEditView"/> 가 맡는다).
/// </summary>
public partial class TableEditPartitionsView : UserControl
{
    public TableEditPartitionsView()
    {
        InitializeComponent();
        AddPartitionButton.Click += (_, _) => Session?.AddPartition();
        DropPartitionButton.Click += (_, _) => Session?.DropSelectedPartition();
        SplitPartitionButton.Click += (_, _) => Session?.SplitSelectedPartition();
        MergePartitionButton.Click += (_, _) => Session?.MergeSelectedPartition();
        TruncatePartitionButton.Click += (_, _) => Session?.TruncateSelectedPartition();
        RevertPartitionButton.Click += (_, _) => Session?.RevertSelectedPartition();
        FillBoundsButton.Click += (_, _) => Session?.ToggleBoundFill();
        ApplyBoundFillButton.Click += (_, _) => Session?.ApplyBoundFill();
        CloseBoundFillButton.Click += (_, _) =>
        {
            if (Session is { } s)
            {
                s.IsBoundFillOpen = false;
            }
        };
    }

    private TableEditSession? Session => DataContext as TableEditSession;
}
