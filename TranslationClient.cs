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
        var translated = await TranslateBatchAsync(
            [text],
            options,
            cancellationToken).ConfigureAwait(false);
        return translated[0];
    }

    public static async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
            return [];

        var translated = options.Provider switch
        {
            TranslationProvider.MicrosoftTranslator =>
                await TranslateWithMicrosoftAsync(texts, options, cancellationToken).ConfigureAwait(false),
            TranslationProvider.GoogleCloudTranslation =>
                await TranslateWithGoogleAsync(texts, options, cancellationToken).ConfigureAwait(false),
            TranslationProvider.LibreTranslate =>
                await TranslateWithLibreAsync(texts, options, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("지원하지 않는 번역 방식입니다.")
        };

        if (translated.Count != texts.Count)
        {
            throw new InvalidDataException(
                $"번역 결과 수가 요청 수와 다릅니다. 요청 {texts.Count}, 결과 {translated.Count}");
        }

        return translated.Select(Normalize).ToArray();
    }

    private static async Task<IReadOnlyList<string>> TranslateWithMicrosoftAsync(
        IReadOnlyList<string> texts,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=ko");
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        if (!string.IsNullOrWhiteSpace(options.Region))
            request.Headers.Add("Ocp-Apim-Subscription-Region", options.Region);
        request.Content = JsonContent(texts.Select(text => new { Text = text }).ToArray());

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement
            .EnumerateArray()
            .Select(item => item
                .GetProperty("translations")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> TranslateWithGoogleAsync(
        IReadOnlyList<string> texts,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var uri = "https://translation.googleapis.com/language/translate/v2?key=" +
                  Uri.EscapeDataString(options.ApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent(new
            {
                q = texts,
                source = "en",
                target = "ko",
                format = "text"
            })
        };

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement
            .GetProperty("data")
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => WebUtility.HtmlDecode(
                item.GetProperty("translatedText").GetString() ?? string.Empty))
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> TranslateWithLibreAsync(
        IReadOnlyList<string> texts,
        TranslationOptions options,
        CancellationToken cancellationToken)
    {
        var endpoint = options.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
            endpoint += "/translate";

        var payload = new Dictionary<string, object>
        {
            ["q"] = texts,
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
        var translated = document.RootElement.GetProperty("translatedText");
        if (translated.ValueKind == JsonValueKind.Array)
        {
            return translated
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
        }

        return [translated.GetString() ?? string.Empty];
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
