using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LMP.Tools.AotScanner;

/// <summary>
/// Two-factor Native AOT + Source Tree dead code, visibility, interface, and structural duplication analyzer.
/// Protects against false positives (extension linking, constant inlining, P/Invoke structs, source generators).
/// </summary>
public static partial class AotScanner
{
    private enum MemberKind
    {
        Type,
        Method,
        Field,
        Event,
        PrivateCandidate,
        DeadInterface
    }

    private readonly record struct SourceFile(
        string FilePath,
        string RelativePath,
        string RawContent,
        string CodeOnlyContent,
        string[] Lines);

    private readonly record struct DeadCodeEntry(
        string FilePath,
        int LineNumber,
        string LineText,
        MemberKind Kind,
        string MemberName,
        string ParentTypeName,
        string Description);

    private readonly record struct DuplicateCluster(
        int StatementCount,
        string SampleFirstLine,
        List<(string RelativePath, int StartLine, int EndLine)> Locations);

    [GeneratedRegex(@"///[^\r\n]*|//[^\r\n]*|/\*[\s\S]*?\*/", RegexOptions.Compiled)]
    private static partial Regex CommentStripRegex();

    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Usage: AotScanner <dll_path> <codegen.dgml> <source_dir> [report=DeadCode.txt]");
            Console.WriteLine(@"Example: AotScanner ""Core\bin\Release\net11.0\LMP.Core.dll"" ""obj\Release\net11.0\win-x64\native\LMP.codegen.dgml.xml"" ""D:\Projects\CS\LMP""");
            Console.ResetColor();
            return 1;
        }

        string assemblyPath = args[0];
        string codegenPath = args[1];
        string sourceDir = args[2];
        string outputPath = args.Length > 3 ? args[3] : "DeadCode.txt";

        if (!File.Exists(assemblyPath) || !File.Exists(codegenPath) || !Directory.Exists(sourceDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] One of the specified paths does not exist.");
            Console.ResetColor();
            return 1;
        }

        string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        string rootPrefix = assemblyName.Contains('.') ? assemblyName[..assemblyName.IndexOf('.')] : assemblyName;

        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine(" LMP Native AOT Structured Dead Code & Clone Scanner (v3.0)");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine($"Assembly:    {assemblyPath}");
        Console.WriteLine($"AOT Graph:   {codegenPath}");
        Console.WriteLine($"Source Root: {sourceDir}");
        Console.WriteLine($"Report:      {outputPath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        Console.Write("[1/4] Caching C# and AXAML files (stripping comments)... ");
        var sourceFiles = LoadSourceFiles(sourceDir);
        Console.WriteLine($"Done ({sourceFiles.Length:N0} files)");

        Console.Write("[2/4] Indexing Native AOT codegen symbols... ");
        var survivedSymbols = LoadCodegenSymbolsFiltered(codegenPath, rootPrefix);
        Console.WriteLine($"Done ({survivedSymbols.Length:N0} symbols)");

        Console.Write("[3/4] Scanning structural code clones across C# files... ");
        var duplicates = DetectStructuralDuplicates(sourceFiles);
        Console.WriteLine($"Done ({duplicates.Count:N0} clone groups)");

        Console.Write("[4/4] Auditing types, methods, visibility, and interfaces... ");
        var stats = AnalyzeAndWriteReport(assemblyPath, survivedSymbols, sourceFiles, duplicates, sourceDir, outputPath);
        Console.WriteLine("Done");

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SUCCESS] Audit completed in {sw.ElapsedMilliseconds} ms");
        Console.ResetColor();
        Console.WriteLine($"  Files with findings:       {stats.FilesCount:N0}");
        Console.WriteLine($"  Dead Types / Interfaces:   {stats.DeadTypes:N0}");
        Console.WriteLine($"  Dead Methods:              {stats.DeadMethods:N0}");
        Console.WriteLine($"  Dead Fields / Constants:   {stats.DeadFields:N0}");
        Console.WriteLine($"  Dead Events:               {stats.DeadEvents:N0}");
        Console.WriteLine($"  Over-exposed Public APIs:  {stats.PrivateCandidates:N0}");
        Console.WriteLine($"  Duplicate Code Clusters:   {duplicates.Count:N0}");
        Console.WriteLine($"  Total Hard Defects:        {stats.TotalDefects:N0}");
        Console.WriteLine($"  Report written to:         {Path.GetFullPath(outputPath)}");
        Console.WriteLine("──────────────────────────────────────────────────────────────");

        return stats.TotalDefects;
    }

    private static string PrettyPrintMemberName(string methodName) => methodName switch
    {
        "op_GreaterThan" => "operator >",
        "op_LessThan" => "operator <",
        "op_GreaterThanOrEqual" => "operator >=",
        "op_LessThanOrEqual" => "operator <=",
        "op_Equality" => "operator ==",
        "op_Inequality" => "operator !=",
        "op_Implicit" => "implicit operator",
        "op_Explicit" => "explicit operator",
        "op_Addition" => "operator +",
        "op_Subtraction" => "operator -",
        "op_Multiply" => "operator *",
        "op_Division" => "operator /",
        "op_Modulus" => "operator %",
        "op_BitwiseAnd" => "operator &",
        "op_BitwiseOr" => "operator |",
        "op_ExclusiveOr" => "operator ^",
        "op_LogicalNot" => "operator !",
        "op_UnaryNegation" => "operator -",
        "op_Increment" => "operator ++",
        "op_Decrement" => "operator --",
        _ => methodName
    };

    private static SourceFile[] LoadSourceFiles(string sourceDir)
    {
        var list = new List<SourceFile>(1024);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".axaml" };

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!extensions.Contains(ext)) continue;

            if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\.vs\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\External\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\publish", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                string[] lines = text.Split('\n');
                string relPath = Path.GetRelativePath(sourceDir, file);
                string codeOnly = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? CommentStripRegex().Replace(text, "")
                    : text;
                list.Add(new SourceFile(file, relPath, text, codeOnly, lines));
            }
            catch { }
        }

        return list.ToArray();
    }

    private static string[] LoadCodegenSymbolsFiltered(string codegenPath, string scopePrefix)
    {
        var symbols = new List<string>(65536);
        var xmlSettings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = true
        };

        using var stream = new FileStream(codegenPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, xmlSettings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || !reader.LocalName.Equals("Node", StringComparison.Ordinal))
                continue;

            string? label = reader.GetAttribute("Label") ?? reader.GetAttribute("Id");
            if (label is not null && label.Contains(scopePrefix, StringComparison.OrdinalIgnoreCase))
                symbols.Add(label);
        }

        return symbols.ToArray();
    }

    private static List<(string FilePath, int LineNumber, string LineText)> LocateAllTypeDeclarations(
        string cleanTypeName, SourceFile[] sources)
    {
        string[] keywords = ["class ", "struct ", "record ", "enum ", "interface "];
        var results = new List<(string FilePath, int LineNumber, string LineText)>(4);

        for (int s = 0; s < sources.Length; s++)
        {
            var src = sources[s];
            if (!src.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                CountOccurrences(src.CodeOnlyContent, cleanTypeName) == 0)
            {
                continue;
            }

            for (int l = 0; l < src.Lines.Length; l++)
            {
                string line = src.Lines[l];
                if (CountOccurrences(line, cleanTypeName) == 0)
                    continue;

                for (int p = 0; p < keywords.Length; p++)
                {
                    if (line.Contains(keywords[p], StringComparison.Ordinal))
                    {
                        results.Add((src.FilePath, l + 1, line.Trim()));
                        break;
                    }
                }
            }
        }

        return results;
    }

    private static (string FilePath, int LineNumber, string LineText) LocateMemberAcrossTypeFiles(
        List<(string FilePath, int LineNumber, string LineText)> typeFiles,
        string memberName,
        SourceFile[] sources,
        bool fallbackToTypeHeader)
    {
        for (int tf = 0; tf < typeFiles.Count; tf++)
        {
            string candidateFile = typeFiles[tf].FilePath;
            for (int s = 0; s < sources.Length; s++)
            {
                if (!sources[s].FilePath.Equals(candidateFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                var lines = sources[s].Lines;
                for (int l = 0; l < lines.Length; l++)
                {
                    string line = lines[l];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal))
                        continue;

                    if (CountOccurrences(line, memberName) > 0 &&
                        (line.Contains("public", StringComparison.Ordinal) ||
                         line.Contains("private", StringComparison.Ordinal) ||
                         line.Contains("internal", StringComparison.Ordinal) ||
                         line.Contains("protected", StringComparison.Ordinal) ||
                         line.Contains("static", StringComparison.Ordinal) ||
                         line.Contains("event", StringComparison.Ordinal) ||
                         line.Contains("const", StringComparison.Ordinal) ||
                         line.Contains("readonly", StringComparison.Ordinal)))
                    {
                        return (candidateFile, l + 1, line.Trim());
                    }
                }
                break;
            }
        }

        return fallbackToTypeHeader && typeFiles.Count > 0
            ? typeFiles[0]
            : (string.Empty, 0, string.Empty);
    }

    private static bool IsCompilerOrRuntimeSynthetic(string name) =>
        name.StartsWith("<", StringComparison.Ordinal) ||
        name.StartsWith("__set_", StringComparison.Ordinal) ||
        name.StartsWith("__get_", StringComparison.Ordinal) ||
        name.StartsWith("__Init_", StringComparison.Ordinal) ||
        name.StartsWith("PropName_", StringComparison.Ordinal) ||
        name.Contains("global::", StringComparison.Ordinal) ||
        name.Contains("IJsonTypeInfoResolver.", StringComparison.Ordinal) ||
        name.Equals(".cctor", StringComparison.Ordinal) ||
        name.Equals(".ctor", StringComparison.Ordinal) ||
        name.Equals("Invoke", StringComparison.Ordinal) ||
        name.Equals("BeginInvoke", StringComparison.Ordinal) ||
        name.Equals("EndInvoke", StringComparison.Ordinal) ||
        name.Equals("Deconstruct", StringComparison.Ordinal) ||
        name.Equals("PrintMembers", StringComparison.Ordinal) ||
        name.Equals("GetHashCode", StringComparison.Ordinal) ||
        name.Equals("Equals", StringComparison.Ordinal) ||
        name.Equals("ToString", StringComparison.Ordinal) ||
        name.Equals("Dispose", StringComparison.Ordinal) ||
        name.Equals("DisposeAsync", StringComparison.Ordinal) ||
        name.Equals("<Clone>$", StringComparison.Ordinal) ||
        name.Contains("ImplementationDetails", StringComparison.Ordinal) ||
        name.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal) ||
        name.Contains("MemoryPack", StringComparison.Ordinal) ||
        name.Contains("InterpolatedStringHandler", StringComparison.Ordinal);

    private static int CountOccurrences(string content, string word)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(word)) return 0;
        int count = 0;
        int idx = 0;
        while ((idx = content.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(content[idx - 1]) && content[idx - 1] != '_';
            bool rightOk = (idx + word.Length >= content.Length) ||
                           !char.IsLetterOrDigit(content[idx + word.Length]) && content[idx + word.Length] != '_';

            if (leftOk && rightOk) count++;
            idx += word.Length;
        }
        return count;
    }

    /// <summary>
    /// Detects structural duplicate code blocks across C# files by normalizing whitespace
    /// and hashing sliding windows of executable statements.
    /// </summary>
    private static List<DuplicateCluster> DetectStructuralDuplicates(SourceFile[] sources)
    {
        const int WindowSize = 10;
        var hashBuckets = new Dictionary<ulong, List<(string RelPath, int StartLine, int EndLine, string FirstLine)>>();

        for (int s = 0; s < sources.Length; s++)
        {
            var src = sources[s];
            if (!src.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                src.RelativePath.StartsWith("SharpJaad.AAC", StringComparison.OrdinalIgnoreCase) ||
                src.RelativePath.StartsWith("Tools", StringComparison.OrdinalIgnoreCase) ||
                src.RelativePath.StartsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
                src.RelativePath.Contains(@"Repeater\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normLines = new List<(int OrigLine, string Normalized, string RawTrimmed)>(src.Lines.Length);
            bool inBlockComment = false;

            for (int l = 0; l < src.Lines.Length; l++)
            {
                string trimmed = src.Lines[l].Trim();
                if (inBlockComment)
                {
                    int endComment = trimmed.IndexOf("*/", StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        inBlockComment = false;
                        trimmed = trimmed[(endComment + 2)..].Trim();
                    }
                    else continue;
                }

                if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (!trimmed.Contains("*/", StringComparison.Ordinal))
                        inBlockComment = true;
                    continue;
                }

                if (string.IsNullOrEmpty(trimmed) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith('#') ||
                    trimmed.StartsWith('[') ||
                    trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("namespace ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("protected ", StringComparison.Ordinal) ||
                    trimmed is "{" or "}" or "};" or ");")
                {
                    continue;
                }

                int inlineComment = trimmed.IndexOf("//", StringComparison.Ordinal);
                if (inlineComment > 0 && !trimmed[..inlineComment].Contains('"'))
                    trimmed = trimmed[..inlineComment].TrimEnd();

                if (trimmed.Length < 12)
                    continue;

                string normalized = NormalizeStatementForCloneHash(trimmed);
                normLines.Add((l + 1, normalized, trimmed));
            }

            if (normLines.Count < WindowSize)
                continue;

            for (int i = 0; i <= normLines.Count - WindowSize; i++)
            {
                int controlFlowOrCallCount = 0;
                ulong hash = 14695981039346656037UL;

                for (int w = 0; w < WindowSize; w++)
                {
                    string stmt = normLines[i + w].Normalized;
                    if (stmt.Contains('(') || stmt.StartsWith("if", StringComparison.Ordinal) ||
                        stmt.StartsWith("for", StringComparison.Ordinal) || stmt.StartsWith("while", StringComparison.Ordinal) ||
                        stmt.StartsWith("return", StringComparison.Ordinal) || stmt.StartsWith("await", StringComparison.Ordinal))
                    {
                        controlFlowOrCallCount++;
                    }

                    for (int c = 0; c < stmt.Length; c++)
                    {
                        hash ^= stmt[c];
                        hash *= 1099511628211UL;
                    }
                    hash ^= (byte)'\n';
                    hash *= 1099511628211UL;
                }

                if (controlFlowOrCallCount < WindowSize / 2)
                    continue;

                if (!hashBuckets.TryGetValue(hash, out var list))
                {
                    list = new List<(string, int, int, string)>(2);
                    hashBuckets[hash] = list;
                }

                int startLine = normLines[i].OrigLine;
                int endLine = normLines[i + WindowSize - 1].OrigLine;

                if (list.Count > 0 &&
                    list[^1].RelPath.Equals(src.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(list[^1].StartLine - startLine) < WindowSize)
                {
                    continue;
                }

                list.Add((src.RelativePath, startLine, endLine, normLines[i].RawTrimmed));
            }
        }

        var clusters = new List<DuplicateCluster>();
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in hashBuckets)
        {
            var locs = kvp.Value;
            if (locs.Count < 2) continue;

            string signatureKey = string.Join("|", locs.Select(x => $"{x.RelPath}:{x.StartLine / 15}"));
            if (!seenPairs.Add(signatureKey))
                continue;

            clusters.Add(new DuplicateCluster(
                WindowSize,
                locs[0].FirstLine,
                locs.Select(x => (x.RelPath, x.StartLine, x.EndLine)).ToList()));
        }

        return clusters
            .OrderByDescending(c => c.Locations.Count)
            .ThenBy(c => c.Locations[0].RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
    }

    private static string NormalizeStatementForCloneHash(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool inSpace = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace) { sb.Append(' '); inSpace = true; }
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return sb.ToString();
    }

    private static (int FilesCount, int DeadTypes, int DeadMethods, int DeadFields, int DeadEvents, int PrivateCandidates, int TotalDefects)
        AnalyzeAndWriteReport(
            string assemblyPath,
            string[] survivedSymbols,
            SourceFile[] sources,
            List<DuplicateCluster> duplicates,
            string sourceRoot,
            string outputPath)
    {
        using var fileStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fileStream);
        var metadataReader = peReader.GetMetadataReader();

        var typeEntries = new List<TypeInspectionModel>();
        var interfaceNames = new List<(string FullName, string CleanName)>();
        var implementedInterfaceNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeHandle);
            string rawTypeName = metadataReader.GetString(typeDef.Name);

            if (string.IsNullOrEmpty(rawTypeName) ||
                rawTypeName.StartsWith("<", StringComparison.Ordinal) ||
                rawTypeName.Equals("<Module>", StringComparison.Ordinal) ||
                rawTypeName.Contains("ImplementationDetails", StringComparison.Ordinal) ||
                rawTypeName.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal) ||
                rawTypeName.Contains("InterpolatedStringHandler", StringComparison.Ordinal))
            {
                continue;
            }

            int backtickIndex = rawTypeName.IndexOf('`');
            string cleanTypeName = backtickIndex > 0 ? rawTypeName[..backtickIndex] : rawTypeName;

            string typeNamespace = metadataReader.GetString(typeDef.Namespace);
            string fullTypeName = string.IsNullOrEmpty(typeNamespace)
                ? cleanTypeName
                : $"{typeNamespace}.{cleanTypeName}";

            if (typeDef.Attributes.HasFlag(TypeAttributes.Interface))
            {
                interfaceNames.Add((fullTypeName, cleanTypeName));
                continue;
            }

            foreach (var ifaceImplHandle in typeDef.GetInterfaceImplementations())
            {
                var ifaceImpl = metadataReader.GetInterfaceImplementation(ifaceImplHandle);
                string ifaceName = ResolveEntitySimpleName(metadataReader, ifaceImpl.Interface);
                if (!string.IsNullOrEmpty(ifaceName))
                    implementedInterfaceNames.Add(ifaceName);
            }

            string baseTypeName = ResolveEntitySimpleName(metadataReader, typeDef.BaseType);
            if (baseTypeName.Equals("Enum", StringComparison.Ordinal) ||
                baseTypeName.Equals("JsonSerializerContext", StringComparison.Ordinal))
            {
                continue;
            }

            bool isExplicitOrSequential = typeDef.Attributes.HasFlag(TypeAttributes.SequentialLayout) ||
                                          typeDef.Attributes.HasFlag(TypeAttributes.ExplicitLayout);

            bool isStaticClass = typeDef.Attributes.HasFlag(TypeAttributes.Abstract) &&
                                 typeDef.Attributes.HasFlag(TypeAttributes.Sealed);

            var visibility = typeDef.Attributes & TypeAttributes.VisibilityMask;
            bool isNestedPrivate = visibility is TypeAttributes.NestedPrivate or TypeAttributes.NestedFamANDAssem;

            if (typeDef.IsNested)
            {
                var declaringTypeDef = metadataReader.GetTypeDefinition(typeDef.GetDeclaringType());
                string declaringName = metadataReader.GetString(declaringTypeDef.Name);
                string declaringNamespace = metadataReader.GetString(declaringTypeDef.Namespace);
                fullTypeName = string.IsNullOrEmpty(declaringNamespace)
                    ? $"{declaringName}.{cleanTypeName}"
                    : $"{declaringNamespace}.{declaringName}.{cleanTypeName}";
            }

            var methods = new List<(string Name, bool IsPublic, bool IsVirtualOrInterface)>();
            foreach (var mh in typeDef.GetMethods())
            {
                var methodDef = metadataReader.GetMethodDefinition(mh);
                string mName = metadataReader.GetString(methodDef.Name);

                if (IsCompilerOrRuntimeSynthetic(mName) ||
                    mName.StartsWith("get_", StringComparison.Ordinal) ||
                    mName.StartsWith("set_", StringComparison.Ordinal) ||
                    mName.StartsWith("add_", StringComparison.Ordinal) ||
                    mName.StartsWith("remove_", StringComparison.Ordinal) ||
                    mName.Equals("System.Collections.IEnumerator.Reset", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isPublic = (methodDef.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                bool isVirtual = methodDef.Attributes.HasFlag(MethodAttributes.Virtual);
                methods.Add((mName, isPublic, isVirtual));
            }

            var fields = new List<string>();
            if (!isExplicitOrSequential)
            {
                foreach (var fh in typeDef.GetFields())
                {
                    var fieldDef = metadataReader.GetFieldDefinition(fh);
                    string fName = metadataReader.GetString(fieldDef.Name);

                    if (IsCompilerOrRuntimeSynthetic(fName) || fName.Equals("value__", StringComparison.Ordinal))
                        continue;

                    fields.Add(fName);
                }
            }

            var events = new List<string>();
            foreach (var eh in typeDef.GetEvents())
            {
                var eventDef = metadataReader.GetEventDefinition(eh);
                string eName = metadataReader.GetString(eventDef.Name);

                if (!IsCompilerOrRuntimeSynthetic(eName))
                    events.Add(eName);
            }

            typeEntries.Add(new TypeInspectionModel(
                fullTypeName, cleanTypeName, isExplicitOrSequential, isStaticClass, isNestedPrivate, methods, fields, events));
        }

        var deadEntries = new ConcurrentBag<DeadCodeEntry>();

        // --- 0. INTERFACE CHECK ---
        foreach (var (fullIfaceName, cleanIfaceName) in interfaceNames)
        {
            if (implementedInterfaceNames.Contains(cleanIfaceName))
                continue;

            int totalRefs = 0;
            for (int s = 0; s < sources.Length; s++)
            {
                totalRefs += CountOccurrences(sources[s].CodeOnlyContent, cleanIfaceName);
                if (totalRefs > 1) break;
            }

            if (totalRefs <= 1)
            {
                var locs = LocateAllTypeDeclarations(cleanIfaceName, sources);
                if (locs.Count > 0)
                {
                    deadEntries.Add(new DeadCodeEntry(
                        locs[0].FilePath,
                        locs[0].LineNumber,
                        locs[0].LineText,
                        MemberKind.DeadInterface,
                        cleanIfaceName,
                        fullIfaceName,
                        "Interface has 0 implementations and 0 references across the solution"));
                }
            }
        }

        Parallel.ForEach(typeEntries, typeModel =>
        {
            var typeFiles = LocateAllTypeDeclarations(typeModel.CleanName, sources);
            if (typeFiles.Count == 0) return;

            var (typeFile, typeLine, typeLineText) = typeFiles[0];
            var ownFileSet = new HashSet<string>(typeFiles.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);

            var typeSpecificSymbols = new List<string>(32);
            for (int i = 0; i < survivedSymbols.Length; i++)
            {
                if (survivedSymbols[i].Contains(typeModel.CleanName, StringComparison.Ordinal))
                    typeSpecificSymbols.Add(survivedSymbols[i]);
            }

            // --- 1. TYPE CHECK (Dead Class / Struct / Record / Enum) ---
            if (!typeModel.IsNativeInteropStruct)
            {
                if (typeSpecificSymbols.Count == 0 && (typeModel.Methods.Count > 0 || typeModel.Fields.Count > 0))
                {
                    int totalTypeReferences = 0;
                    for (int s = 0; s < sources.Length; s++)
                    {
                        totalTypeReferences += CountOccurrences(sources[s].CodeOnlyContent, typeModel.CleanName);
                    }

                    bool isReferencedByName = totalTypeReferences > 1;
                    bool hasActiveMembers = false;

                    if (!isReferencedByName)
                    {
                        foreach (var (mName, _, _) in typeModel.Methods)
                        {
                            string rName = PrettyPrintMemberName(mName);
                            int mCalls = 0;
                            for (int s = 0; s < sources.Length; s++)
                            {
                                mCalls += CountOccurrences(sources[s].CodeOnlyContent, rName);
                                if (mCalls > 1) { hasActiveMembers = true; break; }
                            }
                            if (hasActiveMembers) break;
                        }

                        if (!hasActiveMembers)
                        {
                            foreach (var f in typeModel.Fields)
                            {
                                int fCalls = 0;
                                for (int s = 0; s < sources.Length; s++)
                                {
                                    fCalls += CountOccurrences(sources[s].CodeOnlyContent, f);
                                    if (fCalls > 1) { hasActiveMembers = true; break; }
                                }
                                if (hasActiveMembers) break;
                            }
                        }
                    }

                    if (!isReferencedByName && !hasActiveMembers)
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            typeFile,
                            typeLine,
                            typeLineText,
                            MemberKind.Type,
                            typeModel.CleanName,
                            typeModel.FullName,
                            "Type is never referenced and was trimmed by Native AOT"));
                        return;
                    }
                }
            }

            // --- 2. METHOD & VISIBILITY CHECK ---
            for (int m = 0; m < typeModel.Methods.Count; m++)
            {
                var (methodName, isPublic, isVirtualOrInterface) = typeModel.Methods[m];
                bool methodSurvivedInAot = false;

                for (int s = 0; s < typeSpecificSymbols.Count; s++)
                {
                    if (typeSpecificSymbols[s].Contains(methodName, StringComparison.Ordinal))
                    {
                        methodSurvivedInAot = true;
                        break;
                    }
                }

                string readableName = PrettyPrintMemberName(methodName);
                int totalOccurrences = 0;
                int externalOccurrences = 0;

                for (int s = 0; s < sources.Length; s++)
                {
                    int occ = CountOccurrences(sources[s].CodeOnlyContent, readableName);
                    if (occ > 0)
                    {
                        totalOccurrences += occ;
                        if (!ownFileSet.Contains(sources[s].FilePath))
                            externalOccurrences += occ;
                    }
                }

                if (!methodSurvivedInAot && totalOccurrences <= 1)
                {
                    var (mFile, mLine, mLineText) = LocateMemberAcrossTypeFiles(
                        typeFiles, readableName, sources, fallbackToTypeHeader: true);
                    deadEntries.Add(new DeadCodeEntry(
                        mFile,
                        mLine > 0 ? mLine : typeLine,
                        mLineText,
                        MemberKind.Method,
                        readableName,
                        typeModel.CleanName,
                        "Method has 0 callers in source tree and was trimmed by Native AOT"));
                }
                else if (isPublic && !isVirtualOrInterface && !typeModel.IsStaticClass && !typeModel.IsNestedPrivate &&
                         totalOccurrences > 1 && externalOccurrences == 0 &&
                         !readableName.StartsWith("op_", StringComparison.Ordinal) &&
                         !readableName.StartsWith("operator ", StringComparison.Ordinal))
                {
                    var (mFile, mLine, mLineText) = LocateMemberAcrossTypeFiles(
                        typeFiles, readableName, sources, fallbackToTypeHeader: false);
                    if (mLine > 0 &&
                        mLineText.Contains("public ", StringComparison.Ordinal) &&
                        !mLineText.Contains("override ", StringComparison.Ordinal) &&
                        !mLineText.Contains("virtual ", StringComparison.Ordinal) &&
                        !mLineText.Contains("abstract ", StringComparison.Ordinal))
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            mFile,
                            mLine,
                            mLineText,
                            MemberKind.PrivateCandidate,
                            readableName,
                            typeModel.CleanName,
                            "Public method is only called inside its own declaring type -> can be private"));
                    }
                }
            }

            // --- 3. FIELD AND CONSTANT CHECK ---
            for (int f = 0; f < typeModel.Fields.Count; f++)
            {
                string fieldName = typeModel.Fields[f];
                int totalOccurrences = 0;

                for (int s = 0; s < sources.Length; s++)
                {
                    totalOccurrences += CountOccurrences(sources[s].CodeOnlyContent, fieldName);
                    if (totalOccurrences > 1) break;
                }

                if (totalOccurrences <= 1)
                {
                    var (fFile, fLine, fLineText) = LocateMemberAcrossTypeFiles(
                        typeFiles, fieldName, sources, fallbackToTypeHeader: false);
                    if (fLine > 0)
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            fFile,
                            fLine,
                            fLineText,
                            MemberKind.Field,
                            fieldName,
                            typeModel.CleanName,
                            "Field/constant is never read or mutated"));
                    }
                }
            }

            // --- 4. EVENT CHECK ---
            for (int e = 0; e < typeModel.Events.Count; e++)
            {
                string eventName = typeModel.Events[e];
                int subscribersCount = 0;

                for (int s = 0; s < sources.Length; s++)
                {
                    string content = sources[s].CodeOnlyContent;
                    if (content.Contains($"{eventName} +=", StringComparison.Ordinal) ||
                        content.Contains($"{eventName}+=", StringComparison.Ordinal) ||
                        content.Contains($"{eventName}=", StringComparison.Ordinal))
                    {
                        subscribersCount++;
                        break;
                    }
                }

                if (subscribersCount == 0)
                {
                    var (eFile, eLine, eLineText) = LocateMemberAcrossTypeFiles(
                        typeFiles, eventName, sources, fallbackToTypeHeader: false);
                    if (eLine > 0)
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            eFile,
                            eLine,
                            eLineText,
                            MemberKind.Event,
                            eventName,
                            typeModel.CleanName,
                            "Event has no active subscriptions (+=)"));
                    }
                }
            }
        });

        int deadTypesCount = deadEntries.Count(x => x.Kind is MemberKind.Type or MemberKind.DeadInterface);
        int deadMethodsCount = deadEntries.Count(x => x.Kind == MemberKind.Method);
        int deadFieldsCount = deadEntries.Count(x => x.Kind == MemberKind.Field);
        int deadEventsCount = deadEntries.Count(x => x.Kind == MemberKind.Event);
        int privateCandidatesCount = deadEntries.Count(x => x.Kind == MemberKind.PrivateCandidate);
        int totalHardDefects = deadTypesCount + deadMethodsCount + deadFieldsCount + deadEventsCount;

        var groupedByFile = deadEntries
            .GroupBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var writeStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new StreamWriter(writeStream, Encoding.UTF8);

        writer.WriteLine("# ====================================================================");
        writer.WriteLine("# LMP Native AOT Dead Code, Visibility & Clone Audit");
        writer.WriteLine($"# Assembly:             {Path.GetFileName(assemblyPath)}");
        writer.WriteLine($"# Date:                 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"# Hard Dead Defects:    {totalHardDefects}");
        writer.WriteLine($"# Visibility Hints:     {privateCandidatesCount}");
        writer.WriteLine($"# Duplicate Clusters:   {duplicates.Count}");
        writer.WriteLine("# ====================================================================");
        writer.WriteLine();

        foreach (var fileGroup in groupedByFile)
        {
            string relPath = Path.GetRelativePath(sourceRoot, fileGroup.Key);
            var fileIssues = fileGroup.OrderBy(x => x.LineNumber).ToList();

            writer.WriteLine("======================================================================");
            writer.WriteLine($"File: {relPath} ({fileIssues.Count} findings)");
            writer.WriteLine("======================================================================");

            var typesGroup = fileIssues.Where(x => x.Kind is MemberKind.Type or MemberKind.DeadInterface).ToList();
            var methodsGroup = fileIssues.Where(x => x.Kind == MemberKind.Method).ToList();
            var fieldsGroup = fileIssues.Where(x => x.Kind == MemberKind.Field).ToList();
            var eventsGroup = fileIssues.Where(x => x.Kind == MemberKind.Event).ToList();
            var visGroup = fileIssues.Where(x => x.Kind == MemberKind.PrivateCandidate).ToList();

            if (typesGroup.Count > 0)
            {
                writer.WriteLine("  [DEAD TYPES & INTERFACES]");
                foreach (var entry in typesGroup)
                {
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD TYPE] {entry.ParentTypeName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (methodsGroup.Count > 0)
            {
                writer.WriteLine("  [DEAD METHODS]");
                foreach (var entry in methodsGroup)
                {
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD METHOD] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (fieldsGroup.Count > 0)
            {
                writer.WriteLine("  [DEAD FIELDS & CONSTANTS]");
                foreach (var entry in fieldsGroup)
                {
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD FIELD] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (eventsGroup.Count > 0)
            {
                writer.WriteLine("  [DEAD EVENTS]");
                foreach (var entry in eventsGroup)
                {
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD EVENT] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (visGroup.Count > 0)
            {
                writer.WriteLine("  [OVER-EXPOSED PUBLIC API -> CAN BE PRIVATE]");
                foreach (var entry in visGroup)
                {
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [PRIVATE CANDIDATE] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }
        }

        if (duplicates.Count > 0)
        {
            writer.WriteLine("======================================================================");
            writer.WriteLine($"STRUCTURAL CODE CLONES ({duplicates.Count} groups of {duplicates[0].StatementCount}+ identical statements)");
            writer.WriteLine("======================================================================");
            for (int i = 0; i < duplicates.Count; i++)
            {
                var cluster = duplicates[i];
                writer.WriteLine($"  [CLONE #{i + 1}] ({cluster.Locations.Count} occurrences) Starts with: {cluster.SampleFirstLine}");
                foreach (var (rel, start, end) in cluster.Locations)
                {
                    writer.WriteLine($"    - {rel}:{start}-{end}");
                }
                writer.WriteLine();
            }
        }

        return (groupedByFile.Count, deadTypesCount, deadMethodsCount, deadFieldsCount, deadEventsCount, privateCandidatesCount, totalHardDefects);
    }

    private static string ResolveEntitySimpleName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil) return string.Empty;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var td = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            string n = reader.GetString(td.Name);
            int idx = n.IndexOf('`');
            return idx > 0 ? n[..idx] : n;
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
            string n = reader.GetString(tr.Name);
            int idx = n.IndexOf('`');
            return idx > 0 ? n[..idx] : n;
        }
        return string.Empty;
    }

    private sealed record TypeInspectionModel(
        string FullName,
        string CleanName,
        bool IsNativeInteropStruct,
        bool IsStaticClass,
        bool IsNestedPrivate,
        List<(string Name, bool IsPublic, bool IsVirtualOrInterface)> Methods,
        List<string> Fields,
        List<string> Events);
}