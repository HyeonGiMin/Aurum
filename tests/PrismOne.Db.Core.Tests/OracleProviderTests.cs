using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// Oracle provider. **실접속 검증은 없다** — 인스턴스가 없어서, 서버 없이 확인할 수 있는
/// 것(레지스트리·기능 플래그·인용 규칙·접속 문자열 조립·순수 함수)만 본다.
/// 카탈로그 쿼리(all_constraints 등)는 서버가 확보되면 SqliteProviderTests 처럼
/// 실제 스키마로 검증해야 한다 (MULTI_DB_PLAN.md 2단계).
/// </summary>
public class OracleProviderTests
{
    /// <summary>합성 값 — 실제 접속 정보가 아니다.</summary>
    private static readonly ConnectionProfile Profile =
        new("ora-host", 1521, "ORCLPDB", "prismone", "pw", Kind: DbKind.Oracle);

    private static IDbProvider Provider => DbProviders.For(DbKind.Oracle);

    [Fact]
    public void IsRegistered()
    {
        Assert.True(DbProviders.IsSupported(DbKind.Oracle));
        Assert.Equal("Oracle", Provider.DisplayName);
    }

    [Fact]
    public void UsesRowIdForGridEditing()
    {
        Assert.Equal("ROWID", Provider.RowIdColumn);
        Assert.True(Provider.Capabilities.GridEditing);
    }

    [Fact]
    public void HasNoServerSideBulkExport()
        => Assert.False(Provider.Capabilities.BulkExport);

    [Fact]
    public void OffersOnlyTheIsolationLevelsOracleActuallySupports()
    {
        Assert.Equal(
            [TransactionIsolation.DatabaseDefault,
             TransactionIsolation.ReadCommitted,
             TransactionIsolation.Serializable],
            Provider.SupportedIsolations);
        Assert.DoesNotContain(TransactionIsolation.RepeatableRead, Provider.SupportedIsolations);
    }

    [Fact]
    public void UppercaseIdentifiersNeedNoQuoting()
    {
        Assert.Equal("STUDY_KEY", Provider.QuoteIdentifier("STUDY_KEY"));
        // 소문자는 인용하지 않으면 Oracle 이 대문자로 접어버린다
        Assert.Equal("\"study_key\"", Provider.QuoteIdentifier("study_key"));
        Assert.Equal("\"1st\"", Provider.QuoteIdentifier("1st"));
        Assert.Equal("\"a\"\"b\"", Provider.QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void ConnectionStringCarriesServiceNameAndUser()
    {
        var text = Provider.BuildConnectionString(Profile);

        Assert.Contains("ora-host:1521/ORCLPDB", text);
        Assert.Contains("prismone", text);
    }

    [Fact]
    public void DescribeLeavesThePasswordOut()
    {
        var text = Provider.Describe(Profile);

        Assert.Equal("prismone@ora-host:1521/ORCLPDB", text);
        Assert.DoesNotContain("pw", text);
    }

    [Fact]
    public async Task PostgresOnlyPathRejectsOracle()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Profile.OpenAsync());

    // ---------- 순수 함수 ----------

    [Theory]
    [InlineData("VARCHAR2", 64, null, null, "VARCHAR2(64)")]
    [InlineData("CHAR", 2, null, null, "CHAR(2)")]
    [InlineData("NUMBER", null, 19, 0, "NUMBER(19)")]
    [InlineData("NUMBER", null, 10, 2, "NUMBER(10,2)")]
    [InlineData("NUMBER", null, null, null, "NUMBER")]
    [InlineData("DATE", null, null, null, "DATE")]
    [InlineData("CLOB", null, null, null, "CLOB")]
    public void FormatsTypesLikeTheDataDictionaryWouldRead(
        string type, int? charLength, int? precision, int? scale, string expected)
        => Assert.Equal(expected, OracleErdCatalog.FormatType(type, charLength, precision, scale));

    [Fact]
    public void BindPlaceholdersAreNumberedNotConcatenatedValues()
    {
        Assert.Equal(":o0, :o1, :o2", OracleErdCatalog.Placeholders(3, "o"));
        Assert.Equal(":x0", OracleErdCatalog.Placeholders(1, "x"));
        Assert.Equal("", OracleErdCatalog.Placeholders(0, "o"));
    }
}
