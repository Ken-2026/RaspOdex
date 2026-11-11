using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Renombra.Core;

public sealed record LocationLookupResult(string Country, string CountryCode, string City, string Label);

public static class LocationLookup
{
    private static readonly HttpClient Client = CreateClient();
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);

    public static async Task<LocationLookupResult> ReverseAsync(
        double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Las coordenadas GPS no son válidas.");

        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastRequestUtc);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);

            string lat = latitude.ToString("0.######", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("0.######", CultureInfo.InvariantCulture);
            using var response = await Client.GetAsync($"reverse?format=jsonv2&addressdetails=1&zoom=10&lat={lat}&lon={lon}", cancellationToken)
                .ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("address", out JsonElement address))
                throw new IOException("El servicio no devolvió una ubicación para estas coordenadas.");

            string country = Text(address, "country");
            string countryCode = Text(address, "country_code").ToUpperInvariant();
            string city = FirstText(address, "city", "town", "village", "municipality", "county");
            string label = string.Join(" · ", new[] { city, country }.Where(value => value.Length > 0));
            if (country.Length == 0 && city.Length == 0)
                throw new IOException("El servicio no encontró país ni ciudad para estas coordenadas.");

            return new LocationLookupResult(country, countryCode, city, label);
        }
        catch (HttpRequestException exception)
        {
            throw new IOException("No se pudo consultar la ubicación GPS. Comprueba la conexión e inténtalo de nuevo.", exception);
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://nominatim.openstreetmap.org/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Renombra/1.2 (https://github.com/Ken-2026/Renombra)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es");
        return client;
    }

    private static string Text(JsonElement address, string name) =>
        address.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    private static string FirstText(JsonElement address, params string[] names) =>
        names.Select(name => Text(address, name)).FirstOrDefault(value => value.Length > 0) ?? "";
}
