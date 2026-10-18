using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

record WeatherQuery(string Location, bool IsTomorrow);
record WeatherAnswer(string Text, string Location, string SourceUrl);
record GeocodingResponse(List<GeocodingLocation>? Results);
record GeocodingLocation(
    string? Name,
    string? Admin1,
    string? Country,
    string? CountryCode,
    double Latitude,
    double Longitude,
    string? Timezone,
    int? Population);
record ForecastResponse(CurrentWeather? Current, DailyWeather? Daily);
record CurrentWeather(
    [property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("temperature_2m")] double? Temperature,
    [property: JsonPropertyName("apparent_temperature")] double? ApparentTemperature,
    [property: JsonPropertyName("weather_code")] int? WeatherCode,
    [property: JsonPropertyName("wind_speed_10m")] double? WindSpeed);
record DailyWeather(
    [property: JsonPropertyName("time")] List<string>? Time,
    [property: JsonPropertyName("weather_code")] List<int?>? WeatherCode,
    [property: JsonPropertyName("temperature_2m_max")] List<double?>? TemperatureMax,
    [property: JsonPropertyName("temperature_2m_min")] List<double?>? TemperatureMin,
    [property: JsonPropertyName("precipitation_probability_max")] List<double?>? PrecipitationProbabilityMax,
    [property: JsonPropertyName("precipitation_sum")] List<double?>? PrecipitationSum,
    [property: JsonPropertyName("wind_speed_10m_max")] List<double?>? WindSpeedMax);

class WeatherServiceException(string message) : Exception(message);

static partial class WeatherQueryParser
{
    [GeneratedRegex(@"\b(hava|weather|sıcaklık|sicaklik|yağmur|yagmur|yağış|yagis|rüzgar|ruzgar|kar|kaç derece|kac derece)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherKeywordRegex();

    [GeneratedRegex(@"\b(bugün|bugun|bugünkü|bugunku|yarın|yarin|şimdi|simdi|şu|su|an|hava|havası|havasi|durumu|durum|nasıl|nasil|kaç|kac|derece|sıcaklık|sicaklik|yağmur|yagmur|yağış|yagis|yağışlı|yagisli|yağacak|yagacak|rüzgar|ruzgar|rüzgarlı|ruzgarli|kar|kar yağışı|kar yagisi|sağanak|saganak|bulutlu|güneşli|gunesli|parçalı|parcali|hissedilen|nem|var|olacak|bekleniyor|tahmin|bugün|yarın|mi|mı|mu|mü|için|icin|akşam|aksam|sabah|öğlen|oglen|gece|şiddetli|siddetli|hafif|orta)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonLocationWordRegex();

    public static bool TryParse(string message, out WeatherQuery query)
    {
        query = new WeatherQuery("", false);
        if (!WeatherKeywordRegex().IsMatch(message)) return false;

        var isTomorrow = Regex.IsMatch(message, @"\b(yarın|yarin|tomorrow)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var location = message.Replace('\'', ' ').Replace('’', ' ');
        location = NonLocationWordRegex().Replace(location, " ");
        location = Regex.Replace(location, @"\b(nda|nde|ndan|nden|da|de|ta|te|dan|den|tan|ten)\b", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        location = Regex.Replace(location, @"\s+", " ").Trim(' ', ',', '.', '?', '!', ':', ';', '-');

        if (location.Length > 0 && !location.Contains(','))
        {
            var parts = location.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                var suffixes = new[] { "ndan", "nden", "tan", "ten", "dan", "den", "nda", "nde", "ta", "te", "da", "de" };
                foreach (var suffix in suffixes)
                {
                    if (parts[0].Length > suffix.Length + 2 && parts[0].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        location = parts[0][..^suffix.Length];
                        break;
                    }
                }
            }
        }

        query = new WeatherQuery(location, isTomorrow);
        return true;
    }
}

class OpenMeteoWeatherClient(HttpClient http, ILogger<OpenMeteoWeatherClient> logger)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<WeatherAnswer> GetForecastAsync(WeatherQuery query, CancellationToken ct)
    {
        var location = await FindLocationAsync(query.Location, ct);
        var forecastUrl = BuildForecastUrl(location);

        try
        {
            using var response = await http.GetAsync(forecastUrl, ct);
            response.EnsureSuccessStatusCode();
            var forecast = await response.Content.ReadFromJsonAsync<ForecastResponse>(ct)
                ?? throw new WeatherServiceException("Hava durumu servisi boş yanıt verdi.");

            return FormatForecast(query, location, forecast, forecastUrl);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Open-Meteo tahmini alınamadı.");
            throw new WeatherServiceException("Hava durumu servisine ulaşılamadı. İnternet bağlantını kontrol edip tekrar dene.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WeatherServiceException("Hava durumu isteği zaman aşımına uğradı. Tekrar deneyebilirsin.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Open-Meteo geçersiz JSON döndürdü.");
            throw new WeatherServiceException("Hava durumu servisi beklenmeyen bir yanıt verdi.");
        }
    }

    private async Task<GeocodingLocation> FindLocationAsync(string location, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new WeatherServiceException("Hava durumuna bakmam için şehir veya ilçe yazar mısın? Örnek: Gölbaşı, Ankara.");

        var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                  Uri.EscapeDataString(location) + "&count=10&language=tr&format=json";
        if (!location.Contains(',')) url += "&countryCode=TR";

        try
        {
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<GeocodingResponse>(ct);

            return (result?.Results ?? [])
                .Where(item => item.Latitude is >= -90 and <= 90 &&
                               item.Longitude is >= -180 and <= 180)
                .OrderByDescending(item => item.Population ?? 0)
                .FirstOrDefault()
                ?? throw new WeatherServiceException($"“{location}” için konum bulunamadı. İlçe ve şehri birlikte yazmayı dene.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Open-Meteo konum araması başarısız oldu.");
            throw new WeatherServiceException("Konum servisine ulaşılamadı. İnternet bağlantını kontrol edip tekrar dene.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WeatherServiceException("Konum araması zaman aşımına uğradı. Tekrar deneyebilirsin.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Open-Meteo konum araması geçersiz JSON döndürdü.");
            throw new WeatherServiceException("Konum servisi beklenmeyen bir yanıt verdi.");
        }
    }

    private static WeatherAnswer FormatForecast(
        WeatherQuery query,
        GeocodingLocation location,
        ForecastResponse forecast,
        string sourceUrl)
    {
        var daily = forecast.Daily;
        var current = forecast.Current;
        if (daily?.Time is null || daily.Time.Count == 0 || current?.Time is null)
            throw new WeatherServiceException("Hava durumu yanıtında gerekli tahmin bilgileri yok.");

        if (!DateOnly.TryParse(current.Time[..Math.Min(10, current.Time.Length)], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var today))
            throw new WeatherServiceException("Hava durumu yanıtındaki tarih okunamadı.");

        var targetDay = query.IsTomorrow ? today.AddDays(1) : today;
        var dayIndex = daily.Time.FindIndex(value => value == targetDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (dayIndex < 0)
            throw new WeatherServiceException("İstenen gün için tahmin bulunamadı.");

        var label = string.Join(", ", new[] { location.Name, location.Admin1, location.Country }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var dayName = targetDay.ToString("dddd, d MMMM", Turkish);
        var description = DescribeWeather(GetAt(daily.WeatherCode, dayIndex));
        var high = Temperature(GetAt(daily.TemperatureMax, dayIndex));
        var low = Temperature(GetAt(daily.TemperatureMin, dayIndex));
        var rainChance = GetAt(daily.PrecipitationProbabilityMax, dayIndex);
        var rainAmount = GetAt(daily.PrecipitationSum, dayIndex);
        var windMax = GetAt(daily.WindSpeedMax, dayIndex);

        string text;
        if (query.IsTomorrow)
        {
            text = $"{label} için yarın ({dayName}): {description}. " +
                   $"En yüksek {high}, en düşük {low}. " +
                   $"Yağış olasılığı {Percent(rainChance)}, beklenen yağış {Millimeters(rainAmount)}. " +
                   $"Maksimum rüzgâr {Wind(windMax)}.";
        }
        else
        {
            var currentWeather = DescribeWeather(current.WeatherCode);
            var currentLine = current.Temperature is null
                ? "Anlık sıcaklık bilgisi yok. "
                : $"Modelin güncellediği saat {Clock(current.Time, location.Timezone)} itibarıyla sıcaklık {Temperature(current.Temperature)} ({currentWeather})" +
                  (current.ApparentTemperature is null ? ". " : $", hissedilen {Temperature(current.ApparentTemperature)}. ");
            if (current.WindSpeed is not null)
                currentLine += $"Anlık rüzgâr {Wind(current.WindSpeed)}. ";

            text = $"{label} için bugün ({dayName}): {currentLine}" +
                   $"Günün tahmini: {description}; en yüksek {high}, en düşük {low}. " +
                   $"Yağış olasılığı {Percent(rainChance)}, beklenen yağış {Millimeters(rainAmount)}. " +
                   $"Maksimum rüzgâr {Wind(windMax)}.";
        }

        text += "\nVeri: Open-Meteo hava modeli (tahmindir; istasyon ölçümü olmayabilir, CC BY 4.0).";
        return new WeatherAnswer(text, label, sourceUrl);
    }

    private static string BuildForecastUrl(GeocodingLocation location)
    {
        var latitude = location.Latitude.ToString(CultureInfo.InvariantCulture);
        var longitude = location.Longitude.ToString(CultureInfo.InvariantCulture);
        var timezone = Uri.EscapeDataString(location.Timezone ?? "Europe/Istanbul");
        return "https://api.open-meteo.com/v1/forecast" +
               $"?latitude={latitude}&longitude={longitude}" +
               "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m" +
               "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max" +
               $"&timezone={timezone}&forecast_days=2&temperature_unit=celsius&wind_speed_unit=kmh";
    }

    private static T? GetAt<T>(List<T?>? values, int index) =>
        values is not null && index >= 0 && index < values.Count ? values[index] : default;

    private static string Temperature(double? value) =>
        value is null ? "bilinmiyor" : $"{value.Value.ToString("0.#", Turkish)}°C";

    private static string Percent(double? value) =>
        value is null ? "bilinmiyor" : $"%{value.Value.ToString("0", Turkish)}";

    private static string Millimeters(double? value) =>
        value is null ? "bilinmiyor" : $"{value.Value.ToString("0.#", Turkish)} mm";

    private static string Wind(double? value) =>
        value is null ? "bilinmiyor" : $"{value.Value.ToString("0.#", Turkish)} km/sa";

    private static string Clock(string value, string? timezone)
    {
        var localTime = value.Length >= 16 ? value.Substring(11, 5) : value;
        var timezoneLabel = timezone == "Europe/Istanbul" ? "Türkiye saati" : timezone;
        return string.IsNullOrWhiteSpace(timezoneLabel) ? localTime : $"{localTime} ({timezoneLabel})";
    }

    private static string DescribeWeather(int? code) => code switch
    {
        0 => "açık",
        1 => "çoğunlukla açık",
        2 => "parçalı bulutlu",
        3 => "kapalı",
        45 or 48 => "sisli",
        51 => "hafif çisenti",
        53 => "çisenti",
        55 => "yoğun çisenti",
        56 or 57 => "donan çisenti",
        61 => "hafif yağmur",
        63 => "yağmurlu",
        65 => "kuvvetli yağmur",
        66 or 67 => "donan yağmur",
        71 => "hafif kar",
        73 => "kar yağışlı",
        75 or 77 => "yoğun kar yağışlı",
        80 => "hafif sağanak",
        81 => "sağanak yağışlı",
        82 => "kuvvetli sağanak",
        85 or 86 => "kar sağanaklı",
        95 => "gök gürültülü fırtına",
        96 or 99 => "dolu ihtimalli fırtına",
        _ => "hava durumu bilgisi yok"
    };
}
