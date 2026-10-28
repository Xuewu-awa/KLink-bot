using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal record MemberRow(string Id, string Name, string Kind, int Line, string TokensSha256);
internal record DocumentRow(string Id, string RawSha256, string TokensSha256, string ContextSha256,
    string StructureSha256, string[] ParseErrors, string[] DuplicateMemberIds, bool HasConditionalDirectives,
    MemberRow[] Members);
internal record RequestDocument(string Id, string Path);
internal record Request(RequestDocument[] Documents);

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string TokenString(SyntaxNode node)
    {
        var s = new StringBuilder();
        foreach (var t in node.DescendantTokens(descendIntoTrivia: false))
            s.Append(t.RawKind).Append(':').Append(t.Text.Length).Append(':').Append(t.Text).Append(';');
        return s.ToString();
    }

    private static string Parameters(BaseParameterListSyntax? list) => list is null ? "" : string.Join(",",
        list.Parameters.Select(p => string.Join(" ", p.Modifiers.Select(m => m.Text)) + ":" + (p.Type is null ? "?" : TokenString(p.Type))));

    private static string Container(SyntaxNode node) => string.Join("/", node.Ancestors().Reverse().Select(n => n switch
    {
        BaseNamespaceDeclarationSyntax x => "namespace:" + x.Name.ToString(),
        BaseTypeDeclarationSyntax x => x.Identifier.Text + (x is TypeDeclarationSyntax t ? "`" + (t.TypeParameterList?.Parameters.Count ?? 0) : ""),
        _ => null
    }).Where(x => x is not null));

    private static (string Kind, string Name, string Key)? Identity(MemberDeclarationSyntax node)
    {
        string kind, name, detail;
        switch (node)
        {
            case MethodDeclarationSyntax x:
                kind = "method"; name = x.Identifier.Text;
                detail = (x.ExplicitInterfaceSpecifier?.Name.ToString() ?? "") + "." + name + "`" + (x.TypeParameterList?.Parameters.Count ?? 0) + "(" + Parameters(x.ParameterList) + ")"; break;
            case ConstructorDeclarationSyntax x:
                kind = "constructor"; name = x.Identifier.Text;
                detail = (x.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static:" : "instance:") + name + "(" + Parameters(x.ParameterList) + ")"; break;
            case DestructorDeclarationSyntax x:
                kind = "destructor"; name = x.Identifier.Text; detail = name; break;
            case PropertyDeclarationSyntax x:
                kind = "property"; name = x.Identifier.Text; detail = (x.ExplicitInterfaceSpecifier?.Name.ToString() ?? "") + "." + name; break;
            case IndexerDeclarationSyntax x:
                kind = "indexer"; name = "this"; detail = (x.ExplicitInterfaceSpecifier?.Name.ToString() ?? "") + "(" + Parameters(x.ParameterList) + ")"; break;
            case EventDeclarationSyntax x:
                kind = "event"; name = x.Identifier.Text; detail = (x.ExplicitInterfaceSpecifier?.Name.ToString() ?? "") + "." + name; break;
            case BaseFieldDeclarationSyntax x:
                kind = x is EventFieldDeclarationSyntax ? "event_field" : "field";
                name = string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)); detail = name; break;
            case OperatorDeclarationSyntax x:
                kind = "operator"; name = x.OperatorToken.Text; detail = name + "(" + Parameters(x.ParameterList) + ")"; break;
            case ConversionOperatorDeclarationSyntax x:
                kind = "conversion"; name = x.ImplicitOrExplicitKeyword.Text;
                detail = name + ":" + TokenString(x.Type) + "(" + Parameters(x.ParameterList) + ")"; break;
            case DelegateDeclarationSyntax x:
                kind = "delegate"; name = x.Identifier.Text;
                detail = name + "`" + (x.TypeParameterList?.Parameters.Count ?? 0) + "(" + Parameters(x.ParameterList) + ")"; break;
            case EnumMemberDeclarationSyntax x:
                kind = "enum_member"; name = x.Identifier.Text; detail = name; break;
            default: return null;
        }
        return (kind, name, Container(node) + "::" + kind + ":" + detail);
    }

    private static DocumentRow Analyze(string id, byte[] bytes)
    {
        // Preserve source newlines inside literal tokens. Global newline
        // canonicalization would hide meaningful verbatim/raw-string changes.
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            return new(id, Convert.ToHexStringLower(SHA256.HashData(bytes)), "", "", "",
                ["Invalid UTF-8 source; no syntax equivalence inferred."], [], false, []);
        }
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetCompilationUnitRoot();
        var context = new StringBuilder();
        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>()) context.Append(TokenString(u));
        foreach (var e in root.DescendantNodes().OfType<ExternAliasDirectiveSyntax>()) context.Append(TokenString(e));
        foreach (var a in root.AttributeLists) context.Append(TokenString(a));
        foreach (var n in root.DescendantNodes())
        {
            if (n is BaseNamespaceDeclarationSyntax ns) context.Append("namespace:").Append(TokenString(ns.Name));
            else if (n is TypeDeclarationSyntax type) context.Append(TokenString(type.WithMembers(default)));
            else if (n is EnumDeclarationSyntax en) context.Append(TokenString(en.WithMembers(default)));
        }
        var trivia = root.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsDirective || t.IsKind(SyntaxKind.DisabledTextTrivia)).ToArray();
        foreach (var t in trivia) context.Append("trivia:").Append(t.RawKind).Append(':').Append(t.ToFullString());
        var members = new List<MemberRow>();
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var identity = Identity(node);
            if (identity is not { } key) continue;
            members.Add(new(key.Key, key.Name, key.Kind, tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, Hash(TokenString(node))));
        }
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        if (globals.Length > 0)
            members.Add(new("@top-level", "@top-level", "top_level", 1, Hash(string.Join("", globals.Select(TokenString)))));
        string tokens = Hash(TokenString(root)), ctx = Hash(context.ToString());
        return new(id, Convert.ToHexStringLower(SHA256.HashData(bytes)), tokens, ctx, Hash(tokens + ":" + ctx),
            tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray(),
            members.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToArray(),
            trivia.Any(t => t.IsKind(SyntaxKind.IfDirectiveTrivia) || t.IsKind(SyntaxKind.ElifDirectiveTrivia)),
            members.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray());
    }

    private static void SelfTest()
    {
        DocumentRow A(string s) => Analyze("fixture", Encoding.UTF8.GetBytes(s));
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var plain = A("namespace N; class C { int x=1; int F(int a=1) => a+x; }");
        var comment = A("// comment\nnamespace N; class C { /* a */ int x = 1; int F(int a = 1) => a + x; }");
        Require(plain.StructureSha256 == comment.StructureSha256, "comments/spacing");
        Require(plain.Members.Single(m => m.Name == "F").TokensSha256 != A("namespace N; class C { int x=1; int F(int a=2) => a+x; }").Members.Single(m => m.Name == "F").TokensSha256, "default parameter");
        var extra = A("namespace N; class C { int x=1; int F(int a=1) => a+x; int G()=>2; }");
        Require(extra.Members.Length == plain.Members.Length + 1 && extra.Members.Single(m => m.Name == "F").TokensSha256 == plain.Members.Single(m => m.Name == "F").TokensSha256, "added member");
        Require(A("class C { string F()=>@\"a\r\nb\"; }").StructureSha256 != A("class C { string F()=>@\"a\nb\"; }").StructureSha256, "literal newline");
        Require(A("#if NEVER\nclass Hidden{int x=1;}\n#endif\nclass C{}").StructureSha256 != A("#if NEVER\nclass Hidden{int x=2;}\n#endif\nclass C{}").StructureSha256, "disabled branch");
        Require(A("class C { int F(int a)=>1; int F(string a)=>1; }").DuplicateMemberIds.Length == 0, "overloads");
        Require(A("class C:B {}").ContextSha256 != A("class C:D {}").ContextSha256, "base type");
        Require(A("class C { void F( }").ParseErrors.Length > 0, "parse errors");
        Require(A("partial class C { partial void F(); partial void F(){} }").DuplicateMemberIds.Length == 1, "duplicate identity not silently overwritten");
        Require(A("namespace A{class C{void F(){}}} namespace B{class C{void F(){}}}").DuplicateMemberIds.Length == 0, "namespace keys");
        Require(A("class C { int x=1; int F()=>x; }").Members.Single(m => m.Kind == "field").TokensSha256 != A("class C { int x=2; int F()=>x; }").Members.Single(m => m.Kind == "field").TokensSha256, "field initializer");
        Require(A("class C { C(){} static C(){} int P{get;set;} event System.Action? E; }").DuplicateMemberIds.Length == 0, "constructors/properties/events");
        Require(Analyze("invalid", [0xff]).ParseErrors.Length > 0, "invalid source encoding");
        Console.WriteLine("Roslyn fixtures passed: 13");
    }

    public static int Main(string[] args)
    {
        if (args is ["--selftest"]) { SelfTest(); return 0; }
        if (args.Length != 2) { Console.Error.WriteLine("AuditHistory request.json output.json | --selftest"); return 2; }
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[0]), Json) ?? throw new Exception("missing request");
        var documents = new List<DocumentRow>();
        foreach (var input in request.Documents)
        {
            documents.Add(Analyze(input.Id, File.ReadAllBytes(input.Path)));
            if (documents.Count % 25 == 0) Console.WriteLine($"parsed {documents.Count}/{request.Documents.Length}");
        }
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { parser_version = typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString(),
            parse_options = "Preview language, regular code, empty preprocessor symbols; disabled source retained in context fingerprint; no semantic compilation", documents }, Json), new UTF8Encoding(false));
        Console.WriteLine($"parsed {documents.Count}/{request.Documents.Length}");
        return 0;
    }
}
