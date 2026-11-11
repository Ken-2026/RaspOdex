using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Renombra.Core;

public static class GeoNamesOfflineLookup
{
    private const string DatabaseName = "geonames.sqlite";

    public static string? DatabasePath => FindDatabase();

    public static async Task<LocationLookupResult> ReverseAsync(
        double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Las coordenadas GPS no son válidas.");

        string database = FindDatabase()
            ?? throw new FileNotFoundException("No se encontró el índice local de GeoNames.", DatabaseName);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (double radius in new[] { 0.05, 0.15, 0.5, 1.5, 5.0, 15.0 })
        {
            var result = await FindNearestAsync(connection, latitude, longitude, radius, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        throw new IOException("GeoNames no encontró una localidad próxima a estas coordenadas.");
    }

    private static async Task<LocationLookupResult?> FindNearestAsync(
        SqliteConnection connection, double latitude, double longitude, double radius,
        CancellationToken cancellationToken)
    {
        double longitudeScale = Math.Max(0.05, Math.Cos(latitude * Math.PI / 180));
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.name, p.country_code
            FROM place_index i
            JOIN places p ON p.id = i.id
            WHERE i.min_latitude BETWEEN $minLatitude AND $maxLatitude
              AND i.min_longitude BETWEEN $minLongitude AND $maxLongitude
            ORDER BY
              ((p.latitude - $latitude) * (p.latitude - $latitude)) +
              (((p.longitude - $longitude) * $longitudeScale) *
               ((p.longitude - $longitude) * $longitudeScale)),
              p.population DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$minLatitude", latitude - radius);
        command.Parameters.AddWithValue("$maxLatitude", latitude + radius);
        command.Parameters.AddWithValue("$minLongitude", longitude - radius);
        command.Parameters.AddWithValue("$maxLongitude", longitude + radius);
        command.Parameters.AddWithValue("$latitude", latitude);
        command.Parameters.AddWithValue("$longitude", longitude);
        command.Parameters.AddWithValue("$longitudeScale", longitudeScale);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        string city = reader.GetString(0).Trim();
        string code = reader.GetString(1).Trim().ToUpperInvariant();
        string country = CountryName(code);
        string label = string.Join(" · ", new[] { city, country }.Where(value => value.Length > 0));
        return new LocationLookupResult(country, code, city, label);
    }

    private static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    private static string? FindDatabase()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "data", "GeoNames", DatabaseName),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "data", "GeoNames", DatabaseName)),
            Path.Combine(baseDirectory, DatabaseName)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
