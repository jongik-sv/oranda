using Oranda.Core.Journal;
using Oranda.Core.Rollback.Backup;

namespace Oranda.Core.Tests.Rollback.Backup;

// 소유: rb-m2-oracle RB-44. 데이터가 사라지는 DDL 앞 백업 테이블(CTAS) 제안(되돌리기 지점 설계 9장, RD-17).
public class BackupPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 14, 5, 10);

    private static BackupContext Context(int maxBytes = 128, params string[] keys) =>
        new("APP", "SCOTT", maxBytes, Now) { KeyColumns = keys };

    [Fact]
    public void Truncate_copies_the_whole_table_into_the_session_user_schema()
    {
        var target = new ChangeTarget("SAMPLE", "PRODUCTS", "TABLE", ChangeOperation.Truncate) { DataLoss = DataLossKind.Truncate };

        var offer = BackupPlanner.Offer(target, Context())!;

        Assert.Equal("ORB_PRODUCTS_11131405", offer.BackupName);
        Assert.Equal("CREATE TABLE \"SCOTT\".\"ORB_PRODUCTS_11131405\" AS SELECT * FROM \"SAMPLE\".\"PRODUCTS\"", offer.CreateSql);
        Assert.Equal(("SAMPLE", "PRODUCTS", "TABLE"), (offer.Table.Owner, offer.Table.Name, offer.Table.ObjectType));
        Assert.Contains("TRUNCATE", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_columns_keeps_key_and_dropped_columns_and_falls_back_to_rowid()
    {
        var target = new ChangeTarget(null, "EMP", "TABLE", ChangeOperation.Alter)
        {
            DataLoss = DataLossKind.DropColumns,
            DroppedColumns = ["MEMO", "ID"],
        };

        Assert.True(BackupPlanner.NeedsKeyColumns(target));
        Assert.Equal("CREATE TABLE \"SCOTT\".\"ORB_EMP_11131405\" AS SELECT \"ID\", \"MEMO\" FROM \"APP\".\"EMP\"",
            BackupPlanner.Offer(target, Context(128, "ID"))!.CreateSql); // PK 열을 지워도 두 번 넣지 않는다
        Assert.Equal("CREATE TABLE \"SCOTT\".\"ORB_EMP_11131405\" AS SELECT ROWIDTOCHAR(ROWID) AS ORB_ROWID, \"MEMO\", \"ID\" FROM \"APP\".\"EMP\"",
            BackupPlanner.Offer(target, Context())!.CreateSql);
    }

    [Fact]
    public void Name_is_cut_to_the_server_identifier_limit()
    {
        Assert.Equal("ORB_CUSTOMER_ORDER_HI_11131405", BackupPlanner.DefaultName("CUSTOMER_ORDER_HISTORY_ARCHIVE", 30, Now));
        Assert.Equal("ORB_상품목록_11131405", BackupPlanner.DefaultName("상품목록표", 27, Now)); // 한글 3바이트 글자 경계에서 자른다
    }

    [Fact]
    public void No_offer_for_ddl_that_keeps_data()
    {
        Assert.Null(BackupPlanner.Offer(new ChangeTarget(null, "EMP", "TABLE", ChangeOperation.Drop), Context()));
        Assert.Null(BackupPlanner.Offer(null, Context()));
    }

    [Fact]
    public void Rename_rebuilds_the_create_statement()
    {
        var target = new ChangeTarget("SAMPLE", "PRODUCTS", "TABLE", ChangeOperation.Drop) { DataLoss = DataLossKind.DropPurge };
        var offer = BackupPlanner.Offer(target, Context())!;

        var renamed = BackupPlanner.Rename(offer, " orb_keep ")!;
        Assert.Equal("ORB_KEEP", renamed.BackupName);
        Assert.Equal("CREATE TABLE \"SCOTT\".\"ORB_KEEP\" AS SELECT * FROM \"SAMPLE\".\"PRODUCTS\"", renamed.CreateSql);
        Assert.Equal("Keep \"1\"", BackupPlanner.Rename(offer, "\"Keep \"\"1\"\"\"")!.BackupName);
        Assert.Null(BackupPlanner.Rename(offer, "  "));
        Assert.Equal("SCOTT.ORB_KEEP", BackupPlanner.BackupNameOf(renamed.CreateSql));
        Assert.Null(BackupPlanner.BackupNameOf("CREATE TABLE x AS SELECT 1 FROM DUAL"));
        Assert.Null(BackupPlanner.Rename(offer, "other.orb_keep")); // 스키마는 바꿀 수 없다
        Assert.True(BackupPlanner.IsSameBackup(offer, renamed.CreateSql));
        Assert.False(BackupPlanner.IsSameBackup(offer, "CREATE TABLE \"SCOTT\".\"ORB_KEEP\" AS SELECT * FROM \"HR\".\"SECRETS\""));
        Assert.False(BackupPlanner.IsSameBackup(offer, "DROP USER SCOTT CASCADE"));
    }
}
