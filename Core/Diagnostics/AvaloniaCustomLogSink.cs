using System.Text;
using Avalonia.Logging;

namespace LMP.Core.Diagnostics;

public class AvaloniaCustomLogSink : ILogSink
{
    private readonly LogEventLevel _minLevel;

    public AvaloniaCustomLogSink(LogEventLevel minLevel)
    {
        _minLevel = minLevel;
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= _minLevel;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        LogInternal(level, area, messageTemplate, []);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        LogInternal(level, area, messageTemplate, propertyValues);
    }

    private static void LogInternal(LogEventLevel level, string area, string template, object?[] args)
    {
        string formatted = FormatStructuredTemplate(template, args);
        var fullMessage = $"[{area}] {formatted}";

        switch (level)
        {
            case LogEventLevel.Verbose:
            case LogEventLevel.Debug:
                LMP.Core.Logger.Log.Debug(fullMessage);
                break;
            case LogEventLevel.Information:
                LMP.Core.Logger.Log.Info(fullMessage);
                break;
            case LogEventLevel.Warning:
                LMP.Core.Logger.Log.Warn(fullMessage);
                break;
            case LogEventLevel.Error:
            case LogEventLevel.Fatal:
                LMP.Core.Logger.Log.Error(fullMessage);
                break;
        }
    }

    /// <summary>
    /// Parses named templates ({Property}) and converts them into positional ones ({0})
    /// to prevent System.FormatException in string.Format.
    /// </summary>
    private static string FormatStructuredTemplate(string template, object?[] args)
    {
        if (args == null || args.Length == 0)
            return template;

        var sb = new StringBuilder(template.Length + 16);
        int argIndex = 0;
        int i = 0;

        while (i < template.Length)
        {
            // Look for the placeholder start '{', ignoring escaped '{{'
            if (template[i] == '{' && i + 1 < template.Length && template[i + 1] != '{')
            {
                int closeIndex = template.IndexOf('}', i);
                if (closeIndex != -1)
                {
                    string inside = template.Substring(i + 1, closeIndex - i - 1);
                    int colonIndex = inside.IndexOf(':');
                    string formatModifier = "";
                    if (colonIndex != -1)
                    {
                        formatModifier = inside.Substring(colonIndex);
                        inside = inside.Substring(0, colonIndex);
                    }

                    // If it is already a number inside (positional format), leave it as is
                    if (int.TryParse(inside, out _))
                    {
                        sb.Append('{').Append(inside).Append(formatModifier).Append('}');
                    }
                    else
                    {
                        // If it is text, replace it with the zero-based index of the argument
                        sb.Append('{').Append(argIndex++).Append(formatModifier).Append('}');
                    }
                    i = closeIndex + 1;
                    continue;
                }
            }
            sb.Append(template[i]);
            i++;
        }

        try
        {
            return string.Format(sb.ToString(), args);
        }
        catch
        {
            return template;
        }
    }
}