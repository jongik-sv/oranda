using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Formatting;

internal static partial class FormatEngine
{
    /// <summary>
    /// 쿼리 서식 배치(<see cref="ClauseLayout.Column"/>, TSK-13-43). 확장 규칙(<see cref="LayoutRules"/>)의 줄바꿈 뒤에 SELECT·WITH·INSERT·
    /// UPDATE·DELETE 문의 줄과 열을 다시 정한다.
    /// <list type="bullet">
    /// <item>절 키워드는 기준 열, 본문은 기준 열 + 7(키워드가 6자를 넘으면 한 칸 뒤). 앞 쉼표는 기준 열 + 5, 항목은 + 7.</item>
    /// <item>AND·OR 는 기준 열에서 7칸으로 채운다(<c>AND    </c>). 괄호 조건 묶음이 한 줄에 안 들어가면 여는 괄호를 그 자리에 두고 안쪽은 괄호 열 + 들여쓰기,
    /// AND·OR 는 안쪽 열 앞에 오른쪽으로 붙이고, 닫는 괄호는 여는 괄호 열에 혼자 둔다.</item>
    /// <item>서브쿼리·인라인 뷰: 짧고(줄 전체가 최대 폭 안) 단순하고(FROM 표 하나·조건 하나·GROUP BY 등 없음) 원문이 한 줄이면 괄호째 한 줄. 아니면 블록:
    /// 괄호가 항목 처음이면 그 자리(<c>FROM   (</c>·<c>     , (</c>), 아니면 키워드 줄을 끝내고 다음 줄 항목 열에 <c>(</c>.
    /// 안쪽은 괄호 열 + 들여쓰기, 닫는 괄호는 괄호 열(뒤에 별칭).</item>
    /// <item>WITH: <c>WITH 이름 AS</c>, 다음 줄 기준 열에 <c>(</c>, 안쪽 + 들여쓰기, <c>)</c>, 다음 CTE 는 기준 열 <c>, 이름 AS</c>.</item>
    /// <item>CASE: WHEN 이 하나이고 원문이 한 줄이고 줄이 최대 폭 안이면 한 줄. 아니면 WHEN·ELSE 를 CASE 열 + 5, END 를 CASE 열.</item>
    /// <item>GROUP BY·ORDER BY: 여러 줄이면 항목마다 앞 쉼표 줄, 한 줄이면 다 들어갈 때만 한 줄(아니면 항목마다 한 줄).</item>
    /// <item>주석: 줄 끝 주석은 앞 코드 뒤 원래 공백 수 그대로, 자기 줄 주석은 다음 코드 줄 열. 주석·빈 줄 뒤 토큰은 줄 처음에 온다.</item>
    /// </list>
    /// 판단은 토큰 글자·기본 간격·원문의 주석·빈 줄·서브쿼리와 CASE 안 줄바꿈에서만 나오므로 다시 포맷해도 같다.
    /// 구조를 못 맞추면(MERGE·괄호 짝·예상 밖 모양) 그 문장은 건드리지 않고 앞 단계 배치를 둔다.
    /// </summary>
    sealed partial class ColumnLayout
    {
        const int Body = 7;
        const int CommaCol = 5;
        const int CaseStep = 5;
        const int Big = 100_000;

        enum Mode : byte
        {
            Inline,
            NewLine,
            PadTo,
        }

        enum ListMode : byte
        {
            Each,
            Fill,
            Single,
        }

        readonly record struct Place(Mode Mode, int Col)
        {
            public static Place In => new(Mode.Inline, 0);

            public static Place Nl(int col) => new(Mode.NewLine, col);

            public static Place Pad(int col) => new(Mode.PadTo, col);
        }

        sealed record Saved(GapKind Gap, int Depth, int Extra, int LineDepth, int BlankLines, string OrigGap);

        readonly List<FormatItem> _items;
        readonly List<int> _code;
        readonly FormatterOptions _o;
        readonly LineBreakOptions _lb;
        readonly bool[] _bad;
        readonly bool[] _frozen;
        readonly int _unit;
        readonly int _max;
        readonly int _base;
        readonly List<Token> _buf = new(4);

        int[] _t = [];
        int _n;
        int[] _len = [];
        int[] _gw = [];
        GapKind[] _gap = [];
        bool[] _forced = [];
        int[] _match = [];
        int[] _col = [];
        bool[] _brk = [];
        long[] _pre = [];
        int[] _fc = [];
        int _cur;
        int _ec;
        readonly List<List<(int S, int E)>> _alignSets = [];

        public ColumnLayout(FormatLayout layout, bool[] bad, bool[] frozen)
        {
            _items = layout.Items;
            _code = layout.Code;
            _o = layout.Options;
            _lb = _o.LineBreaks;
            _bad = bad;
            _frozen = frozen;
            _unit = Math.Clamp(_o.Indent.Size, 1, 16);
            _max = Math.Clamp(_lb.MaxLineWidth, 40, 400);
            _base = layout.BaseIndent;
        }

        /// <summary>배치한 문장 번호 표시(키워드 오른쪽 맞춤이 건너뛴다).</summary>
        public bool[] Run()
        {
            var handled = new bool[_bad.Length];
            int k = 0;
            while (k < _code.Count)
            {
                int piece = _items[_code[k]].Piece;
                int e = k;
                while (e < _code.Count && _items[_code[e]].Piece == piece) e++;
                if (piece >= 0 && piece < _bad.Length && !_bad[piece]) handled[piece] = TryPiece(k, e);
                k = e;
            }

            return handled;
        }
    }
}
