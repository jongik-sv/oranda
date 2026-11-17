using System.Text.Json;
using System.Text.Json.Serialization;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 새 테이블 작업 저장(설계 18.7, TE-43)의 편집 모델 JSON. <c>oranda.db</c> 표 <c>table_draft.model_json</c> 에 들어간다.
/// 계산 속성(<see cref="ColumnDraft.IsVirtual"/>·<see cref="ColumnDraft.IsIdentityColumn"/>)은 쓰지 않는다(읽기 전용 속성 무시).
/// 읽을 때는 엄격하다: null 이면 안 되는 칸의 null·빠진 이름·줄 키, 숫자로 쓴 열거형, 모르는 열거형 이름이면 그 저장분을 버린다(null).
/// </summary>
public static class TableDraftJson
{
    /// <summary>편집 모델을 JSON 한 줄로 쓴다.</summary>
    public static string Serialize(TableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.Serialize(draft, TableDraftJsonContext.Default.TableDraft);
    }

    /// <summary>JSON 을 편집 모델로 읽는다. 깨진 글·모르는 열거형 값·빠진 필수 칸이면 null(그 저장분은 되살리지 않는다).</summary>
    public static TableDraft? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var draft = JsonSerializer.Deserialize(json, TableDraftJsonContext.Default.TableDraft);
            return draft is null ? null : Normalize(draft) is { } normalized && IsComplete(normalized) ? normalized : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 빠진 목록 칸을 빈 목록으로. 소스 생성 직렬화기는 생성자가 있는 형식의 init 속성을 생성자 인자처럼 다뤄, JSON 에 없으면 초기값(<c>= []</c>) 대신
    /// default(null)를 넣는다(명시한 <c>null</c> 은 <c>RespectNullableAnnotations</c> 가 이미 거부). 쓸 때는 목록·bool 을 늘 쓰므로 손으로 고친 글·버전 차이에서만 생긴다.
    /// </summary>
    private static TableDraft Normalize(TableDraft d) => d with
    {
        Columns = d.Columns ?? [],
        Constraints = (d.Constraints ?? []).Select(c => c is null ? c! : c with
        {
            ColumnKeys = c.ColumnKeys ?? [],
            ReferencedColumns = c.ReferencedColumns ?? [],
        }).ToList(),
        Indexes = (d.Indexes ?? []).Select(i => i is null ? i! : i with { Columns = i.Columns ?? [] }).ToList(),
    };

    /// <summary>생성자 인자로 받는 필수 문자열(빠지면 직렬화기가 null 을 넣는다)이 다 있나.</summary>
    private static bool IsComplete(TableDraft d)
        => d.Owner is not null && d.Name is not null
            && d.Columns.All(c => c is not null && c.RowKey is not null && c.Name is not null)
            && d.Constraints.All(c => c is not null && c.RowKey is not null && c.Name is not null
                && c.ColumnKeys.All(k => k is not null) && c.ReferencedColumns.All(k => k is not null)
                && (c.ReferencedTable is null || (c.ReferencedTable.Owner is not null && c.ReferencedTable.Name is not null && c.ReferencedTable.ObjectType is not null)))
            && d.Indexes.All(i => i is not null && i.RowKey is not null && i.Name is not null && i.Columns.All(c => c is not null));
}

/// <summary>
/// <see cref="TableDraft"/> 의 소스 생성 직렬화 문맥. camelCase, 열거형 이름 문자열(숫자 거부), null 생략(<see cref="Oranda.Core.Serialization.OrandaJsonContext"/> 와 같은 규칙),
/// null 주석 지킴, DB 칸에 들어가므로 들여쓰지 않는다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IgnoreReadOnlyProperties = true,
    RespectNullableAnnotations = true,
    UseStringEnumConverter = true, // 아래 목록에 없는 열거형(나중에 모델에 더한 것)도 이름으로 쓴다 — 숫자 거부는 목록에 더해야 한다
    Converters =
    [
        typeof(TableDraftEnumConverter<TableKind>),
        typeof(TableDraftEnumConverter<CharSemantics>),
        typeof(TableDraftEnumConverter<IdentityGeneration>),
        typeof(TableDraftEnumConverter<ConstraintKind>),
        typeof(TableDraftEnumConverter<DeleteRule>),
        typeof(TableDraftEnumConverter<IndexKind>),
    ])]
[JsonSerializable(typeof(TableDraft))]
public sealed partial class TableDraftJsonContext : JsonSerializerContext
{
}

/// <summary>열거형을 이름으로만 읽고 쓴다(숫자 값은 읽지 않는다 — 정의 안 된 값이 모델에 들어오지 않게).</summary>
public sealed class TableDraftEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
