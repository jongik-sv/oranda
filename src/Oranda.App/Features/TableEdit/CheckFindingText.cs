using System.Globalization;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>실행 전 검사 한 줄의 문구(테이블 편집기 설계 10장 표, 시안 화면 ⑤). 편집기 아래 줄·운영 확인 창·검사 창이 같이 쓴다.</summary>
public static class CheckFindingText
{
    /// <summary>판정 표시(✔ / ⚠ / ✖).</summary>
    public static string Glyph(CheckSeverity severity) => severity switch
    {
        CheckSeverity.Block => "✖",
        CheckSeverity.Warning => "⚠",
        _ => "✔",
    };

    /// <summary>한 줄 문구(앞에 판정 표시).</summary>
    /// <param name="purgesOnDrop">DROP 이 휴지통을 거치지 않는 DBMS(Tibero 기본, 설계 12장)</param>
    public static string Format(CheckFinding finding, bool purgesOnDrop = false)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Subject is null && finding.Code is CheckCode.Irreversible && finding.Detail is { Length: > 0 } line)
        {
            return Glyph(finding.Severity) + " " + line; // 이미 문구가 된 줄(파티션 지우기·비우기 행 수)
        }

        var s = finding.Subject is { Length: > 0 } subject ? subject : TableEditText.T("테이블", "table");
        var n = finding.Count?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
        var detail = finding.Detail is { Length: > 0 } d ? DetailText(d) : null;
        var atLeast = finding.Detail == "atLeast";
        if (finding.Severity == CheckSeverity.Ok)
        {
            // ✔ 줄 = 그 검사를 해서 걸리지 않음(Core: 같은 CheckCode + Severity.Ok). 실패 문구를 쓰지 않는다.
            return Glyph(CheckSeverity.Ok) + " " + PassedText(finding.Code, s, detail);
        }

        var text = finding.Code switch
        {
            CheckCode.Passed => s + (detail is null ? string.Empty : ": " + detail),
            CheckCode.Overflow => TableEditText.T($"{s}: 새 길이를 넘는 행 {n}건 — 이대로는 실패합니다", $"{s}: {n} row(s) exceed the new length — this will fail"),
            CheckCode.IncompatibleTypeChange => TableEditText.T($"{s}: 데이터가 있어 형식을 바꿀 수 없습니다", $"{s}: the column has data, so its type cannot change")
                                                + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.NullRows => TableEditText.T($"{s} NOT NULL 추가: NULL 행 {n}건 — 기본값을 넣거나 먼저 채우세요", $"{s} NOT NULL: {n} NULL row(s) — fill them first"),
            CheckCode.DuplicateKeys => TableEditText.T($"{s}: 키가 겹치는 행 {n}건", $"{s}: {n} row(s) with duplicate keys"),
            CheckCode.OrphanRows => TableEditText.T($"{s}: 짝이 없는 행 {n}건", $"{s}: {n} row(s) without a parent"),
            CheckCode.CheckViolations => TableEditText.T($"{s}: 조건을 어기는 행 {n}건", $"{s}: {n} row(s) violate the condition"),
            CheckCode.ExpressionFails => TableEditText.T($"{s}: 식을 계산하지 못했습니다", $"{s}: the expression fails") + (detail is null ? string.Empty : " — " + detail),
            CheckCode.NameExists => TableEditText.T($"이름이 이미 있습니다: {s}", $"The name already exists: {s}"),
            CheckCode.LongLock => TableEditText.T(
                $"행이 많은 테이블입니다(약 {n}행). 형식 변경·NOT NULL·제약·인덱스는 오래 잠글 수 있습니다. 운영은 「의뢰용 스크립트 저장」을 권합니다",
                $"Large table (about {n} rows). Type changes, NOT NULL, constraints and indexes can lock it for long. Saving a request script is recommended on production"),
            CheckCode.Irreversible => TableEditText.T($"{s}: 지우면 되돌릴 수 없습니다", $"{s}: dropping cannot be undone")
                                      + (purgesOnDrop ? TableEditText.T("(휴지통 없이 바로 지워짐)", " (no recycle bin)") : string.Empty)
                                      + Dependents(finding),
            CheckCode.ServerName => TableEditText.T($"{s}: 서버가 지은 이름입니다 — 다른 DB 에서는 이름이 달라 실패합니다", $"{s}: server-generated name — fails on other databases"),
            CheckCode.NotChecked => TableEditText.T($"{s}: 확인 못 함", $"{s}: not checked") + (detail is null ? string.Empty : " — " + detail),
            CheckCode.VirtualRecreated => TableEditText.T($"{s}: 가상 식 변경을 열 지우고 다시 넣기로 합니다", $"{s}: the virtual expression is changed by dropping and re-adding the column"),
            CheckCode.InvalidDefinition => TableEditText.T($"{s}: 서버가 받지 않는 정의입니다", $"{s}: the server does not accept this definition")
                                           + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.DependentsInvalidated => TableEditText.T($"{s}: 이 테이블을 쓰는 뷰·PL/SQL·시노님이 무효가 됩니다", $"{s}: views, PL/SQL and synonyms using this table become invalid")
                                               + Dependents(finding),
            CheckCode.ColumnOrderChanges => TableEditText.T($"{s}: 열 순서가 바뀝니다(맨 뒤로)", $"{s}: the column moves to the end"),
            CheckCode.SettingIgnored => TableEditText.T($"{s}: 쓴 설정을 문장에 넣지 않습니다", $"{s}: this setting is not used") + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.TableRewrite => TableEditText.T(
                finding.Count is null ? "MOVE: 테이블 전체를 다시 씁니다" : $"MOVE: 테이블 전체를 다시 씁니다(약 {n}행)",
                finding.Count is null ? "MOVE: the whole table is rewritten" : $"MOVE: the whole table is rewritten (about {n} rows)"),
            CheckCode.MoveLocksDml => TableEditText.T("이 서버는 ONLINE MOVE 가 안 되어 그동안 DML 이 잠깁니다", "This server cannot MOVE ONLINE — DML is locked meanwhile"),
            CheckCode.ConversionFails => finding.Count is 0
                ? TableEditText.T($"{s}: 변환 안 되는 행 없음", $"{s}: every row converts")
                : TableEditText.T($"{s}: 변환 안 되는 행 {(atLeast ? n + "건 이상" : n + "건")}", $"{s}: {n}{(atLeast ? "+" : string.Empty)} row(s) do not convert")
                  + (detail is null || atLeast ? string.Empty : " — " + detail),
            CheckCode.ConversionNeedsQuiet => TableEditText.T($"{s}: 변환하는 동안 이 테이블에 쓰지 않게 하세요", $"{s}: keep writes off this table while converting")
                                              + (detail is null ? string.Empty : " (" + detail + ")"),
            CheckCode.Unsupported => TableEditText.T($"{s}: 이 서버에서는 할 수 없습니다", $"{s}: not supported on this server") + (detail is null ? string.Empty : " (" + detail + ")"),
            _ => s,
        };
        return Glyph(finding.Severity) + " " + text;
    }

    /// <summary>
    /// Core 가 <see cref="CheckFinding.Detail"/> 에 넣는 까닭 키(<c>foreignKeysInOtherSchemas</c>·<c>droppedColumn:B</c> …)를 문구로. 모르는 글은 그대로(서버 오류 메시지 등).
    /// </summary>
    internal static string DetailText(string detail)
    {
        var colon = detail.IndexOf(':', StringComparison.Ordinal);
        var key = colon > 0 && detail[..colon].All(char.IsAsciiLetter) ? detail[..colon] : detail;
        var arg = colon > 0 && key != detail ? detail[(colon + 1)..] : string.Empty;
        return key switch
        {
            "foreignKeysInOtherSchemas" => TableEditText.T("다른 스키마의 외래 키(사전 권한 없음)", "foreign keys in other schemas (no dictionary privilege)"),
            "timeout" => TableEditText.T("시간 제한을 넘음", "timed out"),
            "afterRun" => TableEditText.T("실행 뒤에 확인", "checked after running"),
            "unknown" => TableEditText.T("행 수 모름", "row count unknown"),
            "atLeast" => TableEditText.T("다 세지 못함", "not fully counted"),
            "droppedColumn" => TableEditText.T($"지우는 열 {arg} 을(를) 씀", $"uses dropped column {arg}"),
            "referencedByForeignKey" => TableEditText.T($"이 열을 참조하는 외래 키가 있음({arg})", $"referenced by foreign key {arg}"),
            "temporaryTablespace" => TableEditText.T("임시 테이블은 임시 테이블스페이스를 씀", "a temporary table uses the temporary tablespace"),
            "temporaryStorage" => TableEditText.T("임시 테이블에는 저장 속성을 쓰지 않음", "storage attributes are not used on a temporary table"),
            "existingStorage" => TableEditText.T("기존 테이블의 STORAGE(INITIAL/NEXT)는 바꾸지 않음", "STORAGE (INITIAL/NEXT) is not changed on an existing table"),
            "virtualColumn" => TableEditText.T("가상 열", "virtual column"),
            "emptyExpression" => TableEditText.T("변환식이 비었음", "the conversion expression is empty"),
            "foreignKeyTypeChange" => TableEditText.T($"외래 키 열의 형식 계열이 바뀜(부모 열과 달라짐){Arg(arg)}", $"a foreign key column changes its type family (differs from the parent){Arg(arg)}"),
            "usedByVirtualColumn" => TableEditText.T($"이 열을 쓰는 가상 열이 있음{Arg(arg)}", $"a virtual column uses this column{Arg(arg)}"),
            "domainColumn" => TableEditText.T("도메인 열", "domain column"),
            "unsupportedIndex" => TableEditText.T($"다시 만들 수 없는 종류의 인덱스가 있음{Arg(arg)}", $"an index of a kind that cannot be recreated{Arg(arg)}"),
            "partitionedTable" => TableEditText.T("파티션 테이블", "partitioned table"),
            "partitionedMove" => TableEditText.T("파티션 테이블은 테이블째 MOVE 하지 않음", "a partitioned table is not moved as a whole"),
            "specialTable" => TableEditText.T("특수 테이블에서는 고칠 수 없음", "cannot be changed on a special table"),
            "temporaryForeignKey" => TableEditText.T("임시 테이블에는 외래 키를 둘 수 없음", "a temporary table cannot have foreign keys"),
            "iotWithoutPrimaryKey" => TableEditText.T("인덱스 구성 테이블에는 PK 가 필요함", "an index-organized table needs a primary key"),
            "iotBitmapIndex" => TableEditText.T("새 인덱스 구성 테이블에는 비트맵 인덱스를 만들 수 없음", "no bitmap index on a new index-organized table"),
            "iotCompression" => TableEditText.T("인덱스 구성 테이블은 이 압축을 쓰지 않음", "this compression is not used on an index-organized table"),
            "uniqueBitmap" => TableEditText.T("비트맵 인덱스는 유일로 만들 수 없음", "a bitmap index cannot be unique"),
            "identityFromPlain" => TableEditText.T("일반 열을 신원 열로 바꿀 수 없음", "a plain column cannot become an identity column"),
            "identityUnknown" => TableEditText.T("신원 속성을 읽지 못함(신원 없애기만 됨)", "identity attributes unknown (only dropping the identity works)"),
            "identityDefault" or "identityNullDefault" => TableEditText.T("신원 열에는 기본값을 둘 수 없음", "an identity column cannot have a default"),
            "identityColumn" => TableEditText.T("신원 열", "identity column"),
            "pctFreeRange" => TableEditText.T("PCTFREE 는 0~99", "PCTFREE must be 0–99"),
            "initTransRange" => TableEditText.T("INITRANS 는 1~255", "INITRANS must be 1–255"),
            "parallelRange" => TableEditText.T("병렬 정도가 범위를 벗어남", "the parallel degree is out of range"),
            "extentSize" => TableEditText.T("익스텐트 크기 글이 맞지 않음(예: 64K, 1M)", "the extent size is not valid (e.g. 64K, 1M)"),
            "compressionOther" => TableEditText.T("이 압축은 고를 수 없음", "this compression cannot be chosen"),
            "basicFileLobOption" => TableEditText.T("BASICFILE LOB 은 압축·중복 제거를 쓰지 않음", "a BASICFILE LOB has no compression or deduplication"),
            "lobOnNonLob" => TableEditText.T("LOB 이 아닌 열에 LOB 저장 절", "LOB storage on a non-LOB column"),
            "longColumnMove" => TableEditText.T("LONG 열이 있는 테이블은 MOVE 할 수 없음", "a table with a LONG column cannot be moved"),
            _ when detail.StartsWith("TableEdit.", StringComparison.Ordinal) || detail.StartsWith("tableEdit.", StringComparison.Ordinal)
                => Oranda.App.Features.Localization.UiText.Get(detail, detail),
            _ => detail,
        };
    }

    private static string Arg(string arg) => arg.Length == 0 ? string.Empty : ": " + arg;

    /// <summary>✔ 줄 문구(그 검사를 해서 문제가 없음).</summary>
    private static string PassedText(CheckCode code, string s, string? detail) => code switch
    {
        CheckCode.Passed => s + (detail is null ? string.Empty : ": " + detail),
        CheckCode.Overflow => TableEditText.T($"{s}: 새 길이에 모두 들어감", $"{s}: every value fits the new size"),
        CheckCode.IncompatibleTypeChange => TableEditText.T($"{s}: 형식을 바꿀 수 있음", $"{s}: the type can change"),
        CheckCode.NullRows => TableEditText.T($"{s}: NULL 행 없음", $"{s}: no NULL rows"),
        CheckCode.DuplicateKeys => TableEditText.T($"{s}: 겹치는 키 없음", $"{s}: no duplicate keys"),
        CheckCode.OrphanRows => TableEditText.T($"{s}: 짝 없는 행 없음", $"{s}: every row has a parent"),
        CheckCode.CheckViolations => TableEditText.T($"{s}: 조건을 어기는 행 없음", $"{s}: no rows violate the condition"),
        CheckCode.ExpressionFails => TableEditText.T($"{s}: 식 계산됨", $"{s}: the expression evaluates"),
        CheckCode.NameExists => TableEditText.T($"{s}: 이름 겹침 없음", $"{s}: the name is free"),
        CheckCode.ConversionFails => TableEditText.T($"{s}: 변환 안 되는 행 없음", $"{s}: every row converts"),
        _ => TableEditText.T($"{s}: 문제 없음", $"{s}: no problem"),
    };

    /// <summary>여러 줄(✖ → ⚠ → ✔ 순서는 부르는 쪽이 정한 그대로).</summary>
    public static string FormatAll(IEnumerable<CheckFinding> findings, bool purgesOnDrop = false) =>
        string.Join(Environment.NewLine, findings.Select(f => Format(f, purgesOnDrop)));

    private static string Dependents(CheckFinding finding) => finding.Dependents.Count == 0
        ? string.Empty
        : TableEditText.T($" · 이것을 쓰는 객체 {finding.Dependents.Count}개: ", $" · {finding.Dependents.Count} dependent object(s): ")
          + string.Join(", ", finding.Dependents.Take(5).Select(d => $"{d.Owner}.{d.Name}")) + (finding.Dependents.Count > 5 ? " …" : string.Empty);
}

/// <summary>검사 한 줄의 화면 꼴(문구 + 「그 행 보기」 SELECT).</summary>
public sealed record FindingRow(string Text, string? ViewRowsSql)
{
    public bool HasViewRows => !string.IsNullOrWhiteSpace(ViewRowsSql);

    /// <summary>「변환해서 바꾸기…」 대상 열 줄 키(데이터가 있어 형식을 못 바꾸는 ✖, 설계 18.9). 없으면 null.</summary>
    public string? ConversionRowKey { get; init; }

    public bool HasConversion => ConversionRowKey is not null;
}
