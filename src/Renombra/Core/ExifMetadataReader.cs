using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Renombra.Core;

public static class ExifMetadataReader
{
    private const int BatchSize = 128;
    private static readonly SemaphoreSlim ReadGate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string[] DateTags =
    [
        "SubSecDateTimeOriginal", "DateTimeOriginal", "CreationDate", "DateCreated",
        "SubSecCreateDate", "CreateDate", "MediaCreateDate", "TrackCreateDate"
    ];
    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy:MM:dd", "yyyy-MM-dd"
    ];
    private static readonly string ToolDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "exiftool");
    private static readonly string Executable = Path.Combine(ToolDirectory, "exiftool.exe");

    public static bool IsAvailable => File.Exists(Executable)
        && File.Exists(Path.Combine(ToolDirectory, "exiftool_files", "perl.exe"));

    public static async Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return result;

        if (!IsAvailable)
            throw new InvalidOperationException("Falta el componente ExifTool en la carpeta tools\\exiftool de Renombra.");

        var fullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Contains('\r') || path.Contains('\n'))
                throw new ArgumentException("Hay una ruta con un salto de línea.", nameof(paths));

            fullPaths.Add(Path.GetFullPath(path));
        }

        foreach (string[] batch in fullPaths.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json;
            await ReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                json = await ReadBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReadGate.Release();
            }
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? source = ReadText(item, "SourceFile");
                    if (source is null)
                        continue;

                    string path = Path.GetFullPath(source);
                    if (!fullPaths.Contains(path))
                        continue;

                    string? make = ReadText(item, "Make");
                    var metadata = new FileMetadata(
                        ReadDate(item),
                        ReadCamera(item, make),
                        ReadDimension(item, "ImageWidth") ?? ReadDimension(item, "SourceImageWidth"),
                        ReadDimension(item, "ImageHeight") ?? ReadDimension(item, "SourceImageHeight"))
                    {
                        Make = make ?? "",
                        Lens = ReadText(item, "LensModel") ?? "",
                        Iso = ReadScalar(item, "ISO"),
                        FocalLength = ReadScalar(item, "FocalLength"),
                        Aperture = ReadScalar(item, "FNumber"),
                        Exposure = ReadScalar(item, "ExposureTime"),
                        Latitude = ReadCoordinate(item, "GPSLatitude"),
                        Longitude = ReadCoordinate(item, "GPSLongitude")
                    };
                    if (ExifFields.All.Any(field => field.ReadValue(metadata).Length > 0))
                        result[path] = metadata;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new IOException("ExifTool devolvió una respuesta que Renombra no pudo leer.", exception);
            }
        }

        return result;
    }

    private static async Task<string> ReadBatchAsync(string[] paths, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["PERL5OPT"] = null;
        startInfo.Environment["PERL5LIB"] = null;
        startInfo.Environment["PERLLIB"] = null;

        string[] options =
        [
            "-config", "", "-charset", "filename=UTF8", "-json", "-q", "-Make", "-Model",
            "-CameraModelName", "-ImageWidth#", "-ImageHeight#", "-SourceImageWidth#", "-SourceImageHeight#",
            "-LensModel", "-ISO", "-FocalLength", "-FNumber", "-ExposureTime", "-GPSLatitude#", "-GPSLongitude#"
        ];
        foreach (string option in options)
            startInfo.ArgumentList.Add(option);
        foreach (string tag in DateTags)
            startInfo.ArgumentList.Add("-" + tag);

        // ExifTool consumes UTF-8 arguments from stdin, including filenames beyond the Windows code page.
        startInfo.ArgumentList.Add("-@");
        startInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new IOException("No se pudo iniciar ExifTool. Comprueba que conservas la carpeta exiftool_files junto al ejecutable.", exception);
        }

        using var registration = cancellationToken.Register(() => StopProcess(process));
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(
                WritePathsAsync(process.StandardInput, paths, cancellationToken),
                outputTask,
                errorTask,
                process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Se interrumpió la lectura de metadatos con ExifTool.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string output = await outputTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(output))
            return output;

        string errors = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(errors))
        {
            string firstError = errors.Trim().Split('\n', 2)[0].TrimEnd();
            throw new IOException($"ExifTool no pudo leer los metadatos. {firstError}");
        }

        if (process.ExitCode != 0)
            throw new IOException($"ExifTool se cerró con el código {process.ExitCode} al leer los metadatos.");

        return "[]";
    }

    private static async Task WritePathsAsync(StreamWriter input, string[] paths, CancellationToken cancellationToken)
    {
        using (input)
        {
            await input.WriteLineAsync("--".AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.WriteLineAsync(string.Join('\n', paths).AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process may have exited while cancellation was being handled.
        }
    }

    private static DateTime? ReadDate(JsonElement item)
    {
        foreach (string tag in DateTags)
        {
            string? value = ReadText(item, tag);
            if (value is null)
                continue;

            // Keep the capture's local clock time, including when an offset follows it.
            ReadOnlySpan<char> date = value.AsSpan(0, Math.Min(19, value.Length));
            if (DateTime.TryParseExact(date, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                return parsed;
        }

        return null;
    }

    private static string ReadCamera(JsonElement item, string? make)
    {
        string? model = ReadText(item, "Model") ?? ReadText(item, "CameraModelName");
        if (model is null)
            return make ?? string.Empty;

        if (make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
            return model;

        return $"{make} {model}";
    }

    private static string? ReadText(JsonElement item, string tag)
    {
        if (!item.TryGetProperty(tag, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return null;

        string? text = element.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ReadScalar(JsonElement item, string tag)
    {
        if (!item.TryGetProperty(tag, out JsonElement element))
            return "";

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Trim() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            _ => ""
        };
    }

    private static int? ReadDimension(JsonElement item, string tag)
    {
        if (!item.TryGetProperty(tag, out JsonElement element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) && number > 0)
            return number;

        return element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int textNumber)
            && textNumber > 0 ? textNumber : null;
    }

    private static double? ReadCoordinate(JsonElement item, string tag)
    {
        string value = ReadScalar(item, tag);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double coordinate)
            && coordinate is >= -180 and <= 180 ? coordinate : null;
    }
}
