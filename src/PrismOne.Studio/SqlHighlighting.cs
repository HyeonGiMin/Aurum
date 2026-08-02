using System;
using System.IO;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace PrismOne.Studio;

/// <summary>
/// Golden 에디터 배색: 키워드는 적갈색(maroon) 굵게, 문자열은 파랑, 주석은 초록.
/// </summary>
public static class SqlHighlighting
{
    private const string Xshd = """
        <SyntaxDefinition name="GoldenSQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Keyword" foreground="#8B1A1A" fontWeight="bold"/>
          <Color name="String" foreground="#1414B8"/>
          <Color name="Comment" foreground="#207020"/>
          <Color name="Number" foreground="#1A1A1A"/>
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="--" end="\n"/>
            <Span color="Comment" multiline="true" begin="/\*" end="\*/"/>
            <Span color="String" multiline="true" begin="'" end="'"/>
            <Keywords color="Keyword">
              <Word>select</Word><Word>from</Word><Word>where</Word><Word>insert</Word>
              <Word>into</Word><Word>values</Word><Word>update</Word><Word>set</Word>
              <Word>delete</Word><Word>create</Word><Word>alter</Word><Word>drop</Word>
              <Word>table</Word><Word>view</Word><Word>index</Word><Word>sequence</Word>
              <Word>function</Word><Word>procedure</Word><Word>trigger</Word><Word>schema</Word>
              <Word>database</Word><Word>tablespace</Word><Word>and</Word><Word>or</Word>
              <Word>not</Word><Word>null</Word><Word>is</Word><Word>in</Word><Word>as</Word>
              <Word>on</Word><Word>by</Word><Word>order</Word><Word>group</Word><Word>having</Word>
              <Word>join</Word><Word>left</Word><Word>right</Word><Word>inner</Word><Word>outer</Word>
              <Word>full</Word><Word>cross</Word><Word>union</Word><Word>all</Word><Word>distinct</Word>
              <Word>limit</Word><Word>offset</Word><Word>between</Word><Word>like</Word><Word>ilike</Word>
              <Word>exists</Word><Word>case</Word><Word>when</Word><Word>then</Word><Word>else</Word>
              <Word>end</Word><Word>begin</Word><Word>commit</Word><Word>rollback</Word>
              <Word>grant</Word><Word>revoke</Word><Word>primary</Word><Word>foreign</Word>
              <Word>key</Word><Word>references</Word><Word>constraint</Word><Word>default</Word>
              <Word>unique</Word><Word>check</Word><Word>cascade</Word><Word>restrict</Word>
              <Word>asc</Word><Word>desc</Word><Word>explain</Word><Word>analyze</Word>
              <Word>vacuum</Word><Word>with</Word><Word>recursive</Word><Word>returning</Word>
              <Word>using</Word><Word>partition</Word><Word>over</Word><Word>window</Word>
              <Word>varchar</Word><Word>numeric</Word><Word>integer</Word><Word>bigint</Word>
              <Word>smallint</Word><Word>boolean</Word><Word>timestamp</Word><Word>date</Word>
              <Word>time</Word><Word>interval</Word><Word>text</Word><Word>bytea</Word>
              <Word>jsonb</Word><Word>json</Word><Word>uuid</Word><Word>serial</Word>
              <Word>true</Word><Word>false</Word><Word>if</Word><Word>coalesce</Word>
              <Word>cast</Word><Word>count</Word><Word>sum</Word><Word>avg</Word>
              <Word>min</Word><Word>max</Word>
            </Keywords>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private static readonly Lazy<IHighlightingDefinition> Cached = new(() =>
    {
        using var reader = XmlReader.Create(new StringReader(Xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    });

    public static IHighlightingDefinition Definition => Cached.Value;
}
