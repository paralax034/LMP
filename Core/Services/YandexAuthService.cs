using LMP.Core.Helpers;

namespace LMP.Core.Services;

/// <summary>
/// Управление персистентным хранением OAuth-токена Яндекс Музыки через AtomicFile.
/// </summary>
public sealed class YandexAuthService
{
    private static string TokenFilePath =>
        Path.Combine(Path.GetDirectoryName(G.FilePath.Database) ?? AppContext.BaseDirectory, "yandex_token.bin");

    /// <summary>
    /// Читает сохранённый токен с диска с автоматическим восстановлением из бэкапа при сбое.
    /// </summary>
    public string? GetSavedToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                var token = AtomicFile.ReadTextWithFallback(TokenFilePath, out _)?.Trim();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexAuth] Не удалось прочитать токен: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Атомарно сохраняет токен на диск с созданием резервной копии (.bak).
    /// </summary>
    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            AtomicFile.WriteText(TokenFilePath, token.Trim(), createBackup: true);
            Log.Info("[YandexAuth] Токен успешно сохранён через AtomicFile.");
        }
        catch (Exception ex)
        {
            Log.Error($"[YandexAuth] Сбой атомарной записи токена: {ex.Message}");
        }
    }

    /// <summary>
    /// Удаляет сохранённый токен и его резервную копию.
    /// </summary>
    public void ClearToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                File.Delete(TokenFilePath);
            }

            var backupPath = string.Concat(TokenFilePath, ".bak");
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            Log.Info("[YandexAuth] Токен удалён.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexAuth] Не удалось удалить файл токена: {ex.Message}");
        }
    }
}
