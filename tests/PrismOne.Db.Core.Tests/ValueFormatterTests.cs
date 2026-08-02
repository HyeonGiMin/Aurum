using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class ValueFormatterTests
{
    [Fact]
    public void Format_ShortStringUnchanged()
        => Assert.Equal("hello", ValueFormatter.Format("hello"));

    [Fact]
    public void Format_HugeStringTruncatedForDisplay()
    {
        var huge = new string('x', 200_000);   // JSONB DICOM Data Set 같은 거대 값
        var formatted = ValueFormatter.Format(huge)!;
        Assert.True(formatted.Length < 600);
        Assert.StartsWith(new string('x', ValueFormatter.MaxDisplayChars), formatted);
        Assert.Contains("chars)", formatted);
    }

    [Fact]
    public void Format_NullIsNull()
        => Assert.Null(ValueFormatter.Format(System.DBNull.Value));
}
