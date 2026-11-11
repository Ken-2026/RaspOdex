using System.IO;
using System.Text.Json;

namespace Renombra.Core;

public sealed record TemplatePreset(string Name, string Template, int Start = 1, int Digits = 3);

public static class TemplatePresets
{
    private const string FileName = "Renombra.Plantillas.json";
    private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, FileName);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static IReadOnlyList<TemplatePreset> BuiltIn { get; } = Array.AsReadOnly(new[]
    {
        new TemplatePreset("Fecha de captura y nombre", "{fecha:yyyyMMdd}_{nombre}"),
        new TemplatePreset("Fecha, cámara y número", "{fecha:yyyyMMdd}_{camara}_{n}"),
        new TemplatePreset("Dimensiones y nombre", "{ancho}x{alto}_{nombre}")
    });

    public static IReadOnlyList<TemplatePreset> Read()
    {
        try
        {
            using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var presets = JsonSerializer.Deserialize<TemplatePreset[]>(stream, JsonOptions)
                ?? throw new InvalidDataException("El archivo de plantillas está vacío.");
            return Validate(presets);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<TemplatePreset>();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"El archivo {FileName} contiene JSON no válido.", error);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo leer {FileName} en la carpeta de Renombra.", error);
        }
    }

    public static void Save(IReadOnlyList<TemplatePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var validated = Validate(presets);
        var temporaryPath = Path.Combine(AppContext.BaseDirectory, $"{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, validated, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, StorePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"No se pudieron guardar las plantillas en {FileName}. Comprueba que puedes escribir en la carpeta de Renombra.",
                error);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Preserve the original save error if the temporary file cannot be removed.
            }
        }
    }

    private static TemplatePreset[] Validate(IReadOnlyList<TemplatePreset> presets)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new TemplatePreset[presets.Count];
        for (int index = 0; index < presets.Count; index++)
        {
            var preset = presets[index];
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name) ||
                preset.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(preset.Template))
            {
                throw new InvalidDataException("Cada plantilla necesita un nombre y una regla.");
            }

            var name = preset.Name.Trim();
            if (!names.Add(name))
                throw new InvalidDataException($"Hay más de una plantilla llamada «{name}».");
            if (preset.Start < 0 || preset.Digits is < 1 or > 12)
                throw new InvalidDataException($"La numeración de la plantilla «{name}» no es válida.");

            result[index] = preset with { Name = name };
        }

        return result;
    }
}
