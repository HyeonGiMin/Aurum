using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class CopyExporterTests
{
    [Theory]
    [InlineData("select * from study", "COPY (select * from study) TO STDOUT WITH (FORMAT CSV, HEADER)")]
    [InlineData("select 1;", "COPY (select 1) TO STDOUT WITH (FORMAT CSV, HEADER)")]
    [InlineData("select 1 ;  \n", "COPY (select 1) TO STDOUT WITH (FORMAT CSV, HEADER)")]
    public void BuildCopySql_StripsTrailingSemicolon(string query, string expected)
        => Assert.Equal(expected, CopyExporter.BuildCopySql(query));
}
