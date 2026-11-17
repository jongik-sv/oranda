using Avalonia.Controls;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「저장」 편집 페이지(설계 18.2, 시안 ⑦). DataContext = <see cref="TableEditSession"/>. 칸 글은 칸을 벗어날 때 모델에 들어간다
/// (닫기·실행 앞의 <see cref="TableEditSession.FlushPendingInput"/> 은 이 화면을 품은 <see cref="TableEditView"/> 가 맡는다).
/// 줄 객체는 세션이 처음 바인딩될 때 만들고 세션의 다시 계산마다 맞춘다.
/// </summary>
public partial class TableEditStorageView : UserControl
{
    public TableEditStorageView()
    {
        InitializeComponent();
        OpenPartitioningButton.Click += (_, _) => (DataContext as TableEditSession)?.OpenPartitioning();
    }
}
