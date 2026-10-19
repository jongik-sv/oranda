namespace EditorSpike.L1;

public interface ILineSource
{
    int LineCount { get; }
    /// <summary>0 기반 줄 번호의 텍스트(줄바꿈 제외).</summary>
    string GetLine(int index);
}

/// <summary>
/// 줄마다 (시작 상태, 토큰, 끝 상태)를 보관한다. 편집이 오면 바뀐 줄부터 다시 렉싱하고,
/// 편집 범위를 지난 뒤 끝 상태가 예전과 같아지면 멈춘다.
/// </summary>
public sealed class IncrementalLexer
{
    sealed class LineData
    {
        public int StartState;
        public int EndState = -1; // -1 = 아직 렉싱 안 됨(강제 재렉싱)
        public Token[] Tokens = [];
    }

    readonly ILineSource _src;
    readonly List<LineData> _lines = new();
    readonly List<Token> _scratch = new(64);

    public IncrementalLexer(ILineSource src) { _src = src; }

    public int LineCount => _lines.Count;
    public Token[] TokensOf(int line) => _lines[line].Tokens;
    public int EndStateOf(int line) => _lines[line].EndState;

    public void LexAll()
    {
        _lines.Clear();
        int n = _src.LineCount, state = L1Lexer.Normal;
        _lines.Capacity = Math.Max(_lines.Capacity, n);
        for (int i = 0; i < n; i++)
        {
            var d = new LineData { StartState = state };
            state = LexInto(i, d);
            _lines.Add(d);
        }
    }

    /// <summary>
    /// 0 기반 firstLine 부터 oldCount 줄이 newCount 줄로 바뀌었다.
    /// 돌려주는 값 = (다시 렉싱한 줄 수, 다시 렉싱한 마지막 줄 번호).
    /// </summary>
    public (int relexed, int lastLine) ApplyEdit(int firstLine, int oldCount, int newCount)
    {
        if (oldCount > newCount) _lines.RemoveRange(firstLine + newCount, oldCount - newCount);
        else if (newCount > oldCount)
        {
            var add = new LineData[newCount - oldCount];
            for (int k = 0; k < add.Length; k++) add[k] = new LineData();
            _lines.InsertRange(firstLine + oldCount, add);
        }
        for (int k = firstLine; k < firstLine + newCount; k++) _lines[k].EndState = -1;

        int state = firstLine == 0 ? L1Lexer.Normal : _lines[firstLine - 1].EndState;
        int i = firstLine, relexed = 0, editEnd = firstLine + newCount; // 편집된 줄 [firstLine, editEnd)
        int n = _lines.Count;
        while (i < n)
        {
            var d = _lines[i];
            if (i >= editEnd && d.StartState == state && d.EndState != -1) break; // 수렴
            int oldEnd = d.EndState;
            d.StartState = state;
            state = LexInto(i, d);
            relexed++;
            i++;
            if (i >= editEnd && state == oldEnd) break; // 끝 상태가 같으면 다음 줄부터는 그대로
        }
        return (relexed, i - 1);
    }

    int LexInto(int i, LineData d)
    {
        _scratch.Clear();
        int end = L1Lexer.LexLine(_src.GetLine(i), d.StartState, _scratch);
        d.Tokens = _scratch.Count == 0 ? [] : _scratch.ToArray();
        d.EndState = end;
        return end;
    }
}

/// <summary>벤치·검증용 string 목록 줄 원본.</summary>
public sealed class ListLineSource : ILineSource
{
    public readonly List<string> Lines;
    public ListLineSource(IEnumerable<string> lines) { Lines = lines.ToList(); }
    public static ListLineSource FromText(string text) => new(text.Split('\n'));
    public int LineCount => Lines.Count;
    public string GetLine(int index) => Lines[index];
}
