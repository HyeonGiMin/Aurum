using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class HangulQwertyTests
{
    [Theory]
    [InlineData("암호", "dkagh")]
    [InlineData("값", "rkqt")]
    [InlineData("의", "dml")]
    [InlineData("쏘", "Th")]
    [InlineData("뷁", "qnpfr")]
    [InlineData("ㅁㄴㅇ", "asd")]
    [InlineData("abc한1!", "abcgks1!")]
    [InlineData("***REMOVED***", "***REMOVED***")]
    public void Convert_MapsDubeolsikToQwerty(string input, string expected)
        => Assert.Equal(expected, HangulQwerty.Convert(input));
}
