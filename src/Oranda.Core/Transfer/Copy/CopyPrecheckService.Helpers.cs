using System.Globalization;
using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 도우미 ----

        private const string CountPrefix = "SELECT COUNT(*)";

        /// <summary>대상 세션 DBMS 의 카탈로그 SELECT 글(<c>copy.*</c>).</summary>
        private string TargetSql(string id) => CopyCatalogQueries.Sql(catalog, Target.Server, id);

        /// <summary>원본 세션 DBMS 의 카탈로그 SELECT 글(<c>copy.*</c>).</summary>
        private string SourceSql(string id) => CopyCatalogQueries.Sql(sourceParts.Catalog, Source.Server, id);

        /// <summary>대상 초기화 파라미터 값(<c>copy.parameter_value</c>)을 글자로. 행이 없거나 서버 오류면 null.</summary>
        private async Task<string?> TargetParameterAsync(string name, CancellationToken ct)
        {
            try
            {
                return codec.ToText((await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.parameter_value"), ct,
                    ("PNAME", name)).ConfigureAwait(false)).FirstOrDefault()?[0]);
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null; // V$PARAMETER 를 읽지 못하면 알 수 없음.
            }
        }

        private string Key(TableRule rule) => CopyOwners.Key(job, rule);

        private string SourceOwner(TableRule rule) => CopyOwners.Source(job, rule);

        private string TargetOwner(TableRule rule) => CopyOwners.Target(job, rule);

        // 규칙이 어긋나 문장을 못 만들면 그 표만 오류로 알리고 점검은 이어 간다.
        private T? Build<T>(string table, Func<T?> make)
            where T : class
        {
            try
            {
                return make();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                AddInvalid(table, ex.Message);
                return null;
            }
        }

        private void AddInvalid(string table, string message)
        {
            var text = $"{table}: {message}";
            if (!_items.Any(i => i.Code == "RuleInvalid" && i.Message == text))
            {
                _items.Add(new CheckItem(table, "RuleInvalid", CheckLevel.Error, text));
            }
        }

        private static bool IsNumber(TableInfo info, string column)
            => info.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal))?.DataType
                is { } t && (t.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase) || t.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase));

        private static string Normalize(string condition) => Whitespace().Replace(condition, "").ToUpperInvariant();

        private static string ForeignKeySignature(ConstraintInfo c)
            => string.Join(',', c.Columns) + ">" + c.ReferencedTable?.Name;

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
