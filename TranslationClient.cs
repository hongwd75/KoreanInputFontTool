using System.Net;
using System.Text;
using System.Text.Json;

namespace KoreanInputFontTool;

internal static class TranslationClient
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<string> TranslateAsync(
        string text,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var translated = options.Provider switch
        {
            TranslationProvider.MicrosoftTranslator =>
                await TranslateWithMicrosoftAsync(text, options, cancellationToken).ConfigureAwait(false),
            TranslationProvider.GoogleCloudTranslation =>
                await TranslateWithGoogleAsync(text, options, cancellationToken).ConfigureAwait(false),
            TranslationProvider.LibreTranslate =>
                await TranslateWithLibreAsync(text, options, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("지원하지 않는 번역 방식입니다.")
        };

        return Normalize(translated);
    }

    private static async Task<string> TranslateWithMicrosoftAsync(
        string text,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=ko");
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        if (!string.IsNullOrWhiteSpace(options.Region))
            request.Headers.Add("Ocp-Apim-Subscription-Region", options.Region);
        request.Content = JsonContent(new[] { new { Text = text } });

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement[0]
            .GetProperty("translations")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    private static async Task<string> TranslateWithGoogleAsync(
        string text,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var uri = "https://translation.googleapis.com/language/translate/v2?key=" +
                  Uri.EscapeDataString(options.ApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent(new
            {
                q = text,
                source = "en",
                target = "ko",
                format = "text"
            })
        };

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var translated = document.RootElement
            .GetProperty("data")
            .GetProperty("translations")[0]
            .GetProperty("translatedText")
            .GetString() ?? string.Empty;
        return WebUtility.HtmlDecode(translated);
    }

    private static async Task<string> TranslateWithLibreAsync(
        string text,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var endpoint = options.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
            endpoint += "/translate";

        var payload = new Dictionary<string, string>
        {
            ["q"] = text,
            ["source"] = "en",
            ["target"] = "ko",
            ["format"] = "text"
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            payload["api_key"] = options.ApiKey;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent(payload)
        };
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("translatedText").GetString() ?? string.Empty;
    }

    private static StringContent JsonContent<T>(T value) => new(
        JsonSerializer.Serialize(value),
        Encoding.UTF8,
        "application/json");

    private static string Normalize(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
}
