using LMP.Core.Helpers;

namespace LMP.Core.Services;

/// <summary>
/// Manages persistent storage of Yandex Music OAuth tokens via AtomicFile.
/// </summary>
public sealed class YandexAuthService
{
    private static string TokenFilePath =>
        Path.Combine(Path.GetDirectoryName(G.FilePath.Database) ?? AppContext.BaseDirectory, "yandex_token.bin");

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
            Log.Warn($"[YandexAuth] Failed to read token: {ex.Message}");
        }

        return null;
    }

    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            AtomicFile.WriteText(TokenFilePath, token.Trim(), createBackup: true);
            Log.Info("[YandexAuth] Token saved successfully via AtomicFile.");
        }
        catch (Exception ex)
        {
            Log.Error($"[YandexAuth] Failed to write token atomically: {ex.Message}");
        }
    }

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

            Log.Info("[YandexAuth] Token purged.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexAuth] Failed to delete token file: {ex.Message}");
        }
    }
}
