using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Renombra.Core;

public sealed record DocumentAnalysis(string SuggestedName, string Summary, string Method, string Error = "")
{
    internal FileStamp? Snapshot { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

public sealed record DocumentPreferences(bool Organization, bool Type, bool Subject, bool Person, bool Reference, bool Identifier, bool Date, bool UseAi, string Model = "qwen3:1.7b", string? GeminiKey = null);

public static class DocumentAnalyzer
{
    private const int PageLimit = 3;
    private const int MaxText = 100_000;
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Words = new(@"[\p{L}][\p{L}\p{M}]{2,}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "como", "sobre", "este", "esta", "esto", "estos", "estas", "donde", "desde", "hasta", "mediante",
        "documento", "documentos", "pagina", "página", "datos", "nombre", "total", "fecha", "numero", "número",
        "con", "por", "del", "los", "las", "una", "uno", "unos", "unas", "que", "sus", "dicha", "dicho", "ante",
        "the", "and", "for", "with", "from", "this", "that", "you", "your", "are", "was", "have", "not", "all",
        "www", "http", "https", "com", "info", "email", "telefono", "teléfono", "dirección", "direccion"
    };

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx";

    public static async Task<DocumentAnalysis> AnalyzeAsync(string path, DocumentPreferences preferences, CancellationToken cancellationToken = default)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!Supports(path)) return new("", "", "", "Solo se admiten PDF y DOCX en esta prueba.");
            var snapshot = FileStamp.Capture(path);
            var (text, method) = ext == ".pdf" ? await ReadPdfAsync(path, cancellationToken) : (ReadDocx(path), "Texto de Word");
            cancellationToken.ThrowIfCancellationRequested();
            if (FileStamp.Capture(path) != snapshot) return new("", "", method, "El documento cambió durante el análisis; vuelve a analizarlo.");
            if (Words.Matches(text).Count < 3)
                return new("", "", method, "No se encontró suficiente texto para proponer un nombre. Comprueba el documento o el OCR.");
            var fields = ExtractLocalFields(text);
            if (preferences.UseAi || preferences.GeminiKey is not null)
            {
                try
                {
                    var aiFields = preferences.GeminiKey is not null ? await GeminiAi.ExtractDocumentAsync(text, preferences.GeminiKey, cancellationToken) : await LocalDocumentAi.ExtractAsync(text, preferences.Model, cancellationToken);
                    foreach (var field in aiFields) fields[field.Key] = field.Value;
                    method += preferences.GeminiKey is null ? " · IA local" : " · Gemini en línea";
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (preferences.GeminiKey is not null) return new("", "", method, "Gemini no pudo analizar el documento: " + e.Message);
                    method += " · reglas (IA local no disponible)";
                }
            }
            if (FileStamp.Capture(path) != snapshot) return new("", "", method, "El documento cambió durante el análisis; vuelve a analizarlo.");
            var suggestion = BuildName(fields, preferences);
            return new(suggestion, text.Length <= 500 ? text : text[..500] + "…", method) { Snapshot = snapshot, Fields = fields };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return new("", "", "", "No se pudo leer: " + e.Message);
        }
        catch (Exception e)
        {
            return new("", "", "", "No se pudo analizar el documento: " + e.Message);
        }
    }

    public static string BuildName(IReadOnlyDictionary<string, string> fields, DocumentPreferences preferences)
    {
        var selected = new[] { ("organismo", preferences.Organization), ("tipo", preferences.Type), ("asunto", preferences.Subject),
            ("persona", preferences.Person), ("referencia", preferences.Reference), ("identificador", preferences.Identifier), ("fecha", preferences.Date) };
        fields.TryGetValue("tipo", out var type);
        fields.TryGetValue("asunto", out var subject);
        var redundantSubject = !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(subject) &&
            preferences.Type && preferences.Subject && type.Contains(subject, StringComparison.OrdinalIgnoreCase);
        var redundantType = !redundantSubject && preferences.Type && preferences.Subject &&
            !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(subject) && subject.Contains(type, StringComparison.OrdinalIgnoreCase);
        fields.TryGetValue("identificador", out var privateId);
        var parts = selected.Where(x => x.Item2 && fields.ContainsKey(x.Item1) && !(x.Item1 == "asunto" && redundantSubject) && !(x.Item1 == "tipo" && redundantType))
            .Select(x => !preferences.Identifier && !string.IsNullOrWhiteSpace(privateId)
                ? fields[x.Item1].Replace(privateId, "", StringComparison.OrdinalIgnoreCase).Trim()
                : fields[x.Item1].Trim())
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return parts.Length == 0 ? "Documento" : string.Join("_", parts);
    }

    private static Dictionary<string, string> ExtractLocalFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = text.Replace('\r', '\n');
        var organization = Regex.Match(source, @"(?im)^\s*(?:proveedor|empresa|emisor|organismo)\s*[:\-]\s*(?<v>[^\r\n]{2,60})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (organization.Success) fields["organismo"] = organization.Groups["v"].Value.Trim();
        else if (source.Contains("Seguridad Social", StringComparison.OrdinalIgnoreCase)) fields["organismo"] = "Seguridad Social";
        var type = Regex.Match(source, @"(?im)\b(resoluci[oó]n|certificado|factura|n[oó]mina|contrato|presupuesto|recibo|reserva|informe|extracto bancario|licencia)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (type.Success) fields["tipo"] = type.Value;
        var subject = Regex.Match(source, @"(?im)\b(jubilaci[oó]n|vida laboral|incapacidad|electricidad|licencia de obras|prestaci[oó]n por desempleo)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (subject.Success) fields["asunto"] = subject.Value;
        var person = Regex.Match(source, @"(?im)^\s*(?:nombre y apellidos|interesado|titular)\s*[:\-]\s*(?<v>[^\r\n]{3,70})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (person.Success) fields["persona"] = person.Groups["v"].Value.Trim();
        var reference = Regex.Match(source, @"(?i)\b(?:expediente|referencia|n[uú]mero de factura)\s*[:#\-]?\s*(?<v>[A-Z0-9][A-Z0-9/\-]{2,35})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (reference.Success) fields["referencia"] = reference.Groups["v"].Value;
        var identifier = Regex.Match(source, @"(?i)\b(?:DNI|NIE|NIF)\s*[:\-]?\s*(?<v>[XYZ]?\d{7,8}[A-Z])\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (identifier.Success) fields["identificador"] = identifier.Groups["v"].Value;
        var date = Regex.Match(source, @"\b(?:\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2}|(?:19|20)\d{2}[./-]\d{1,2}[./-]\d{1,2})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (date.Success) fields["fecha"] = date.Value;
        return fields;
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("El archivo no contiene un documento Word válido.");
        if (entry.Length > 20_000_000) throw new InvalidDataException("El texto interno del documento es demasiado grande.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var text = new StringBuilder();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = XDocument.Load(reader);
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            foreach (var run in paragraph.Descendants(w + "t")) text.Append(run.Value);
            text.AppendLine();
            if (text.Length >= MaxText) break;
        }
        return text.ToString();
    }

    private static async Task<(string Text, string Method)> ReadPdfAsync(string path, CancellationToken token)
    {
        var pages = new List<(int Number, string Text)>();
        int total;
        using (var pdf = PdfDocument.Open(path))
        {
            total = pdf.NumberOfPages;
            for (var i = 1; i <= Math.Min(total, PageLimit); i++)
            {
                token.ThrowIfCancellationRequested();
                pages.Add((i, ContentOrderTextExtractor.GetText(pdf.GetPage(i))));
            }
        }
        bool usedOcr = false;
        var text = new StringBuilder();
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            string pageText = page.Text;
            if (Words.Matches(pageText).Count < 5)
            {
                pageText = await OcrPdfPageAsync(path, page.Number, token);
                usedOcr = true;
            }
            text.AppendLine(pageText);
            if (text.Length >= MaxText) break;
        }
        return (text.ToString(), (usedOcr ? "PDF con OCR" : "Texto de PDF") + $" · {Math.Min(total, PageLimit)} de {total} páginas");
    }

    private static async Task<string> OcrPdfPageAsync(string path, int page, CancellationToken token)
    {
        var localTesseract = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tesseract.exe");
        var tesseract = File.Exists(localTesseract) ? localTesseract : FindExecutable("tesseract.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"));
        var renderer = FindPdfRenderer();
        if (tesseract is null || renderer is null)
            throw new IOException("Para leer PDF escaneados se necesitan Tesseract (español) y pdftoppm. Consulta la guía de la versión de prueba.");
        var dir = Path.Combine(Path.GetTempPath(), "RaspOdex-OCR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prefix = Path.Combine(dir, "pagina");
            await RunAsync(renderer, ["-f", page.ToString(), "-l", page.ToString(), "-r", "150", "-scale-to", "2200", "-png", "-singlefile", path, prefix], token);
            var bundledData = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tessdata");
            return tesseract == localTesseract
                ? await RunAsync(tesseract, [prefix + ".png", "stdout", "--tessdata-dir", bundledData, "-l", "spa+eng", "--psm", "3"], token)
                : await RunAsync(tesseract, [prefix + ".png", "stdout", "-l", "spa+eng", "--psm", "3"], token);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FindPdfRenderer()
    {
        return FindExecutable("pdftoppm.exe", Path.Combine(AppContext.BaseDirectory, "tools", "poppler", "Library", "bin", "pdftoppm.exe"));
    }

    private static string? FindExecutable(string name, string preferred)
    {
        if (File.Exists(preferred)) return preferred;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var path = Path.Combine(dir.Trim('"'), name); if (File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("No se pudo iniciar la herramienta de lectura.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } if (token.IsCancellationRequested) throw; throw new IOException("La lectura tardó demasiado."); }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("La herramienta de lectura no pudo procesar la página. " + (error.Length > 300 ? error[..300] : error));
        return output;
    }

    public static string Suggest(string text)
    {
        var clean = text.Replace('\r', '\n');
        var normalized = clean.Normalize(NormalizationForm.FormD);
        normalized = string.Concat(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        // Pistas del proyecto local MiOCRPotente: sumar indicios reduce decisiones por una sola palabra.
        var rules = new (string Name, string[] Terms)[]
        {
            ("Factura", ["factura", "invoice", "base imponible", "importe total"]),
            ("Nómina", ["nomina", "salario bruto", "devengos", "retenciones"]),
            ("Contrato", ["contrato", "contract", "acuerdo entre", "las partes"]),
            ("Presupuesto", ["presupuesto", "quotation", "oferta economica"]),
            ("Recibo", ["recibo", "receipt", "justificante de pago"]),
            ("Informe", ["informe", "report", "informe medico"]),
            ("Reserva", ["reserva", "booking", "localizador"]),
            ("Extracto", ["extracto bancario", "movimientos de cuenta", "bank statement"])
        };
        var scores = rules.Select(rule => (rule.Name, Score: rule.Terms.Count(term =>
            Regex.IsMatch(normalized, @"\b" + Regex.Escape(term) + @"\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))))
            .OrderByDescending(item => item.Score).ToArray();
        var kind = scores[0].Score > 0 ? scores[0].Name : "Documento";
        var entityMatch = Regex.Match(clean, @"(?im)^\s*(?:proveedor|empresa|emisor)\s*[:\-]\s*(?<value>[^\r\n]{2,60})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var entity = entityMatch.Success ? entityMatch.Groups["value"].Value.Trim() : "";
        var freq = Words.Matches(clean).Select(m => m.Value).Where(w => !StopWords.Contains(w) && w.Length <= 30)
            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var terms = freq.OrderByDescending(x => x.Value).ThenBy(x => clean.IndexOf(x.Key, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key).Where(w => !w.Equals(kind, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
        var dateMatch = Regex.Match(clean, @"\b(?<d>\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2}|(?:19|20)\d{2}[./-]\d{1,2}[./-]\d{1,2})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var date = dateMatch.Success && DateTime.TryParse(dateMatch.Groups["d"].Value, Spanish, DateTimeStyles.None, out var value) ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        var components = new[] {kind, date, entity, string.Join("_", terms)}.Where(x => x.Length > 0);
        var name = Spaces.Replace(string.Join("_", components), "_");
        return name.Length > 100 ? name[..100].TrimEnd('_', '.', ' ') : name;
    }
}
