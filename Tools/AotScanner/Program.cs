using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;

namespace LMP.Tools.AotScanner;

/// <summary>
/// Двухфакторный анализатор мертвого кода с генерацией кликабельных ссылок на исходный код (файл:строка).
/// </summary>
/// <remarks>
/// Автоматически транслирует внутренние CLI-имена операторов (op_*) в C#-синтаксис и находит строки объявления.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Описание загруженного файла исходного кода.
    /// </summary>
    /// <param name="FilePath">Абсолютный путь к файлу.</param>
    /// <param name="Content">Текстовое содержимое исходного файла.</param>
    /// <param name="Lines">Массив строк файла для быстрого поиска номеров строк.</param>
    private readonly record struct SourceFile(string FilePath, string Content, string[] Lines);

    /// <summary>
    /// Главная точка входа в анализатор.
    /// </summary>
    /// <param name="args">Аргументы: [0] dll, [1] codegen.dgml, [2] sourceDir, [3] output (опционально).</param>
    /// <returns>Код возврата процесса (0 — успешно, 1 — ошибка).</returns>
    /// <remarks>
    /// Генерирует отчет стандарта MSBuild: File.cs(Line): Message.
    /// </remarks>
    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Использование: AotScanner <путь_к_dll> <codegen.dgml> <папка_с_исходниками> [отчёт=DeadCode_Clickable.txt]");
            Console.WriteLine(@"Пример: AotScanner ""Core\bin\Release\net11.0\LMP.Core.dll"" ""obj\Release\net11.0\win-x64\native\LMP.codegen.dgml.xml"" ""D:\Projects\CS\LMP""");
            Console.ResetColor();
            return 1;
        }

        string assemblyPath = args[0];
        string codegenPath = args[1];
        string sourceDir = args[2];
        string outputPath = args.Length > 3 ? args[3] : "DeadCode_Clickable.txt";

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
        Console.WriteLine(" Native AOT Clickable Source Code Dead Code Locator");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        Console.WriteLine($"Сборка:      {assemblyPath}");
        Console.WriteLine($"Граф AOT:    {codegenPath}");
        Console.WriteLine($"Исходники:   {sourceDir}");
        Console.WriteLine($"Отчёт:       {outputPath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        Console.Write("[1/3] Кэширование файлов C# решения... ");
        var sourceFiles = LoadSourceFiles(sourceDir);
        Console.WriteLine($"Готово ({sourceFiles.Length:N0} файлов)");

        Console.Write("[2/3] Индексация символов codegen AOT... ");
        var survivedSymbols = LoadCodegenSymbolsFiltered(codegenPath, rootPrefix);
        Console.WriteLine($"Готово ({survivedSymbols.Length:N0} символов)");

        Console.Write("[3/3] Поиск строк объявлений и валидация call-sites... ");
        var result = VerifyAndLocateDeadCode(assemblyPath, survivedSymbols, sourceFiles, outputPath);
        Console.WriteLine("Готово");

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[УСПЕХ] Анализ завершён за {sw.ElapsedMilliseconds} мс");
        Console.ResetColor();
        Console.WriteLine($"Неиспользуемых методов без вызовов (0 calls): {result.ZeroCalls:N0}");
        Console.WriteLine($"Неиспользуемых классов / изолированных фич:  {result.Islands:N0}");
        Console.WriteLine($"Спасённых заинлайненных методов (AOT):       {result.Rescued:N0}");
        Console.WriteLine($"Кликабельный отчёт:                          {Path.GetFullPath(outputPath)}");
        Console.WriteLine("──────────────────────────────────────────────────────────────");

        return 0;
    }

    /// <summary>
    /// Преобразует внутреннее CLI-имя оператора в синтаксис языка C#.
    /// </summary>
    /// <param name="methodName">Имя метода из метаданных (например, op_GreaterThan).</param>
    /// <returns>Читаемое имя на языке C# (например, operator >).</returns>
    /// <remarks>
    /// Оптимизировано через сопоставление со строковыми литералами без лишних аллокаций.
    /// </remarks>
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

    /// <summary>
    /// Считывает исходные файлы проекта с разбиением на строки.
    /// </summary>
    /// <param name="sourceDir">Корневая папка исходников.</param>
    /// <returns>Массив описателей исходных файлов.</returns>
    /// <remarks>
    /// Исключает папки артефактов компиляции (bin, obj, .vs, .git).
    /// </remarks>
    private static SourceFile[] LoadSourceFiles(string sourceDir)
    {
        var list = new List<SourceFile>(512);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\.vs\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(file);
                string[] lines = text.Split('\n');
                list.Add(new SourceFile(file, text, lines));
            }
            catch { }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Загружает узлы графа codegen с фильтрацией по префиксу.
    /// </summary>
    /// <param name="codegenPath">Путь к файлу codegen.dgml.xml.</param>
    /// <param name="scopePrefix">Префикс сборки проекта.</param>
    /// <returns>Массив скомпилированных машинных меток.</returns>
    /// <remarks>
    /// Потоковое чтение буфером 64 КБ без аллокаций DOM.
    /// </remarks>
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
            {
                continue;
            }

            string? label = reader.GetAttribute("Label") ?? reader.GetAttribute("Id");
            if (label is not null && label.Contains(scopePrefix, StringComparison.OrdinalIgnoreCase))
            {
                symbols.Add(label);
            }
        }

        return symbols.ToArray();
    }

    /// <summary>
    /// Находит файл и номер строки, где объявлен целевой тип.
    /// </summary>
    /// <param name="cleanTypeName">Имя типа без дженерик-суффикса.</param>
    /// <param name="sources">Кэш исходников проекта.</param>
    /// <returns>Путь к файлу, номер строки и строка исходника или пустой кортеж при неудаче.</returns>
    /// <remarks>
    /// Ищет ключевые слова class, struct, record, enum.
    /// </remarks>
    private static (string FilePath, int LineNumber, string LineText) LocateTypeDeclaration(string cleanTypeName, SourceFile[] sources)
    {
        string[] patterns = [$"class {cleanTypeName}", $"struct {cleanTypeName}", $"record {cleanTypeName}", $"enum {cleanTypeName}"];

        for (int s = 0; s < sources.Length; s++)
        {
            var src = sources[s];
            if (!src.Content.Contains(cleanTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            for (int l = 0; l < src.Lines.Length; l++)
            {
                string line = src.Lines[l];
                for (int p = 0; p < patterns.Length; p++)
                {
                    if (line.Contains(patterns[p], StringComparison.Ordinal))
                    {
                        return (src.FilePath, l + 1, line.Trim());
                    }
                }
            }
        }

        return (string.Empty, 0, string.Empty);
    }

    /// <summary>
    /// Находит номер строки объявления метода внутри файла родительского класса.
    /// </summary>
    /// <param name="filePath">Путь к файлу класса.</param>
    /// <param name="methodName">Имя метода или CLI-оператора.</param>
    /// <param name="sources">Кэш исходников.</param>
    /// <returns>Номер строки и текст строки в файле.</returns>
    /// <remarks>
    /// Корректно определяет позиции перегруженных операторов и обычных методов.
    /// </remarks>
    private static (int LineNumber, string LineText) LocateMethodDeclaration(string filePath, string methodName, SourceFile[] sources)
    {
        string readableName = PrettyPrintMemberName(methodName);

        for (int s = 0; s < sources.Length; s++)
        {
            if (!sources[s].FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = sources[s].Lines;
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                if (line.Contains(readableName, StringComparison.Ordinal) &&
                    (line.Contains("public", StringComparison.Ordinal) ||
                     line.Contains("private", StringComparison.Ordinal) ||
                     line.Contains("internal", StringComparison.Ordinal) ||
                     line.Contains("protected", StringComparison.Ordinal) ||
                     line.Contains("static", StringComparison.Ordinal)))
                {
                    return (l + 1, line.Trim());
                }
            }

            break;
        }

        return (0, string.Empty);
    }

    /// <summary>
    /// Выполняет верификацию и привязку координат исходного кода для найденного мертвого кода.
    /// </summary>
    /// <param name="assemblyPath">Путь к исследуемой сборке.</param>
    /// <param name="survivedSymbols">Символы AOT codegen.</param>
    /// <param name="sources">Кэш файлов решения.</param>
    /// <param name="outputPath">Путь к файлу отчета.</param>
    /// <returns>Кортеж статистики найденных элементов.</returns>
    /// <remarks>
    /// Формирует ссылки вида Path(Line): Message для мгновенного перехода в редакторе.
    /// </remarks>
    private static (int ZeroCalls, int Islands, int Rescued) VerifyAndLocateDeadCode(
        string assemblyPath,
        string[] survivedSymbols,
        SourceFile[] sources,
        string outputPath)
    {
        using var fileStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(fileStream);
        var metadataReader = peReader.GetMetadataReader();

        var candidates = new List<(string FullName, string CleanName, bool HasMethods, List<string> Methods)>();

        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeHandle);
            string rawTypeName = metadataReader.GetString(typeDef.Name);
            string typeNamespace = metadataReader.GetString(typeDef.Namespace);

            if (typeDef.Attributes.HasFlag(TypeAttributes.Interface) ||
                string.IsNullOrEmpty(rawTypeName) ||
                rawTypeName.StartsWith("<", StringComparison.Ordinal) ||
                rawTypeName.Equals("<Module>", StringComparison.Ordinal) ||
                rawTypeName.Contains("ImplementationDetails", StringComparison.Ordinal) ||
                rawTypeName.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal))
            {
                continue;
            }

            int backtickIndex = rawTypeName.IndexOf('`');
            string cleanTypeName = backtickIndex > 0 ? rawTypeName[..backtickIndex] : rawTypeName;
            string fullTypeName = string.IsNullOrEmpty(typeNamespace) ? cleanTypeName : $"{typeNamespace}.{cleanTypeName}";

            var methodNames = new List<string>();
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var methodDef = metadataReader.GetMethodDefinition(methodHandle);
                string methodName = metadataReader.GetString(methodDef.Name);

                if (methodName.StartsWith("<", StringComparison.Ordinal) ||
                    methodName.Equals(".cctor", StringComparison.Ordinal) ||
                    methodName.Equals(".ctor", StringComparison.Ordinal) ||
                    methodName.StartsWith("get_", StringComparison.Ordinal) ||
                    methodName.StartsWith("set_", StringComparison.Ordinal) ||
                    methodName.StartsWith("add_", StringComparison.Ordinal) ||
                    methodName.StartsWith("remove_", StringComparison.Ordinal) ||
                    methodName.Equals("Deconstruct", StringComparison.Ordinal) ||
                    methodName.Equals("PrintMembers", StringComparison.Ordinal) ||
                    methodName.Contains("MemoryPack", StringComparison.Ordinal))
                {
                    continue;
                }

                methodNames.Add(methodName);
            }

            candidates.Add((fullTypeName, cleanTypeName, typeDef.GetMethods().Count > 0, methodNames));
        }

        var zeroCallsList = new ConcurrentBag<string>();
        var islandsList = new ConcurrentBag<string>();
        int rescuedCount = 0;

        Parallel.ForEach(candidates, candidate =>
        {
            var typeSpecificSymbols = new List<string>(32);
            for (int i = 0; i < survivedSymbols.Length; i++)
            {
                if (survivedSymbols[i].Contains(candidate.CleanName, StringComparison.Ordinal))
                {
                    typeSpecificSymbols.Add(survivedSymbols[i]);
                }
            }

            var (typeFile, typeLine, typeLineText) = LocateTypeDeclaration(candidate.CleanName, sources);

            if (typeSpecificSymbols.Count == 0)
            {
                if (!candidate.HasMethods)
                {
                    return;
                }

                int filesReferencing = 0;
                for (int s = 0; s < sources.Length; s++)
                {
                    if (sources[s].Content.Contains(candidate.CleanName, StringComparison.Ordinal))
                    {
                        filesReferencing++;
                    }
                }

                if (filesReferencing <= 1)
                {
                    string loc = !string.IsNullOrEmpty(typeFile) ? $"{typeFile}({typeLine})" : candidate.FullName;
                    islandsList.Add($"{loc}: [DEAD CLASS - UNREFERENCED] {candidate.FullName}\n       {typeLineText}");
                }
                else
                {
                    Interlocked.Increment(ref rescuedCount);
                }
                return;
            }

            for (int m = 0; m < candidate.Methods.Count; m++)
            {
                string methodName = candidate.Methods[m];
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
                    int filesWithOccurrences = 0;

                    for (int s = 0; s < sources.Length; s++)
                    {
                        string content = sources[s].Content;
                        int idx = content.IndexOf(readableName, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            filesWithOccurrences++;
                            while (idx >= 0)
                            {
                                totalOccurrences++;
                                idx = content.IndexOf(readableName, idx + readableName.Length, StringComparison.Ordinal);
                            }
                        }
                    }

                    var (methodLine, methodLineText) = LocateMethodDeclaration(typeFile, methodName, sources);
                    string loc = !string.IsNullOrEmpty(typeFile) ? $"{typeFile}({methodLine})" : $"{candidate.FullName}.{readableName}";

                    if (totalOccurrences <= 1)
                    {
                        zeroCallsList.Add($"{loc}: [DEAD METHOD - 0 CALLS] {candidate.FullName}.{readableName}\n       {methodLineText}");
                    }
                    else if (filesWithOccurrences == 1)
                    {
                        islandsList.Add($"{loc}: [DEAD METHOD - UNREACHABLE ISLAND] {candidate.FullName}.{readableName}\n       {methodLineText}");
                    }
                    else
                    {
                        Interlocked.Increment(ref rescuedCount);
                    }
                }
            }
        });

        using var writeStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new StreamWriter(writeStream, System.Text.Encoding.UTF8);

        writer.WriteLine($"# LMP Native AOT Clickable Source Report");
        writer.WriteLine($"# Assembly: {Path.GetFileName(assemblyPath)}");
        writer.WriteLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine("# ====================================================================");
        writer.WriteLine("# 1. ZERO CALL SITES (100% Dead - Click path to jump to code line)");
        writer.WriteLine("# ====================================================================");

        foreach (var item in zeroCallsList.OrderBy(x => x, StringComparer.Ordinal))
        {
            writer.WriteLine(item);
        }

        writer.WriteLine();
        writer.WriteLine("# ====================================================================");
        writer.WriteLine("# 2. UNREACHABLE ISLANDS (Features only called inside dead subsystems)");
        writer.WriteLine("# ====================================================================");

        foreach (var item in islandsList.OrderBy(x => x, StringComparer.Ordinal))
        {
            writer.WriteLine(item);
        }

        return (zeroCallsList.Count, islandsList.Count, rescuedCount);
    }
}