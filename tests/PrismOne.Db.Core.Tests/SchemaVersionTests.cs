using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class SchemaVersionTests
{
    [Theory]
    [InlineData("20260718_01_seed_encapsulated_sopclass.sql", "20260718_01")]
    [InlineData("20260715_02_keys_numeric_to_bigint.sql", "20260715_02")]
    [InlineData("20260715_02", "20260715_02")]
    [InlineData("free-form-name.sql", "free-form-name")]   // 패턴 밖이면 .sql 만 제거
    [InlineData("v2_hotfix.sql", "v2_hotfix")]
    public void ShortLabel_TrimsToDatePlusSequence(string versionId, string expected)
        => Assert.Equal(expected, SchemaVersionInfo.ShortLabel(versionId));

    [Fact]
    public void Label_BaselineWhenNoPatchesRecorded()
    {
        Assert.Equal("baseline", new SchemaVersionInfo(null, null, 0).Label);
        Assert.Equal("20260718_01",
            new SchemaVersionInfo("20260718_01_seed_encapsulated_sopclass.sql", DateTime.UnixEpoch, 4).Label);
    }
}
