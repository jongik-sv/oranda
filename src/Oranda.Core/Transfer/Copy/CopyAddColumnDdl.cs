using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

/// <summary>대상에 더할 열 하나(PC1, <see cref="TableRule.AddColumns"/>): 대상 구조에 넣을 열(<paramref name="Column"/>, NULL 허용)과 보낼 문장(<paramref name="Ddl"/>).</summary>
public sealed record CopyAddColumn(ColumnInfo Column, string Ddl);

/// <summary>
/// 「대상에 열 추가」의 문장(PC1, 결정 D-066). 순수 함수 — 사전 점검(계획·미리 보기)과 처리 설명(<see cref="CopyPrecheckFixes"/>)이 같은 글을 쓰고, 실행기는 점검 계획의 글을 그대로 보낸다.
/// 형식은 원본 열을 대상 DBMS 로 옮긴 형식(<see cref="CrossTypeMap"/>, 같은 DBMS 면 원본 형식 그대로)이고 <see cref="CreateTableDdl"/> 과 같은 형식 글을 쓴다.
/// NOT NULL·기본값·IDENTITY·INVISIBLE·주석은 붙이지 않는다: 행이 있는 표에 NOT NULL 열을 더하면 실패하고(ORA-01758), 원본 기본값은 원본 객체를 가리킬 수 있다.
/// Oracle·Tibero 모두 <c>ALTER TABLE "OWNER"."TABLE" ADD ("COL" 형식)</c> 꼴이다.
/// </summary>
public static class CopyAddColumnDdl
{
    /// <summary>
    /// 더할 열과 문장. 더할 수 없으면 null 과 그 이유(<paramref name="refused"/>): 가상 열, 대상 DBMS 로 옮길 형식이 없는 열(교차), 복사가 읽을 수 없는 형식.
    /// </summary>
    /// <param name="sourceCodec">원본 코덱. 주면 읽지 못하는 형식도 막는다(교차 복사의 <see cref="CrossTableMap.IsDropped"/> 와 같은 판정).</param>
    public static CopyAddColumn? Plan(Dbms source, Dbms target, ColumnInfo column, string owner, string table, IBulkValueCodec? sourceCodec, out string? refused)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        refused = null;
        if (column.IsVirtual)
        {
            refused = "가상 열은 값을 넣지 않아 더하지 않습니다";
            return null;
        }

        ColumnInfo mapped = column;
        if (source != target)
        {
            var map = CrossTypeMap.Map(source, target, column);
            if (CrossTableMap.IsDropped(map, column, sourceCodec))
            {
                refused = $"{column.DataType} 열은 {target} 로 옮길 형식이 없습니다({PrecheckRules.DropReasonText(map.DropReason)})";
                return null;
            }

            mapped = map.Target ?? column;
        }
        else if (sourceCodec is not null && !sourceCodec.IsSupported(column.DataType))
        {
            refused = $"복사가 지원하지 않는 형식({column.DataType})입니다";
            return null;
        }

        var added = mapped with
        {
            IsNullable = true,
            DefaultValue = null,
            IsIdentity = false,
            IsVirtual = false,
            IsInvisible = false,
            Comment = null,
            Domain = null,
        };
        var ddl = $"ALTER TABLE {CopyCommonStatements.Qualified(owner, table)} ADD ({SqlIdentifier.QuoteAlways(added.Name)} {CreateTableDdl.TypeText(added)})";
        return new CopyAddColumn(added, ddl);
    }
}
