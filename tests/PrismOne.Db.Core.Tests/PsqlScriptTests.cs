using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class PsqlScriptTests
{
    private static Dictionary<string, string> Vars(params (string K, string V)[] pairs)
        => pairs.ToDictionary(p => p.K, p => p.V);

    // ---------- 치환 ----------

    [Fact]
    public void Substitute_LiteralAndIdentifierAndRaw()
    {
        var vars = Vars(("name", "pris'mone"), ("path", "/data/x"), ("n", "42"));
        Assert.Equal("select 'pris''mone'", PsqlScript.Substitute("select :'name'", vars));
        Assert.Equal("grant usage to \"pris'mone\"", PsqlScript.Substitute("grant usage to :\"name\"", vars));
        Assert.Equal("limit 42", PsqlScript.Substitute("limit :n", vars));
    }

    [Fact]
    public void Substitute_SkipsCastsQuotesDollarAndComments()
    {
        var vars = Vars(("v", "X"));
        Assert.Equal("select 1::int", PsqlScript.Substitute("select 1::int", vars));
        Assert.Equal("select ':v'", PsqlScript.Substitute("select ':v'", vars));
        Assert.Equal("select \":v\"", PsqlScript.Substitute("select \":v\"", vars));
        Assert.Equal("select $tag$ :v $tag$", PsqlScript.Substitute("select $tag$ :v $tag$", vars));
        Assert.Equal("-- :v\nselect X", PsqlScript.Substitute("-- :v\nselect :v", vars));
        Assert.Equal("/* :v */ X", PsqlScript.Substitute("/* :v */ :v", vars));
    }

    [Fact]
    public void Substitute_UndefinedPlainVarLeftAsIs_ButQuotedFormThrows()
    {
        var vars = Vars();
        Assert.Equal("select '12:30'::time", PsqlScript.Substitute("select '12:30'::time", vars));
        Assert.Equal("where t > :nope", PsqlScript.Substitute("where t > :nope", vars));
        Assert.Throws<KeyNotFoundException>(() => PsqlScript.Substitute("select :'nope'", vars));
    }

    // ---------- 파싱 ----------

    [Fact]
    public void Parse_SetAndConditionals_MirrorGrantsFile()
    {
        const string script = """
            \set ON_ERROR_STOP on
            \if :{?db_owner}
            \else
              \set db_owner prismone
            \endif
            GRANT USAGE ON SCHEMA prismone TO :"db_owner";
            """;

        // db_owner 미정의 → \else 의 \set 이 기본값을 채운다 (Text 는 세미콜론 제외)
        var vars = Vars();
        var units = PsqlScript.Parse(script, vars);
        var unit = Assert.Single(units);
        Assert.Equal("GRANT USAGE ON SCHEMA prismone TO \"prismone\"", unit.Sql);

        // db_owner 정의 → \if 참, \set 은 건너뛴다
        var units2 = PsqlScript.Parse(script, Vars(("db_owner", "app")));
        Assert.Equal("GRANT USAGE ON SCHEMA prismone TO \"app\"", Assert.Single(units2).Sql);
    }

    [Fact]
    public void Parse_GexecAtLineEnd_SplitsPrecedingStatements()
    {
        const string script = """
            create table t(a int);
            SELECT format('CREATE ROLE %I', :'db_owner')
             WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'db_owner')\gexec
            """;
        var units = PsqlScript.Parse(script, Vars(("db_owner", "prismone")));
        Assert.Equal(2, units.Count);
        Assert.IsType<PsqlUnit.Statement>(units[0]);
        var gexec = Assert.IsType<PsqlUnit.Gexec>(units[1]);
        Assert.Contains("rolname = 'prismone'", gexec.Sql);
        Assert.DoesNotContain("\\gexec", gexec.Sql);
    }

    [Fact]
    public void Parse_DollarQuotedFunctionBody_StaysOneStatement()
    {
        const string script = """
            create function f() returns int language plpgsql as $$
            begin
              return 1; -- 세미콜론이 있어도 한 문장
            end $$;
            select 2;
            """;
        var units = PsqlScript.Parse(script, Vars());
        Assert.Equal(2, units.Count);
        Assert.Contains("return 1;", units[0].Sql);
    }

    [Fact]
    public void Parse_UnsupportedMetaCommand_Throws()
    {
        Assert.Throws<NotSupportedException>(() => PsqlScript.Parse("\\copy t from stdin", Vars()));
        Assert.Throws<FormatException>(() => PsqlScript.Parse("\\if true\nselect 1;", Vars()));
    }

    // ---------- 실제 repo SQL 파일 전체 ----------

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "manifest.txt")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    public static TheoryData<string> ManifestFiles()
    {
        var data = new TheoryData<string>();
        var root = RepoRoot();
        foreach (var line in File.ReadAllLines(Path.Combine(root, "manifest.txt")))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            data.Add(t[(t.IndexOf('|') + 1)..]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Parse_EveryManifestFile_YieldsUnitsAndFullySubstitutes(string relPath)
    {
        var vars = new Dictionary<string, string>
        {
            ["db_name"] = "prismone",
            ["db_owner"] = "prismone",
            ["db_pass"] = "***REMOVED***",
            ["ts_data"] = "/data/pg_ts/prismone",
            ["ts_idx"] = "/data/pg_ts/prismone_idx",
        };
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relPath));
        var units = PsqlScript.Parse(text, vars);

        Assert.NotEmpty(units);
        foreach (var unit in units)
        {
            Assert.DoesNotContain("\\gexec", unit.Sql);
            // :'var' / :"var" 가 남아 있으면 치환 누락
            Assert.DoesNotMatch(@":['""][A-Za-z_]\w*['""]", unit.Sql);
        }
    }

    [Fact]
    public void Parse_CreatePrismoneFile_ProducesFourGexecGuards()
    {
        var vars = Vars(("db_name", "prismone"), ("db_owner", "prismone"),
            ("db_pass", "pw"), ("ts_data", "/d"), ("ts_idx", "/i"));
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "sql/10_create_prismone.sql"));
        var units = PsqlScript.Parse(text, vars);
        Assert.Equal(4, units.Count);
        Assert.All(units, u => Assert.IsType<PsqlUnit.Gexec>(u));
    }
}
