using LMP.Core.Services;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

public static class GoogleAiTests
{
    [TestMethod(TestCategory.Integration, "Google AI: Validate Connection and Token",
        Group = "AI", Order = 1, RequiresNetwork = true, TimeoutSeconds = 15)]
    public static async Task TestGoogleAiConnectionAsync(IServiceProvider services)
    {
        var ai = services.GetRequiredService<GoogleAiService>();
        var auth = services.GetRequiredService<CookieAuthService>();

        if (!auth.IsAuthenticated)
        {
            Log.Warn("[GoogleAiTests] Test skipped: user is not authenticated.");
            return;
        }

        var response = await ai.AskAsync("Ответь строго одним словом: 'Работает'.");
        Log.Info($"[GoogleAiTests] Gemini raw reply: {response}");

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("Google AI returned an empty response.");
    }

    [TestMethod(TestCategory.Integration, "Google AI: Fetch Similar Tracks for Daft Punk",
        Group = "AI", Order = 2, RequiresNetwork = true, TimeoutSeconds = 20)]
    public static async Task TestSimilarTracksRecommendationAsync(IServiceProvider services)
    {
        var ai = services.GetRequiredService<GoogleAiService>();
        var auth = services.GetRequiredService<CookieAuthService>();

        if (!auth.IsAuthenticated)
        {
            Log.Warn("[GoogleAiTests] Test skipped: user is not authenticated.");
            return;
        }

        var recommendations = await ai.GetSimilarTracksAsync("Daft Punk", "Get Lucky", count: 5);

        Log.Info($"[GoogleAiTests] Received {recommendations.Count} recommendations:");
        for (int i = 0; i < recommendations.Count; i++)
        {
            Log.Info($"  {i + 1}. {recommendations[i]}");
        }

        if (recommendations.Count == 0)
            throw new InvalidOperationException("Google AI failed to parse recommendation list.");
    }
}