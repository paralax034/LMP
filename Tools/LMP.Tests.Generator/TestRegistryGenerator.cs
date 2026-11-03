using System;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LMP.Tests.Generator;

/// <summary>
/// Инкрементальный генератор реестра тестов. Собирает статические методы с <c>[TestMethod]</c>
/// из пространств имён <c>LMP.Tests*</c> и порождает <c>GeneratedTestRegistry.Create()</c>:
/// массив регистраций с атрибутом, сигнатурой и статическим делегатом вызова без рефлексии.
/// Неподдерживаемая сигнатура — ошибка сборки (LMPT001), недоступный для вызова метод — предупреждение (LMPT002).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TestRegistryGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "LMP.Tests.Framework.TestMethodAttribute";
    private const string RequiredNamespacePrefix = "LMP.Tests";

    private static readonly DiagnosticDescriptor UnsupportedSignature = new(
        id: "LMPT001",
        title: "Unsupported test method signature",
        messageFormat: "Test method '{0}' {1}",
        category: "LMP.Tests",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotInvocable = new(
        id: "LMPT002",
        title: "Test method is not discoverable",
        messageFormat: "Test method '{0}' is ignored: {1}",
        category: "LMP.Tests",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var frameworkPresent = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(AttributeMetadataName) is not null);

        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, ct) => Analyze(ctx, ct));

        var collected = candidates.Collect().Combine(frameworkPresent);

        context.RegisterSourceOutput(collected, static (spc, input) =>
        {
            var (items, present) = input;
            if (!present)
                return;

            var valid = new List<TestModel>(items.Length);
            foreach (var item in items)
            {
                if (item.Diagnostic is { } diag)
                {
                    spc.ReportDiagnostic(diag.ToDiagnostic());
                    continue;
                }

                if (item.Model is { } model)
                    valid.Add(model);
            }

            valid.Sort(static (a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
            spc.AddSource("GeneratedTestRegistry.g.cs", SourceText.From(Emit(valid), Encoding.UTF8));
        });
    }

    private static Candidate Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not IMethodSymbol method || ctx.Attributes.Length == 0)
            return default;

        var type = method.ContainingType;
        string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        // Область обнаружения прежняя: только пространства имён LMP.Tests*, остальное молча пропускается.
        if (!ns.StartsWith(RequiredNamespacePrefix, StringComparison.Ordinal))
            return default;

        string display = $"{type.Name}.{method.Name}";
        var location = method.Locations.Length > 0 ? method.Locations[0] : Location.None;

        if (!type.IsStatic || type.TypeKind != TypeKind.Class)
            return Candidate.Fail(NotInvocable, location, display, "the containing type must be a static class");

        if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public)
            return Candidate.Fail(NotInvocable, location, display, "the method must be public static");

        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return Candidate.Fail(NotInvocable, location, display, $"type '{t.Name}' is not accessible from the generated registry");

            if (t.IsGenericType)
                return Candidate.Fail(UnsupportedSignature, location, display, "must not be declared in a generic type");
        }

        if (method.IsGenericMethod)
            return Candidate.Fail(UnsupportedSignature, location, display, "must not be generic");

        var compilation = ctx.SemanticModel.Compilation;
        var taskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        var servicesType = compilation.GetTypeByMetadataName("System.IServiceProvider");
        var tokenType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, taskType))
            return Candidate.Fail(UnsupportedSignature, location, display,
                $"must return System.Threading.Tasks.Task, got '{method.ReturnType.ToDisplayString()}'");

        var p = method.Parameters;
        foreach (var parameter in p)
        {
            if (parameter.RefKind != RefKind.None)
                return Candidate.Fail(UnsupportedSignature, location, display, "must not use ref/out/in parameters");
        }

        bool IsServices(int i) => SymbolEqualityComparer.Default.Equals(p[i].Type, servicesType);
        bool IsToken(int i) => SymbolEqualityComparer.Default.Equals(p[i].Type, tokenType);

        string? signature = p.Length switch
        {
            0 => "None",
            1 when IsServices(0) => "ServicesOnly",
            1 when IsToken(0) => "TokenOnly",
            2 when IsServices(0) && IsToken(1) => "ServicesAndToken",
            2 when IsToken(0) && IsServices(1) => "TokenAndServices",
            _ => null
        };

        if (signature is null)
            return Candidate.Fail(UnsupportedSignature, location, display,
                "has unsupported parameters: expected (), (CancellationToken), (IServiceProvider), " +
                "(IServiceProvider, CancellationToken) or (CancellationToken, IServiceProvider)");

        var attr = ctx.Attributes[0];
        string category = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is { } raw
            ? Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";
        string displayName = attr.ConstructorArguments.Length > 1
            ? Literal(attr.ConstructorArguments[1])
            : "null";

        var named = new StringBuilder();
        foreach (var pair in attr.NamedArguments)
        {
            if (named.Length > 0) named.Append(", ");
            named.Append(pair.Key).Append(" = ").Append(Literal(pair.Value));
        }

        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string methodName = SyntaxFacts.GetKeywordKind(method.Name) != SyntaxKind.None ? "@" + method.Name : method.Name;

        return new Candidate(new TestModel(
            SortKey: $"{type.Name}.{method.Name}|{typeName}",
            ClassName: type.Name,
            MethodName: method.Name,
            InvokeTarget: $"{typeName}.{methodName}",
            Category: category,
            DisplayName: displayName,
            NamedArguments: named.ToString(),
            Signature: signature), null);
    }

    private static string Literal(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";

        return constant.Value switch
        {
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            bool b => b ? "true" : "false",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => constant.ToCSharpString()
        };
    }

    private static string Emit(List<TestModel> models)
    {
        const string F = "global::LMP.Tests.Framework.";
        var sb = new StringBuilder(256 + models.Count * 512);

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace LMP.Tests.Framework;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Реестр тестов, собранный генератором LMP.Tests.Generator на этапе компиляции.</summary>");
        sb.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"LMP.Tests.Generator\", \"1.0\")]");
        sb.AppendLine("internal static class GeneratedTestRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Создаёт регистрации всех обнаруженных тестов в детерминированном порядке.</summary>");
        sb.Append("    internal static ").Append(F).AppendLine("TestRegistration[] Create() =>");
        sb.AppendLine("    [");

        foreach (var m in models)
        {
            string args = m.Signature switch
            {
                "ServicesOnly" => "services",
                "TokenOnly" => "ct",
                "ServicesAndToken" => "services, ct",
                "TokenAndServices" => "ct, services",
                _ => string.Empty
            };

            sb.Append("        new(").Append(SymbolDisplay.FormatLiteral(m.ClassName, quote: true))
              .Append(", ").Append(SymbolDisplay.FormatLiteral(m.MethodName, quote: true)).AppendLine(",");
            sb.Append("            new ").Append(F).Append("TestMethodAttribute((").Append(F).Append("TestCategory)")
              .Append(m.Category).Append(", ").Append(m.DisplayName).Append(')');
            if (m.NamedArguments.Length > 0)
                sb.Append(" { ").Append(m.NamedArguments).Append(" }");
            sb.AppendLine(",");
            sb.Append("            ").Append(F).Append("TestParameterSignature.").Append(m.Signature).AppendLine(",");
            sb.Append("            static (services, ct) => ").Append(m.InvokeTarget).Append('(').Append(args).AppendLine(")),");
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Неизменяемая модель одного теста; равенство по значению сохраняет кэш инкрементального конвейера.</summary>
    private readonly record struct TestModel(
        string SortKey,
        string ClassName,
        string MethodName,
        string InvokeTarget,
        string Category,
        string DisplayName,
        string NamedArguments,
        string Signature);

    /// <summary>Сериализуемое описание диагностики без ссылок на синтаксические деревья.</summary>
    private readonly record struct DiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        string? FilePath,
        TextSpan Span,
        LinePositionSpan LineSpan,
        string Arg0,
        string Arg1)
    {
        public Diagnostic ToDiagnostic() => Diagnostic.Create(
            Descriptor,
            FilePath is null ? Location.None : Location.Create(FilePath, Span, LineSpan),
            Arg0,
            Arg1);
    }

    /// <summary>Результат анализа метода: модель для реестра либо диагностика.</summary>
    private readonly record struct Candidate(TestModel? Model, DiagnosticInfo? Diagnostic)
    {
        public static Candidate Fail(DiagnosticDescriptor descriptor, Location location, string member, string reason)
        {
            var line = location.GetLineSpan();
            return new Candidate(null, new DiagnosticInfo(
                descriptor,
                location.IsInSource ? line.Path : null,
                location.SourceSpan,
                line.Span,
                member,
                reason));
        }
    }
}