using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Renombra.Core;

public sealed record PhotoVisionResult(string Description, string Error = "")
{
    internal FileStamp? Snapshot { get; init; }
}

public static class PhotoVision
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(4) };
    public const string Model = "qwen3.5:0.8b";
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff";

    public static async Task<PhotoVisionResult> AnalyzeAsync(string path, bool readText, string? geminiKey = null, CancellationToken token = default)
    {
        try
        {
            if (!Supports(path)) return new("", "Formato no admitido para visión. Usa JPG, PNG, BMP o TIFF.");
            var snapshot = FileStamp.Capture(path);
            if (new FileInfo(path).Length > 100_000_000) return new("", "La imagen supera el límite de 100 MB de esta prueba.");
            byte[] thumbnail;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var width = decoder.Frames[0].PixelWidth; var height = decoder.Frames[0].PixelHeight;
                if (width < 1 || height < 1 || (long)width * height > 200_000_000)
                    return new("", "Dimensiones de imagen no válidas o demasiado grandes para esta prueba.");
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                if (width >= height && width > 768) image.DecodePixelWidth = 768;
                else if (height > 768) image.DecodePixelHeight = 768;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var buffer = new MemoryStream(); encoder.Save(buffer); thumbnail = buffer.ToArray();
            }
            token.ThrowIfCancellationRequested();
            const string schema = """{"type":"object","properties":{"descripcion":{"type":"string"}},"required":["descripcion"]}""";
            var request = new
            {
                model = Model, stream = false, think = false,
                format = JsonSerializer.Deserialize<JsonElement>(schema),
                options = new { temperature = 0, num_predict = 80, num_ctx = 4096 },
                messages = new[] { new { role = "user", content = "Describe solamente lo que se ve claramente en esta foto con entre una y cuatro palabras genéricas en español, para un nombre de archivo. No adivines nombres de personas, lugar exacto ni fecha. Si no distingues nada responde imagen. Responde solo JSON con descripcion.", images = new[] { Convert.ToBase64String(thumbnail) } } }
            };
            string description;
            if (geminiKey is not null)
            {
                var prompt = "Describe solo lo visible en esta foto con una a cuatro palabras genéricas en español. No inventes persona, ubicación ni fecha. Devuelve solo JSON con la propiedad descripcion.";
                using var fields = JsonDocument.Parse(await GeminiAi.GenerateAsync(geminiKey, prompt, thumbnail, token));
                description = fields.RootElement.GetProperty("descripcion").GetString() ?? "";
            }
            else
            {
                using var response = await Client.PostAsJsonAsync("api/chat", request, token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new("", "Falta el modelo visual local; pulsa Preparar IA visual.");
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                using var fields = JsonDocument.Parse(result.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}");
                description = fields.RootElement.GetProperty("descripcion").GetString() ?? "";
            }
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "un", "una", "unos", "unas", "el", "la", "los", "las", "de", "del", "con", "en", "y", "que", "hay", "es", "se", "ve" };
            var words = Regex.Matches(description, @"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                .Select(m => m.Value).Where(word => !stop.Contains(word)).Take(4).ToArray();
            var name = string.Join("_", words);
            if (readText)
            {
                var ocrText = await PhotoOcr.ReadAsync(path, token);
                var ocrWords = Regex.Matches(ocrText, @"[\p{L}\p{M}]{3,20}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    .Select(m => m.Value).Where(word => !stop.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
                name = string.Join("_", new[] { name }.Concat(ocrWords));
            }
            if (name.Length > 65) name = name[..65].TrimEnd('_');
            if (name.Length == 0) return new("", "La IA no pudo describir la imagen. Revísala manualmente.");
            if (FileStamp.Capture(path) != snapshot) return new("", "La imagen cambió durante el análisis; vuelve a analizarla.");
            return new(name) { Snapshot = snapshot };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new("", "No se pudo analizar la imagen: " + e.Message); }
    }
}
