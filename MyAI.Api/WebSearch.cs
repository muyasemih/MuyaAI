using System.Net.Http.Json;
using System.Text.Json;

record WebSearchResult(string Title, string Url, string Description);

record SearxSearchResponse(List<SearxSearchResult>? Results);
record SearxSearchResult(string? Title, string? Url, string? Content);

class WebSearchException(string message) : Exception(message);

class SearxNGSearchClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<SearxNGSearchClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly int _resultCount = Math.Clamp(
        configuration.GetValue("WebSearch:ResultCount", 5), 1, 10);

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<List<WebSearchResult>> SearchAsync(
        string query,
        CancellationToken ct)
    {
        var boundedQuery = query.Length > 600 ? query[..600] : query;
        var url = "search?q=" + Uri.EscapeDataString(boundedQuery) +
                  $"&format=json&language=tr&safesearch=1&pageno=1";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SearXNG {Status} döndürdü.", (int)response.StatusCode);
                var message = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden =>
                        "SearXNG JSON API kapalı. searxng/settings.yml içindeki search.formats bölümünde json olduğundan emin ol.",
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "Arama servisi çok fazla istek aldı. Biraz sonra tekrar dene.",
                    _ => "Yerel internet arama servisi şu anda yanıt vermiyor."
                };
                throw new WebSearchException(message);
            }

            var result = await response.Content.ReadFromJsonAsync<SearxSearchResponse>(
                JsonOptions, ct);

            return (result?.Results ?? new List<SearxSearchResult>())
                .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var parsed) &&
                               (parsed.Scheme == Uri.UriSchemeHttp ||
                                parsed.Scheme == Uri.UriSchemeHttps))
                .Take(_resultCount)
                .Select(item => new WebSearchResult(
                    string.IsNullOrWhiteSpace(item.Title) ? item.Url! : item.Title,
                    item.Url!,
                    LimitDescription(item.Content)))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Yerel SearXNG servisine bağlanılamadı.");
            throw new WebSearchException(
                "Yerel arama servisine ulaşılamadı. Önce proje klasöründe `docker compose up -d` komutunu çalıştır.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchException("İnternet araması zaman aşımına uğradı. Biraz sonra tekrar dene.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SearXNG geçersiz JSON döndürdü.");
            throw new WebSearchException("Arama servisi beklenmeyen bir yanıt döndürdü.");
        }
    }

    private static string LimitDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        return description.Length <= 1600 ? description : description[..1600];
    }
}
