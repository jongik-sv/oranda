using System.Collections.Frozen;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Language.Lexing;

/// <summary>문장 토큰으로 <see cref="SqlStatement"/>(분류·동사·바인드·치환 변수·위험 대상)를 만든다.</summary>
internal static class StatementClassifier
{
    static readonly FrozenSet<string> DmlWords = new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "LOCK", "CALL" }
        .ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string> DdlWords = new[]
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "COMMENT", "GRANT", "REVOKE", "ANALYZE", "AUDIT", "NOAUDIT",
        "FLASHBACK", "PURGE", "ASSOCIATE", "DISASSOCIATE",
    }.ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string> TxWords = new[] { "COMMIT", "ROLLBACK", "SAVEPOINT" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>DROP/TRUNCATE 뒤 대상 이름 앞에 오는 종류·수식어(이름이 아님).</summary>
    static readonly FrozenSet<string> ObjectWords = new[]
    {
        "PUBLIC", "TABLE", "VIEW", "MATERIALIZED", "INDEX", "USER", "ROLE", "SYNONYM", "SEQUENCE", "PACKAGE", "BODY",
        "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "DATABASE", "LINK", "TABLESPACE", "CLUSTER", "DIRECTORY", "PROFILE",
        "IF", "EXISTS", "DOMAIN", "SCHEMA", "LIBRARY", "OPERATOR", "CONTEXT", "EDITION", "PLUGGABLE", "JAVA", "SOURCE",
        "CLASS", "RESOURCE", "DIMENSION", "OUTLINE", "FLASHBACK", "ARCHIVE", "RESTORE", "POINT", "SNAPSHOT", "LOG", "ON",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static SqlStatement Build(string text, List<Token> toks, int start, int end, bool blockType)
    {
        string body = text.Substring(start, end - start);
        string? w0 = Word(text, toks, 0), w1 = Word(text, toks, 1);
        var kind = StatementKind.Other;
        bool uncertain = false;
        string? verb = w0;

        if (w0 is null)
        {
            // 괄호로 시작(부분 질의) 또는 VALUES 등
            if (toks[0].Kind == TokenKind.Punct && text[toks[0].Start] == '(') kind = StatementKind.Query;
            else uncertain = true;
        }
        else if (w0 is "SELECT" or "WITH" or "VALUES" or "TABLE") kind = w0 == "TABLE" ? Uncertain(out uncertain) : StatementKind.Query;
        else if (w0 is "DECLARE" or "BEGIN") kind = StatementKind.PlSql;
        else if (w0 == "SET" && w1 is "TRANSACTION" or "CONSTRAINT" or "CONSTRAINTS") { kind = StatementKind.TransactionControl; verb = "SET " + w1; }
        else if (TxWords.Contains(w0)) kind = StatementKind.TransactionControl;
        else if (DmlWords.Contains(w0)) kind = StatementKind.Dml;
        else if (DdlWords.Contains(w0))
        {
            kind = StatementKind.Ddl;
            if (w0 == "ALTER" && w1 == "SYSTEM") verb = "ALTER SYSTEM";
        }
        else if (w0 is "EXPLAIN" or "SET") kind = StatementKind.Other;
        else uncertain = true;

        bool plsqlBlock = blockType;
        var binds = new List<string>();
        var subs = new List<string>();
        int depth = 0;
        bool hasWhere = false;
        foreach (var t in toks)
        {
            switch (t.Kind)
            {
                case TokenKind.Bind when !plsqlBlock || kind == StatementKind.PlSql:
                    AddName(binds, text.Substring(t.Start + 1, t.Length - 1));
                    break;
                case TokenKind.Substitution:
                    {
                        int skip = text[t.Start + 1] == '&' ? 2 : 1;
                        AddName(subs, text.Substring(t.Start + skip, t.Length - skip));
                        break;
                    }

                case TokenKind.Punct:
                    if (text[t.Start] == '(') depth++;
                    else if (text[t.Start] == ')') depth--;
                    break;
                case TokenKind.Keyword or TokenKind.Identifier when depth == 0
                    && text.AsSpan(t.Start, t.Length).Equals("WHERE", StringComparison.OrdinalIgnoreCase):
                    hasWhere = true;
                    break;
            }
        }

        string? danger = null;
        if (kind == StatementKind.Ddl)
        {
            if (verb == "ALTER SYSTEM") danger = "SYSTEM";
            else if (w0 is "DROP" or "TRUNCATE") danger = TargetName(text, toks);
        }

        return new SqlStatement(body, new TextRange(start, end - start), kind)
        {
            Verb = verb,
            BindNames = binds,
            SubstitutionNames = subs,
            IsUnfilteredDml = kind == StatementKind.Dml && w0 is "UPDATE" or "DELETE" && !hasWhere,
            DangerousTarget = danger,
            IsClassificationUncertain = uncertain,
        };
    }

    static StatementKind Uncertain(out bool uncertain)
    {
        uncertain = true;
        return StatementKind.Other;
    }

    static void AddName(List<string> list, string name)
    {
        foreach (var x in list)
        {
            if (string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) return;
        }

        list.Add(name);
    }

    /// <summary>첫 단어를 대문자로(식별자·키워드 토큰만). 아니면 null.</summary>
    static string? Word(string text, List<Token> toks, int i)
    {
        if (i >= toks.Count) return null;
        var t = toks[i];
        return t.Kind is TokenKind.Keyword or TokenKind.Identifier
            ? text.Substring(t.Start, t.Length).ToUpperInvariant()
            : null;
    }

    /// <summary>DROP/TRUNCATE 의 대상 이름(스키마.이름). 종류 단어는 건너뛴다.</summary>
    static string? TargetName(string text, List<Token> toks)
    {
        int i = 1;
        while (i < toks.Count && toks[i].Kind is TokenKind.Keyword or TokenKind.Identifier
               && ObjectWords.Contains(text.Substring(toks[i].Start, toks[i].Length).ToUpperInvariant()))
            i++;
        if (i >= toks.Count) return null;
        var sb = new System.Text.StringBuilder();
        while (i < toks.Count && toks[i].Kind is TokenKind.Keyword or TokenKind.Identifier or TokenKind.QuotedIdent)
        {
            sb.Append(text, toks[i].Start, toks[i].Length);
            if (i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Punct && text[toks[i + 1].Start] == '.') { sb.Append('.'); i += 2; }
            else break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}
