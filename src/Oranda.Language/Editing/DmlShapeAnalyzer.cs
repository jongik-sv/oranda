using System.Collections.Frozen;
using Oranda.Core.Composition;
using Oranda.Core.Rollback;
using Oranda.Language.Lexing;

namespace Oranda.Language.Editing;

/// <summary>
/// 쓰기 문장의 모양(설계 13.2, RB-30). 토큰으로 최상위 절만 본다 — 주석·문자열·괄호 안(서브쿼리)의 WHERE 는 건너뛴다.
/// 이름 규칙은 변경 일지(<c>ChangeTarget</c>)와 같다: 따옴표 없는 이름은 대문자, 따옴표 이름은 따옴표를 벗겨 그대로.
/// 원복 대상이 아닌 문장(설계 6.3 마지막 줄)은 <see cref="DmlShape.NotTarget"/> 에 이유를 적는다. 예외를 던지지 않는다.
/// </summary>
[OrandaService(typeof(IDmlAnalyzer))]
public sealed class DmlShapeAnalyzer : IDmlAnalyzer
{
    public const string NotTargetMerge = "MERGE";
    public const string NotTargetPlSql = "PL/SQL";
    public const string NotTargetDbLink = "DB 링크";
    public const string NotTargetInlineView = "인라인 뷰 대상";
    public const string NotTargetReturning = "RETURNING 절이 이미 있음";
    public const string NotTargetLogErrors = "LOG ERRORS 절이 이미 있음";
    public const string NotTargetMultiRow = "여러 행 VALUES";
    public const string NotTargetMultiTable = "INSERT ALL";
    public const string NotTargetUnknown = "문장 모양을 알 수 없음";

    /// <summary>표 이름 뒤에서 별칭이 될 수 없는 낱말(절의 시작).</summary>
    private static readonly FrozenSet<string> ClauseWords = new[]
    {
        "SET", "WHERE", "VALUES", "SELECT", "WITH", "RETURNING", "RETURN", "LOG", "USING", "ON", "PARTITION", "SUBPARTITION",
        "DEFAULT",
    }.ToFrozenSet(StringComparer.Ordinal);

    public DmlShape? Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return new Reader(text).Analyze();
        }
        catch (Exception)
        {
            return null; // 계약: 예외를 던지지 않는다. 모양을 모르는 쓰기 문장은 공급자가 「대상 아님」으로 다룬다.
        }
    }

    /// <summary>주석·힌트를 뺀 토큰을 앞에서부터 읽는다.</summary>
    private sealed class Reader
    {
        private readonly string _text;
        private readonly List<Token> _t;
        private int _i;

        public Reader(string text)
        {
            _text = text;
            _t = [.. SqlLexer.Tokenize(text).Where(t => t.Kind is not (TokenKind.Comment or TokenKind.Hint))];
            while (_t.Count > 0 && (IsPunct(_t[^1], ';') || IsOperator(_t[^1], '/')))
            {
                _t.RemoveAt(_t.Count - 1);
            }
        }

        public DmlShape? Analyze()
        {
            switch (Word(0))
            {
                case "UPDATE":
                    _i = 1;
                    return Update();
                case "DELETE":
                    _i = 1;
                    return Delete();
                case "INSERT":
                    _i = 1;
                    return Insert();
                case "MERGE":
                    _i = 1;
                    return Merge();
                case "BEGIN" or "DECLARE" or "CALL" or "EXEC" or "EXECUTE":
                    return new DmlShape(DmlVerb.PlSql) { NotTarget = NotTargetPlSql };
                case null when _t.Count > 0 && _text[_t[0].Start] == '<': // <<라벨>> BEGIN …
                    return new DmlShape(DmlVerb.PlSql) { NotTarget = NotTargetPlSql };
                default:
                    return null; // SELECT·DDL·트랜잭션 제어·LOCK TABLE 은 쓰기 문장이 아니다(LOCK 은 행을 바꾸지 않는다)
            }
        }

        private DmlShape Update()
        {
            var shape = Target(DmlVerb.Update);
            if (shape.NotTarget is not null && shape.Name is null)
            {
                return shape;
            }

            if (Word(_i) != "SET")
            {
                return shape with { NotTarget = shape.NotTarget ?? NotTargetUnknown };
            }

            _i++;
            var set = new List<string>();
            while (_i < _t.Count && !IsClauseEnd(_i))
            {
                if (IsPunct(_t[_i], '('))
                {
                    // (a, b) = (SELECT …)
                    _i++;
                    while (_i < _t.Count && !IsPunct(_t[_i], ')'))
                    {
                        if (SetColumn(shape, out var nested) is { } c && !nested)
                        {
                            set.Add(c);
                        }
                        else
                        {
                            return shape with { NotTarget = shape.NotTarget ?? NotTargetUnknown };
                        }
                    }

                    _i++; // 닫는 괄호
                }
                else if (Word(_i) == "VALUE" && _i + 1 < _t.Count && IsPunct(_t[_i + 1], '('))
                {
                    return shape with { NotTarget = shape.NotTarget ?? NotTargetUnknown }; // 객체 표의 SET VALUE(e) = …
                }
                else if (SetColumn(shape, out var nested) is { } c && !nested)
                {
                    set.Add(c);
                }
                else
                {
                    return shape with { NotTarget = shape.NotTarget ?? NotTargetUnknown };
                }

                SkipToTopLevel(',');
                if (_i < _t.Count && IsPunct(_t[_i], ','))
                {
                    _i++;
                }
            }

            return Tail(shape with { SetColumns = set });
        }

        private DmlShape Delete()
        {
            if (Word(_i) == "FROM")
            {
                _i++;
            }

            var shape = Target(DmlVerb.Delete);
            return shape.NotTarget is not null && shape.Name is null ? shape : Tail(shape);
        }

        private DmlShape Insert()
        {
            if (Word(_i) is "ALL" or "FIRST")
            {
                return new DmlShape(DmlVerb.InsertSelect) { NotTarget = NotTargetMultiTable };
            }

            if (Word(_i) != "INTO")
            {
                return new DmlShape(DmlVerb.Other) { NotTarget = NotTargetUnknown };
            }

            _i++;
            var shape = Target(DmlVerb.InsertValues);
            if (shape.NotTarget is not null && shape.Name is null)
            {
                return shape;
            }

            var cols = new List<string>();
            if (_i < _t.Count && IsPunct(_t[_i], '(') && Word(_i + 1) is not ("SELECT" or "WITH"))
            {
                _i++;
                while (_i < _t.Count && !IsPunct(_t[_i], ')'))
                {
                    if (ColumnName() is { } c)
                    {
                        cols.Add(c);
                    }
                    else
                    {
                        _i++;
                    }
                }

                _i++;
            }

            shape = shape with { InsertColumns = cols };
            if (Word(_i) == "VALUES")
            {
                _i++;
                SkipGroup();
                if (_i < _t.Count && IsPunct(_t[_i], ','))
                {
                    shape = shape with { NotTarget = shape.NotTarget ?? NotTargetMultiRow };
                    while (_i < _t.Count && IsPunct(_t[_i], ','))
                    {
                        _i++;
                        SkipGroup();
                    }
                }

                return Tail(shape);
            }

            if (Word(_i) is "SELECT" or "WITH" || (_i < _t.Count && IsPunct(_t[_i], '(')))
            {
                shape = shape with { Verb = DmlVerb.InsertSelect };
                SkipToTopLevelWord("RETURNING", "RETURN", "LOG"); // SELECT 의 WHERE 는 문장 WHERE 가 아니다
                return Tail(shape);
            }

            return shape with { NotTarget = shape.NotTarget ?? NotTargetUnknown };
        }

        private DmlShape Merge()
        {
            if (Word(_i) != "INTO")
            {
                return new DmlShape(DmlVerb.Merge) { NotTarget = NotTargetMerge };
            }

            _i++;
            var shape = Target(DmlVerb.Merge);
            return shape with { NotTarget = NotTargetMerge, WhereText = null };
        }

        /// <summary>표 참조(이름·DB 링크·파티션 절·별칭)를 읽는다. 인라인 뷰면 이름 없이 대상 아님.</summary>
        private DmlShape Target(DmlVerb verb)
        {
            if (Word(_i) == "ONLY" && _i + 1 < _t.Count && IsPunct(_t[_i + 1], '('))
            {
                _i++;
            }

            if (_i >= _t.Count)
            {
                return new DmlShape(verb) { NotTarget = NotTargetUnknown };
            }

            var start = _t[_i].Start;
            if (Word(_i) is "TABLE" or "THE" && _i + 1 < _t.Count && IsPunct(_t[_i + 1], '('))
            {
                _i++;
                SkipGroup();
                return new DmlShape(verb) { NotTarget = NotTargetInlineView, Alias = Alias(), TableText = Slice(start) }; // 컬렉션 식 대상
            }

            if (IsPunct(_t[_i], '('))
            {
                var inner = _i + 1;
                if (Word(inner) is "SELECT" or "WITH")
                {
                    SkipGroup();
                    var alias = Alias();
                    return new DmlShape(verb) { NotTarget = NotTargetInlineView, Alias = alias, TableText = Slice(start) };
                }

                _i++; // ONLY ( name )
            }

            var parts = NameParts();
            if (parts is null || parts.Count > 2)
            {
                return new DmlShape(verb) { NotTarget = NotTargetUnknown };
            }

            if (_i < _t.Count && IsPunct(_t[_i], ')'))
            {
                _i++; // ONLY ( name ) 의 닫는 괄호
            }

            string? link = null;
            if (_i < _t.Count && IsOperator(_t[_i], '@'))
            {
                _i++;
                var linkParts = NameParts();
                link = linkParts is null ? string.Empty : string.Join('.', linkParts);
            }

            if (Word(_i) is "PARTITION" or "SUBPARTITION")
            {
                _i++;
                if (Word(_i) == "FOR")
                {
                    _i++;
                }

                SkipGroup();
            }

            var a = Alias();
            return new DmlShape(verb)
            {
                Owner = parts.Count == 2 ? parts[0] : null,
                Name = parts[^1],
                DbLink = link,
                TableText = Slice(start),
                Alias = a,
                NotTarget = link is null ? null : NotTargetDbLink,
            };
        }

        /// <summary>WHERE(최상위)와 RETURNING·LOG ERRORS 를 본다.</summary>
        private DmlShape Tail(DmlShape shape)
        {
            string? where = null;
            string? notTarget = shape.NotTarget;
            while (_i < _t.Count)
            {
                var w = Word(_i);
                if (w == "WHERE")
                {
                    _i++;
                    var from = _i;
                    SkipToTopLevelWord("RETURNING", "RETURN", "LOG");
                    if (_i > from)
                    {
                        where = _text[_t[from].Start.._t[_i - 1].End].Trim();
                    }

                    continue;
                }

                if (w is "RETURNING" or "RETURN")
                {
                    notTarget ??= NotTargetReturning;
                    break;
                }

                if (w == "LOG" && Word(_i + 1) == "ERRORS")
                {
                    notTarget ??= NotTargetLogErrors;
                    break;
                }

                if (_i < _t.Count && IsPunct(_t[_i], '('))
                {
                    SkipGroup();
                }
                else
                {
                    _i++;
                }
            }

            return shape with { WhereText = where, NotTarget = notTarget };
        }

        private string? Alias()
        {
            if (Word(_i) == "AS")
            {
                _i++;
            }

            if (_i < _t.Count && _t[_i].Kind is TokenKind.Identifier or TokenKind.QuotedIdent or TokenKind.Keyword
                && !(Word(_i) is { } w && ClauseWords.Contains(w)))
            {
                var name = Normalize(_t[_i]);
                _i++;
                return name;
            }

            return null;
        }

        /// <summary><c>a.b</c> 모양 이름. 이름이 아니면 null.</summary>
        private List<string>? NameParts()
        {
            var parts = new List<string>();
            while (_i < _t.Count && IsName(_t[_i]))
            {
                parts.Add(Normalize(_t[_i]));
                _i++;
                if (_i < _t.Count && IsPunct(_t[_i], '.'))
                {
                    _i++;
                    if (_i >= _t.Count || !IsName(_t[_i]))
                    {
                        return null;
                    }

                    continue;
                }

                break;
            }

            return parts.Count == 0 ? null : parts;
        }

        /// <summary>
        /// SET 대상 열 하나. 한정자는 별칭·표 이름(<c>e.sal</c>, <c>emp.sal</c>, <c>scott.emp.sal</c>)일 때만 떼어 내고,
        /// 그 밖의 여러 부분 이름(객체 속성 <c>obj.attr</c>)은 <paramref name="nested"/> 로 알린다(바뀐 열을 정확히 알 수 없다).
        /// </summary>
        private string? SetColumn(DmlShape shape, out bool nested)
        {
            var save = _i;
            nested = false;
            if (NameParts() is not { } parts)
            {
                _i = save;
                return null;
            }

            nested = !(parts.Count == 1
                       || (parts.Count == 2 && (parts[0] == shape.Alias || parts[0] == shape.Name))
                       || (parts.Count == 3 && parts[0] == shape.Owner && parts[1] == shape.Name));
            if (_i < _t.Count && IsPunct(_t[_i], ','))
            {
                _i++;
            }

            return parts[^1];
        }

        /// <summary>열 이름 하나(별칭 한정자 <c>e.sal</c> 는 떼어 낸다)와 뒤 쉼표를 읽는다. 이름이 아니면 null 이고 위치는 그대로.</summary>
        private string? ColumnName()
        {
            var save = _i;
            if (NameParts() is not { } parts)
            {
                _i = save;
                return null;
            }

            if (_i < _t.Count && IsPunct(_t[_i], ','))
            {
                _i++;
            }

            return parts[^1];
        }

        private bool IsClauseEnd(int i) => Word(i) is "WHERE" or "RETURNING" or "RETURN" || (Word(i) == "LOG" && Word(i + 1) == "ERRORS");

        /// <summary>최상위에서 <paramref name="punct"/> 또는 절 끝을 만날 때까지 넘긴다.</summary>
        private void SkipToTopLevel(char punct)
        {
            var depth = 0;
            while (_i < _t.Count)
            {
                var t = _t[_i];
                if (depth == 0 && (IsPunct(t, punct) || IsClauseEnd(_i)))
                {
                    return;
                }

                if (IsPunct(t, '('))
                {
                    depth++;
                }
                else if (IsPunct(t, ')'))
                {
                    depth--;
                }

                _i++;
            }
        }

        private void SkipToTopLevelWord(params string[] words)
        {
            var depth = 0;
            while (_i < _t.Count)
            {
                var t = _t[_i];
                if (depth == 0 && Word(_i) is { } w && words.Contains(w) && (w != "LOG" || Word(_i + 1) == "ERRORS"))
                {
                    return;
                }

                if (IsPunct(t, '('))
                {
                    depth++;
                }
                else if (IsPunct(t, ')'))
                {
                    depth--;
                }

                _i++;
            }
        }

        /// <summary>지금 자리가 여는 괄호면 짝이 맞는 닫는 괄호 뒤로 넘긴다.</summary>
        private void SkipGroup()
        {
            if (_i >= _t.Count || !IsPunct(_t[_i], '('))
            {
                return;
            }

            var depth = 0;
            while (_i < _t.Count)
            {
                if (IsPunct(_t[_i], '('))
                {
                    depth++;
                }
                else if (IsPunct(_t[_i], ')') && --depth == 0)
                {
                    _i++;
                    return;
                }

                _i++;
            }
        }

        private string Slice(int start) => _i == 0 ? string.Empty : _text[start.._t[_i - 1].End].Trim();

        private string? Word(int i)
            => i < _t.Count && _t[i].Kind is TokenKind.Keyword or TokenKind.Identifier
                ? _text.Substring(_t[i].Start, _t[i].Length).ToUpperInvariant()
                : null;

        private bool IsPunct(Token t, char c) => t.Kind == TokenKind.Punct && t.Length == 1 && _text[t.Start] == c;

        private bool IsOperator(Token t, char c) => t.Kind == TokenKind.Operator && t.Length == 1 && _text[t.Start] == c;

        private static bool IsName(Token t) => t.Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.QuotedIdent;

        private string Normalize(Token t)
        {
            var raw = _text.Substring(t.Start, t.Length);
            if (t.Kind == TokenKind.QuotedIdent && (raw.Length < 2 || raw[^1] != '"'))
            {
                throw new FormatException("닫히지 않은 따옴표 이름"); // Analyze 가 받아 null 로 돌린다
            }

            return t.Kind == TokenKind.QuotedIdent && raw.Length >= 2 && raw[0] == '"'
                ? raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                : raw.ToUpperInvariant();
        }
    }
}
