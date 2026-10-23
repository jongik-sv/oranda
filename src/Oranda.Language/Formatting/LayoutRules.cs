using System.Collections.Frozen;
using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

/// <summary>
/// 확장 규칙 연결(FMT-04·05·06·10). 기본 엔진이 간격·들여쓰기를 정한 뒤 <see cref="ApplyLayoutRules"/> 가 불린다.
/// 규칙은 항목을 더하거나 빼지 않고 <see cref="FormatItem.Gap"/>·<see cref="FormatItem.Depth"/> 만 고친다. 소유: TSK-05-02.
/// </summary>
internal static partial class FormatEngine
{
    static partial void ApplyLayoutRules(FormatLayout layout) => new LayoutRules(layout).Run();

    /// <summary>
    /// 쉼표 위치·연산자/괄호 공백·줄바꿈(SELECT 목록·JOIN·AND/OR·IN 목록·최대 폭)·정렬(별칭·:=·열 정의·=)·제외 구간.
    /// 모든 결정은 토큰 열과 원문의 강제 줄바꿈(빈 줄·주석)에서만 나오므로 출력을 다시 포맷해도 같다.
    /// </summary>
    sealed partial class LayoutRules
    {
        enum ListKind : byte
        {
            None,
            Select,
            Set,
            ColumnDef,
            ByList,
        }

        sealed class Level
        {
            public int Open = -1;
            public bool IsCase;
            public int OwnerDepth;
            public int Clause = -1;
            public string ClauseWord = "";
            public int ClauseDepth;
            public int Cond = -1;
            public int CondDepth;
            public bool Between;
            public ListKind Kind;
            public bool OnePerLine;
            public bool WrapAll;
            public int BreakDepth;
            public int ElemStart = -1;
            public List<(int S, int E)>? Elems;
        }

        static readonly FrozenSet<string> ClauseWords = new[]
        {
            "SELECT","FROM","WHERE","GROUP","ORDER","HAVING","CONNECT","START","UNION","INTERSECT","MINUS","EXCEPT","INTO",
            "VALUES","SET","RETURNING","USING","ON","WHEN","BULK","FETCH","OFFSET","FOR","MODEL","WINDOW",
        }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> CondHeads = new[] { "WHERE", "HAVING", "ON", "START", "CONNECT" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> JoinWords = new[] { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> JoinPrefix = new[] { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER" }.ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> BinaryOps = new[] { "=", "<>", "!=", "^=", "~=", "<", ">", "<=", ">=", "+", "-", "*", "/", "||", ":=", "=>" }
            .ToFrozenSet(StringComparer.Ordinal);

        static readonly FrozenSet<string> TableConstraintWords = new[] { "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "PERIOD", "SUPPLEMENTAL", "LIKE" }
            .ToFrozenSet(StringComparer.Ordinal);

        readonly List<FormatItem> _items;
        readonly List<int> _code;
        readonly FormatterOptions _o;
        readonly LineBreakOptions _lb;
        readonly AlignOptions _al;
        readonly int[] _cpos;
        readonly bool[] _frozen;
        readonly bool[] _mine;
        readonly int[] _nest;
        readonly bool[] _bad;
        readonly bool[] _joinLine;
        readonly bool[] _condLine;
        readonly int _unit;
        readonly int _maxWidth;
        readonly List<Token> _buf = new(4);
        readonly List<(ListKind Kind, List<(int S, int E)> Elems)> _lists = [];
        readonly List<(int First, int Last, int On)> _regions = [];
        readonly FormatLayout _layout;
        bool[] _column = [];

        public LayoutRules(FormatLayout layout)
        {
            _layout = layout;
            _items = layout.Items;
            _code = layout.Code;
            _o = layout.Options;
            _lb = _o.LineBreaks;
            _al = _o.Align;
            int n = _items.Count;
            _cpos = new int[n];
            Array.Fill(_cpos, -1);
            for (int k = 0; k < _code.Count; k++) _cpos[_code[k]] = k;
            _frozen = new bool[n];
            _mine = new bool[n];
            _nest = new int[n];
            _joinLine = new bool[n];
            _condLine = new bool[n];
            int maxPiece = -1;
            foreach (var it in _items) maxPiece = Math.Max(maxPiece, it.Piece);
            _bad = new bool[maxPiece + 1];
            _unit = Math.Clamp(_o.Indent.Size, 1, 16);
            _maxWidth = Math.Clamp(_lb.MaxLineWidth, 40, 400);
        }

        public void Run()
        {
            if (_items.Count == 0) return;
            MarkFrozen();
            MarkBad();
            if (!_o.OperatorSpacing) TightOperators();
            if (_o.ParenInnerSpace) InnerSpaces();
            LayoutPass();
            if (_o.ClauseLayout == ClauseLayout.Column) _column = new ColumnLayout(_layout, _bad, _frozen).Run();
            if (_o.KeywordAlign == KeywordAlign.Right) RiverPass();
            if (_al.Alias) AlignAlias();
            if (_al.Assignment) AlignLines(":=", sameDepth: true);
            if (_al.EqualsSign) AlignLines("=", sameDepth: false);
            if (_al.ColumnDefinition) AlignColumns();
            ApplyFrozen();
        }
    }
}
