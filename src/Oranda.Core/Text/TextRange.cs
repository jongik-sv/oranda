namespace Oranda.Core.Text;

/// <summary>문서 안의 범위. 0부터 세는 UTF-16 문자 오프셋과 길이.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}
