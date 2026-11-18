using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 하위 파티션 LIST 템플릿 한 줄(파티션 추천 설계 PA-11): 이름 · 값 목록(보통 값 <c>A, B</c>, 비우면 DEFAULT). 칸 글은 칸을 벗어날 때
/// <see cref="TableEditSession.UpdateSubpartitionRow"/> 로 모델에 들어간다. 보이던 글 그대로 다시 들어오면 모델을 건드리지 않는다.
/// </summary>
public sealed class SubpartitionRowViewModel : ObservableObject
{
    private SubpartitionTemplateRow _row = new(string.Empty, null);

    internal SubpartitionRowViewModel(TableEditSession session, int index)
    {
        Session = session;
        Index = index;
    }

    public TableEditSession Session { get; }

    /// <summary>템플릿 안 위치(0부터).</summary>
    public int Index { get; }

    public string Name
    {
        get => _row.Name;
        set
        {
            if (!string.Equals(value ?? string.Empty, Name, StringComparison.Ordinal))
            {
                Session.UpdateSubpartitionRow(Index, value ?? string.Empty, null);
            }
        }
    }

    /// <summary>값 목록(보통 값). 빈 칸 = DEFAULT(나머지 전부).</summary>
    public string ValuesText
    {
        get => PartitionBounds.ToPlain(_row.Values);
        set
        {
            if (!string.Equals(value ?? string.Empty, ValuesText, StringComparison.Ordinal))
            {
                Session.UpdateSubpartitionRow(Index, null, string.IsNullOrWhiteSpace(value) ? "DEFAULT" : value);
            }
        }
    }

    internal void Sync(SubpartitionTemplateRow row)
    {
        if (row == _row)
        {
            return;
        }

        _row = row;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ValuesText));
    }
}
