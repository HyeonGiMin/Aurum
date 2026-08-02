// SqlCompletion 은 Studio 프로젝트에 있어 여기서 직접 테스트하지 못하지만,
// 별칭 해석의 핵심인 정규식 동작을 동일 패턴으로 검증해 회귀를 막는다.
using System.Text.RegularExpressions;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class SqlAliasTests
{
    private static readonly Regex Pattern = new(
        @"\b(?:from|join)\s+(?:([A-Za-z_]\w*)\s*\.\s*)?([A-Za-z_]\w*)(?:\s+(?:as\s+)?([A-Za-z_]\w*))?",
        RegexOptions.IgnoreCase);

    [Fact]
    public void FromWithSchemaAndAlias()
    {
        var m = Pattern.Match("select * from prismone.study s where s.study_key = 1");
        Assert.Equal("prismone", m.Groups[1].Value);
        Assert.Equal("study", m.Groups[2].Value);
        Assert.Equal("s", m.Groups[3].Value);
    }

    [Fact]
    public void JoinWithAsAlias()
    {
        var m = Pattern.Match("from study st join prismone.series as se on se.study_key = st.study_key");
        Assert.Equal(2, Pattern.Matches("from study st join prismone.series as se on 1=1").Count);
        Assert.True(m.Success);
    }
}
