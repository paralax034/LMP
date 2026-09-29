using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml;

namespace LMP.Tools.AotScanner;

/// <summary>
/// Двухфакторный анализатор мертвого кода Native AOT + Source Tree с группировкой по файлам,
/// защитой от ложных срабатываний (линковка расширений, инлайнинг констант, P/Invoke структуры) и категоризацией дефектов.
/// </summary>
public static class Program
{
    private enum MemberKind
    {
        Type,
        Method,
        Field,
        Event
    }

    private readonly record struct SourceFile(string FilePath, string RelativePath, string Content, string[] Lines);

    private readonly record struct DeadCodeEntry(
        string FilePath,
        int LineNumber,
        string LineText,
        MemberKind Kind,
        string MemberName,
        string ParentTypeName,
        string Description);

    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Использование: AotScanner <путь_к_dll> <codegen.dgml> <папка_с_исходниками> [отчёт=DeadCode.txt]");
            Console.WriteLine(@"Пример: AotScanner ""Core\bin\Release\net11.0\LMP.Core.dll"" ""obj\Release\net11.0\win-x64\native\LMP.codegen.dgml.xml"" ""D:\Projects\CS\LMP""");
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
            Console.WriteLine("[ОШИБКА] Один из указанных путей не существует.");
            Console.ResetColor();
            return 1;
        }

        string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        string rootPrefix = assemblyName.Contains('.') ? assemblyName[..assemblyName.IndexOf('.')] : assemblyName;

        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine(" LMP Native AOT Structured Dead Code Scanner (v2.1)");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine($"Сборка:      {assemblyPath}");
        Console.WriteLine($"Граф AOT:    {codegenPath}");
        Console.WriteLine($"Исходники:   {sourceDir}");
        Console.WriteLine($"Отчёт:       {outputPath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        Console.Write("[1/3] Кэширование файлов C# и разметки AXAML... ");
        var sourceFiles = LoadSourceFiles(sourceDir);
        Console.WriteLine($"Готово ({sourceFiles.Length:N0} файлов)");

        Console.Write("[2/3] Индексация символов codegen AOT... ");
        var survivedSymbols = LoadCodegenSymbolsFiltered(codegenPath, rootPrefix);
        Console.WriteLine($"Готово ({survivedSymbols.Length:N0} символов)");

        Console.Write("[3/3] Глубокий аудит типов, методов, полей и событий... ");
        var stats = AnalyzeAndWriteReport(assemblyPath, survivedSymbols, sourceFiles, sourceDir, outputPath);
        Console.WriteLine("Готово");

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[УСПЕХ] Анализ завершён за {sw.ElapsedMilliseconds} мс");
        Console.ResetColor();
        Console.WriteLine($"  Файлов с мёртвым кодом:  {stats.FilesCount:N0}");
        Console.WriteLine($"  Мёртвых типов (Types):   {stats.DeadTypes:N0}");
        Console.WriteLine($"  Мёртвых методов:         {stats.DeadMethods:N0}");
        Console.WriteLine($"  Мёртвых полей / констант:{stats.DeadFields:N0}");
        Console.WriteLine($"  Мёртвых событий (Events):{stats.DeadEvents:N0}");
        Console.WriteLine($"  Всего подтверждено:      {stats.TotalIssues:N0}");
        Console.WriteLine($"  Отчёт сохранён в:        {Path.GetFullPath(outputPath)}");
        Console.WriteLine("──────────────────────────────────────────────────────────────");

        return stats.TotalIssues;
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
                file.Contains(@"\External\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                string[] lines = text.Split('\n');
                string relPath = Path.GetRelativePath(sourceDir, file);
                list.Add(new SourceFile(file, relPath, text, lines));
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

    private static (string FilePath, int LineNumber, string LineText) LocateTypeDeclaration(string cleanTypeName, SourceFile[] sources)
    {
        string[] patterns = [$"class {cleanTypeName}", $"struct {cleanTypeName}", $"record {cleanTypeName}", $"enum {cleanTypeName}", $"interface {cleanTypeName}"];

        for (int s = 0; s < sources.Length; s++)
        {
            var src = sources[s];
            if (!src.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !src.Content.Contains(cleanTypeName, StringComparison.Ordinal))
                continue;

            for (int l = 0; l < src.Lines.Length; l++)
            {
                string line = src.Lines[l];
                for (int p = 0; p < patterns.Length; p++)
                {
                    if (line.Contains(patterns[p], StringComparison.Ordinal))
                        return (src.FilePath, l + 1, line.Trim());
                }
            }
        }

        return (string.Empty, 0, string.Empty);
    }

    private static (int LineNumber, string LineText) LocateMemberDeclaration(string filePath, string memberName, SourceFile[] sources)
    {
        if (string.IsNullOrEmpty(filePath)) return (0, string.Empty);

        for (int s = 0; s < sources.Length; s++)
        {
            if (!sources[s].FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = sources[s].Lines;
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                if (line.Contains(memberName, StringComparison.Ordinal) &&
                    (line.Contains("public", StringComparison.Ordinal) ||
                     line.Contains("private", StringComparison.Ordinal) ||
                     line.Contains("internal", StringComparison.Ordinal) ||
                     line.Contains("protected", StringComparison.Ordinal) ||
                     line.Contains("static", StringComparison.Ordinal) ||
                     line.Contains("event", StringComparison.Ordinal) ||
                     line.Contains("const", StringComparison.Ordinal) ||
                     line.Contains("readonly", StringComparison.Ordinal)))
                {
                    return (l + 1, line.Trim());
                }
            }
            break;
        }

        return (0, string.Empty);
    }

    private static bool IsCompilerOrRuntimeSynthetic(string name) =>
        name.StartsWith("<", StringComparison.Ordinal) ||
        name.StartsWith("__set_", StringComparison.Ordinal) ||
        name.StartsWith("__get_", StringComparison.Ordinal) ||
        name.StartsWith("__Init_", StringComparison.Ordinal) ||
        name.Equals(".cctor", StringComparison.Ordinal) ||
        name.Equals(".ctor", StringComparison.Ordinal) ||
        name.Equals("Invoke", StringComparison.Ordinal) ||
        name.Equals("BeginInvoke", StringComparison.Ordinal) ||
        name.Equals("EndInvoke", StringComparison.Ordinal) ||
        name.Equals("Deconstruct", StringComparison.Ordinal) ||
        name.Equals("PrintMembers", StringComparison.Ordinal) ||
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

    private static (int FilesCount, int DeadTypes, int DeadMethods, int DeadFields, int DeadEvents, int TotalIssues)
        AnalyzeAndWriteReport(
            string assemblyPath,
            string[] survivedSymbols,
            SourceFile[] sources,
            string sourceRoot,
            string outputPath)
    {
        using var fileStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fileStream);
        var metadataReader = peReader.GetMetadataReader();

        var typeEntries = new List<TypeInspectionModel>();

        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeHandle);
            string rawTypeName = metadataReader.GetString(typeDef.Name);

            if (typeDef.Attributes.HasFlag(TypeAttributes.Interface) ||
                string.IsNullOrEmpty(rawTypeName) ||
                rawTypeName.StartsWith("<", StringComparison.Ordinal) ||
                rawTypeName.Equals("<Module>", StringComparison.Ordinal) ||
                rawTypeName.Contains("ImplementationDetails", StringComparison.Ordinal) ||
                rawTypeName.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal) ||
                rawTypeName.Contains("InterpolatedStringHandler", StringComparison.Ordinal))
            {
                continue;
            }

            bool isExplicitOrSequential = typeDef.Attributes.HasFlag(TypeAttributes.SequentialLayout) ||
                                          typeDef.Attributes.HasFlag(TypeAttributes.ExplicitLayout);

            bool isStaticClass = typeDef.Attributes.HasFlag(TypeAttributes.Abstract) &&
                                 typeDef.Attributes.HasFlag(TypeAttributes.Sealed);

            int backtickIndex = rawTypeName.IndexOf('`');
            string cleanTypeName = backtickIndex > 0 ? rawTypeName[..backtickIndex] : rawTypeName;

            string fullTypeName;
            if (typeDef.IsNested)
            {
                var declaringTypeDef = metadataReader.GetTypeDefinition(typeDef.GetDeclaringType());
                string declaringName = metadataReader.GetString(declaringTypeDef.Name);
                string declaringNamespace = metadataReader.GetString(declaringTypeDef.Namespace);
                fullTypeName = string.IsNullOrEmpty(declaringNamespace)
                    ? $"{declaringName}.{cleanTypeName}"
                    : $"{declaringNamespace}.{declaringName}.{cleanTypeName}";
            }
            else
            {
                string typeNamespace = metadataReader.GetString(typeDef.Namespace);
                fullTypeName = string.IsNullOrEmpty(typeNamespace)
                    ? cleanTypeName
                    : $"{typeNamespace}.{cleanTypeName}";
            }

            var methods = new List<string>();
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

                methods.Add(mName);
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
                fullTypeName, cleanTypeName, isExplicitOrSequential, isStaticClass, methods, fields, events));
        }

        var deadEntries = new ConcurrentBag<DeadCodeEntry>();

        Parallel.ForEach(typeEntries, typeModel =>
        {
            var (typeFile, typeLine, typeLineText) = LocateTypeDeclaration(typeModel.CleanName, sources);
            if (string.IsNullOrEmpty(typeFile)) return;

            var typeSpecificSymbols = new List<string>(32);
            for (int i = 0; i < survivedSymbols.Length; i++)
            {
                if (survivedSymbols[i].Contains(typeModel.CleanName, StringComparison.Ordinal))
                    typeSpecificSymbols.Add(survivedSymbols[i]);
            }

            // --- 1. ПРОВЕРКА ТИПА (Dead Class / Struct / Record / Enum) ---
            bool isTypeDead = false;

            if (!typeModel.IsNativeInteropStruct)
            {
                if (typeSpecificSymbols.Count == 0 && (typeModel.Methods.Count > 0 || typeModel.Fields.Count > 0))
                {
                    // Подсчитываем прямые упоминания имени типа во всех исходниках
                    int totalTypeReferences = 0;
                    for (int s = 0; s < sources.Length; s++)
                    {
                        totalTypeReferences += CountOccurrences(sources[s].Content, typeModel.CleanName);
                    }

                    // Если имя типа встречается более 1 раза (объявление + обращения вроде Defaults.X или new Type()), тип активен
                    bool isReferencedByName = totalTypeReferences > 1;

                    bool hasActiveMembers = false;

                    // Если имя типа не упоминается напрямую (характерно для extension-классов вроде SearchFilterExtensions),
                    // проверяем активность методов расширения и констант/полей
                    if (!isReferencedByName)
                    {
                        foreach (var m in typeModel.Methods)
                        {
                            string rName = PrettyPrintMemberName(m);
                            int mCalls = 0;
                            for (int s = 0; s < sources.Length; s++)
                            {
                                mCalls += CountOccurrences(sources[s].Content, rName);
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
                                    fCalls += CountOccurrences(sources[s].Content, f);
                                    if (fCalls > 1) { hasActiveMembers = true; break; }
                                }
                                if (hasActiveMembers) break;
                            }
                        }
                    }

                    if (!isReferencedByName && !hasActiveMembers)
                    {
                        isTypeDead = true;
                        deadEntries.Add(new DeadCodeEntry(
                            typeFile,
                            typeLine,
                            typeLineText,
                            MemberKind.Type,
                            typeModel.CleanName,
                            typeModel.FullName,
                            "Тип не используется ни внутри своего файла, ни в других частях проекта"));
                        return;
                    }
                }
            }

            if (isTypeDead) return;

            // --- 2. ПРОВЕРКА МЕТОДОВ ---
            for (int m = 0; m < typeModel.Methods.Count; m++)
            {
                string methodName = typeModel.Methods[m];
                bool methodSurvivedInAot = false;

                for (int s = 0; s < typeSpecificSymbols.Count; s++)
                {
                    if (typeSpecificSymbols[s].Contains(methodName, StringComparison.Ordinal))
                    {
                        methodSurvivedInAot = true;
                        break;
                    }
                }

                if (!methodSurvivedInAot)
                {
                    string readableName = PrettyPrintMemberName(methodName);
                    int totalOccurrences = 0;

                    for (int s = 0; s < sources.Length; s++)
                    {
                        totalOccurrences += CountOccurrences(sources[s].Content, readableName);
                        if (totalOccurrences > 1) break;
                    }

                    if (totalOccurrences <= 1)
                    {
                        var (mLine, mLineText) = LocateMemberDeclaration(typeFile, readableName, sources);
                        deadEntries.Add(new DeadCodeEntry(
                            typeFile,
                            mLine > 0 ? mLine : typeLine,
                            mLineText,
                            MemberKind.Method,
                            readableName,
                            typeModel.CleanName,
                            "Метод не вызывается в проекте и вырезан компилятором"));
                    }
                }
            }

            // --- 3. ПРОВЕРКА ПОЛЕЙ И КОНСТАНТ ---
            for (int f = 0; f < typeModel.Fields.Count; f++)
            {
                string fieldName = typeModel.Fields[f];
                int totalOccurrences = 0;

                for (int s = 0; s < sources.Length; s++)
                {
                    totalOccurrences += CountOccurrences(sources[s].Content, fieldName);
                    if (totalOccurrences > 1) break;
                }

                if (totalOccurrences <= 1)
                {
                    var (fLine, fLineText) = LocateMemberDeclaration(typeFile, fieldName, sources);
                    if (fLine > 0)
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            typeFile,
                            fLine,
                            fLineText,
                            MemberKind.Field,
                            fieldName,
                            typeModel.CleanName,
                            "Поле/константа нигде не читается и не изменяется"));
                    }
                }
            }

            // --- 4. ПРОВЕРКА СОБЫТИЙ ---
            for (int e = 0; e < typeModel.Events.Count; e++)
            {
                string eventName = typeModel.Events[e];
                int subscribersCount = 0;

                for (int s = 0; s < sources.Length; s++)
                {
                    string content = sources[s].Content;
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
                    var (eLine, eLineText) = LocateMemberDeclaration(typeFile, eventName, sources);
                    if (eLine > 0)
                    {
                        deadEntries.Add(new DeadCodeEntry(
                            typeFile,
                            eLine,
                            eLineText,
                            MemberKind.Event,
                            eventName,
                            typeModel.CleanName,
                            "Событие объявлено, но не имеет активных подписок (+=)"));
                    }
                }
            }
        });

        var groupedByFile = deadEntries
            .GroupBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var writeStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new StreamWriter(writeStream, Encoding.UTF8);

        writer.WriteLine("# ====================================================================");
        writer.WriteLine($"# LMP Native AOT Structured Dead Code Audit");
        writer.WriteLine($"# Сборка:    {Path.GetFileName(assemblyPath)}");
        writer.WriteLine($"# Дата:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"# Всего подтверждено дефектов: {deadEntries.Count}");
        writer.WriteLine("# ====================================================================");
        writer.WriteLine();

        int deadTypesCount = 0;
        int deadMethodsCount = 0;
        int deadFieldsCount = 0;
        int deadEventsCount = 0;

        foreach (var fileGroup in groupedByFile)
        {
            string relPath = Path.GetRelativePath(sourceRoot, fileGroup.Key);
            var fileIssues = fileGroup.OrderBy(x => x.LineNumber).ToList();

            writer.WriteLine($"======================================================================");
            writer.WriteLine($"📁 Файл: {relPath} ({fileIssues.Count} дефектов)");
            writer.WriteLine($"======================================================================");

            var typesGroup = fileIssues.Where(x => x.Kind == MemberKind.Type).ToList();
            var methodsGroup = fileIssues.Where(x => x.Kind == MemberKind.Method).ToList();
            var fieldsGroup = fileIssues.Where(x => x.Kind == MemberKind.Field).ToList();
            var eventsGroup = fileIssues.Where(x => x.Kind == MemberKind.Event).ToList();

            if (typesGroup.Count > 0)
            {
                writer.WriteLine("  [ТИПЫ / КЛАССЫ]");
                foreach (var entry in typesGroup)
                {
                    deadTypesCount++;
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD TYPE] {entry.ParentTypeName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (methodsGroup.Count > 0)
            {
                writer.WriteLine("  [МЕТОДЫ]");
                foreach (var entry in methodsGroup)
                {
                    deadMethodsCount++;
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD METHOD] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (fieldsGroup.Count > 0)
            {
                writer.WriteLine("  [ПОЛЯ И КОНСТАНТЫ]");
                foreach (var entry in fieldsGroup)
                {
                    deadFieldsCount++;
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD FIELD] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }

            if (eventsGroup.Count > 0)
            {
                writer.WriteLine("  [СОБЫТИЯ]");
                foreach (var entry in eventsGroup)
                {
                    deadEventsCount++;
                    writer.WriteLine($"    {entry.FilePath}({entry.LineNumber}): [DEAD EVENT] {entry.ParentTypeName}.{entry.MemberName}");
                    if (!string.IsNullOrWhiteSpace(entry.LineText))
                        writer.WriteLine($"      {entry.LineText}");
                }
                writer.WriteLine();
            }
        }

        return (groupedByFile.Count, deadTypesCount, deadMethodsCount, deadFieldsCount, deadEventsCount, deadEntries.Count);
    }

    private sealed record TypeInspectionModel(
        string FullName,
        string CleanName,
        bool IsNativeInteropStruct,
        bool IsStaticClass,
        List<string> Methods,
        List<string> Fields,
        List<string> Events);
}