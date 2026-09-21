using Muthur.XmlDocCheck;

namespace Muthur.XmlDocCheck.Tests;

public sealed class SourceCheckerTests
{
    private const string Duplicate = "/// <summary/>\n/// <summary/>\n";

    [Theory]
    [InlineData("public class C { public void M() {} }")]
    [InlineData("/// <summary>One</summary>\nclass C {}")]
    [InlineData("/// <remarks><summary/><summary/></remarks>\nclass C {}")]
    [InlineData("/// <summary/><remarks><summary/></remarks>\nclass C {}")]
    [InlineData("/// <summary/><example><![CDATA[<summary/><summary/>]]>&lt;summary/&gt;</example>\nclass C {}")]
    [InlineData("/// <summary/><x:summary/><Summary/>\nclass C {}")]
    [InlineData("/// <summary/>\npartial class C {}\n/// <summary/>\npartial class C {}")]
    [InlineData("/// <summary/>\nclass C {}\n/// <summary/>\nclass D {}")]
    [InlineData("/// <summary/><summary/><broken>\nclass C {}")]
    [InlineData("/// <summary/><summary/>\n// separator\n/// <broken>\nclass C {}")]
    [InlineData("class C {\n/// <summary/><summary/>\n}\n/// <summary/><summary/>")]
    [InlineData("/// <summary>Deletes all databases.</summary>\nclass Innocent {}")]
    [InlineData("/// <summary/><summary/>\nnamespace N { class C {} }")]
    [InlineData("/// <summary/><summary/>\nnamespace N;\nclass C {}")]
    [InlineData("class C { void M(\n/// <summary/><summary/>\nint x) {} }")]
    public void Accepted_source_has_no_diagnostics(string source) => Assert.Empty(SourceChecker.Analyze("src/C.cs", source));

    [Theory]
    [InlineData("/// <summary/>\n/// <summary/>\nclass C {}", 2, 5)]
    [InlineData("/// <summary/>\n/// <summary/>\n/// <summary/>\nclass C {}", 2, 5)]
    [InlineData("/// <summary>\n/// First\n/// </summary>\n/// <summary>Second</summary>\nclass C {}", 4, 5)]
    [InlineData("/** <summary/>\n * <summary/> */\nclass C {}", 2, 4)]
    [InlineData("/// <summary/>\n\n// separator\n/** <summary/> */\nclass C {}", 4, 5)]
    [InlineData("/// <summary/>\n// separator\n/// <summary/>\n[Obsolete]\nclass C {}", 3, 5)]
    public void Duplicate_reports_second_opening_once(string source, int line, int column)
    {
        var diagnostic = Assert.Single(SourceChecker.Analyze("src\\C.cs", source));
        Assert.Equal(new SummaryDiagnostic("src/C.cs", line, column), diagnostic);
        Assert.Equal("MUTHURXML001", diagnostic.Code);
    }

    [Fact]
    public void Positive_fixture_assertion_fails_if_analysis_results_are_omitted()
    {
        var actual = SourceChecker.Analyze("src/C.cs", Duplicate + "class C {}");
        RequireDuplicate(actual);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RequireDuplicate([]));
    }

    private static void RequireDuplicate(IReadOnlyList<SummaryDiagnostic> diagnostics) =>
        Assert.Equal(new SummaryDiagnostic("src/C.cs", 2, 5), Assert.Single(diagnostics));

    [Theory]
    [InlineData("class C {}")]
    [InlineData("record C;")]
    [InlineData("delegate void D();")]
    [InlineData("class C {\n", "void M() {}\n}")]
    [InlineData("class C {\n", "int a, b;\n}")]
    [InlineData("class C {\n", "event Action A, B;\n}")]
    [InlineData("class C {\n", "int P { get; set; }\n}")]
    [InlineData("enum E {\n", "A, B\n}")]
    [InlineData("class C { void M() {\n", "void Local() {}\n} }")]
    public void Each_documentable_owner_is_checked(string prefixOrDeclaration, string? suffix = null)
    {
        var source = suffix is null ? Duplicate + prefixOrDeclaration : prefixOrDeclaration + Duplicate + suffix;
        Assert.Single(SourceChecker.Analyze("tests/C.cs", source));
    }

    [Fact]
    public void Partial_declarations_are_independent() => Assert.Equal(2,
        SourceChecker.Analyze("src/C.cs", Duplicate + "partial class C {}\n" + Duplicate + "partial class C {}").Count);

    [Fact]
    public void All_conditional_branches_are_checked_and_shared_diagnostics_deduplicated()
    {
        var source = "#if A\n" + Duplicate + "class A {}\n#elif B && !C\n" + Duplicate
            + "class B {}\n#else\n" + Duplicate + "class C {}\n#endif\n" + Duplicate + "class D {}";
        Assert.Equal(new[] { 3, 7, 11, 15 }, SourceChecker.Analyze("src/C.cs", source).Select(d => d.Line));
    }

    [Theory]
    [InlineData("#define A\n#if A\n", "#else\nclass D {}\n#endif", 1)]
    [InlineData("#undef A\n#if A\n", "#endif", 0)]
    public void Source_defines_and_undefines_retain_their_meaning(string prefix, string suffix, int count) =>
        Assert.Equal(count, SourceChecker.Analyze("src/C.cs", prefix + Duplicate + "class C {}\n" + suffix).Count);

    [Fact]
    public void Excessive_conditional_scope_fails_explicitly()
    {
        var source = "#if " + string.Join(" || ", Enumerable.Range(0, 13).Select(i => $"S{i}")) + "\n#endif";
        Assert.Contains("more than 12", Assert.Throws<InvalidOperationException>(() => SourceChecker.Analyze("src/C.cs", source)).Message);
    }

    [Fact]
    public void Nested_inactive_directive_symbols_are_discovered()
    {
        var source = "#if A\n#if B\n" + Duplicate + "class C {}\n#endif\n#endif";
        Assert.Single(SourceChecker.Analyze("src/C.cs", source));
    }

    [Theory]
    [InlineData("src/C.g.cs")]
    [InlineData("src/C.G.I.CS")]
    [InlineData("src/C.generated.cs")]
    [InlineData("tests/C.DESIGNER.cs")]
    [InlineData("src/bin/C.cs")]
    [InlineData("tests/X/obj/C.cs")]
    [InlineData("tests/Muthur.XmlDocCheck.Tests/Fixtures/C.cs")]
    [InlineData("tools/C.cs")]
    [InlineData("src2/C.cs")]
    [InlineData("Src/C.cs")]
    [InlineData("src/C.txt")]
    public void Excluded_paths_are_ignored(string path) => Assert.Empty(SourceChecker.Analyze(path, Duplicate + "class C {}"));

    [Theory]
    [InlineData("// <AUTO-GENERATED/>\n")]
    [InlineData("/* <autogenerated> */\n")]
    [InlineData("\n// header\n// <auto-generated\n")]
    public void Leading_generated_comment_markers_are_ignored(string header) =>
        Assert.Empty(SourceChecker.Analyze("src/Migrations/C.cs", header + Duplicate + "class C {}"));

    [Theory]
    [InlineData("tests/Other/Fixtures/C.cs", "class Header { string s = \"<auto-generated>\"; }\n")]
    [InlineData("src/C.cs", "class Header {}\n// <auto-generated>\n")]
    public void Ordinary_fixtures_and_nonleading_markers_are_checked(string path, string header) =>
        Assert.Single(SourceChecker.Analyze(path, header + Duplicate + "class C {}"));
}
