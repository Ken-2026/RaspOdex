using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Renombra.Core;

public sealed record FileItem(
    string FullPath,
    DateTime Modified,
    DateTime Created,
    FileMetadata? Metadata = null,
    LocationLookupResult? ResolvedLocation = null);

public sealed record FileMetadata(DateTime? DateTaken = null, string Camera = "", int? Width = null, int? Height = null)
{
    public string Make { get; init; } = "";
    public string Lens { get; init; } = "";
    public string Iso { get; init; } = "";
    public string FocalLength { get; init; } = "";
    public string Aperture { get; init; } = "";
    public string Exposure { get; init; } = "";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

public sealed record ExifField(string Token, string Label, Func<FileMetadata, string> ReadValue);

public static class ExifFields
{
    public static IReadOnlyList<ExifField> All { get; } = Array.AsReadOnly<ExifField>(
    [
        new("fecha", "Fecha de captura", metadata =>
            metadata.DateTaken?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""),
        new("hora", "Hora de captura", metadata =>
            metadata.DateTaken?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? ""),
        new("dia_semana", "Día de la semana", metadata =>
            metadata.DateTaken?.ToString("dddd", CultureInfo.GetCultureInfo("es-ES")) ?? ""),
        new("fecha_segura", "Fecha de captura o modificación", metadata =>
            metadata.DateTaken?.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) ?? ""),
        new("camara", "Cámara", metadata => metadata.Camera),
        new("fabricante", "Fabricante", metadata => metadata.Make),
        new("objetivo", "Objetivo", metadata => metadata.Lens),
        new("iso", "ISO", metadata => metadata.Iso),
        new("focal", "Distancia focal", metadata => metadata.FocalLength),
        new("apertura", "Apertura (f/)", metadata => metadata.Aperture),
        new("exposicion", "Tiempo de exposición", metadata => metadata.Exposure),
        new("latitud", "Latitud GPS", metadata => FormatCoordinate(metadata.Latitude)),
        new("longitud", "Longitud GPS", metadata => FormatCoordinate(metadata.Longitude)),
        new("ancho", "Ancho (px)", metadata => metadata.Width?.ToString(CultureInfo.InvariantCulture) ?? ""),
        new("alto", "Alto (px)", metadata => metadata.Height?.ToString(CultureInfo.InvariantCulture) ?? "")
    ]);

    private static readonly string[] Tokens = All.Select(field => field.Token).ToArray();
    private static readonly Dictionary<string, ExifField> FieldsByToken = All.ToDictionary(
        field => field.Token, StringComparer.OrdinalIgnoreCase);

    public static bool IsUsedIn(string template) => RenameEngine.TemplateUses(template, Tokens);

    internal static ExifField? Find(string token) => FieldsByToken.GetValueOrDefault(token);

    private static string FormatCoordinate(double? coordinate) => coordinate?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
}

public enum TextCase { Unchanged, Lower, Upper, Title }

public sealed record RenameOptions(
    string Search = "",
    string Replacement = "",
    bool Regex = false,
    bool MatchCase = false,
    bool ReplaceAll = true,
    bool EditExtension = false,
    string Template = "",
    TextCase Casing = TextCase.Unchanged,
    bool Number = false,
    int Start = 1,
    int Digits = 3,
    string Location = "",
    string Country = "",
    string CountryCode = "",
    string Indicator = "",
    string City = "",
    string OutputFolder = "",
    bool AutoUnique = false);

public enum RenameStatus { Unchanged, Ready, Error }

public sealed record RenameRow(FileItem File, string NewName, RenameStatus Status, string Detail)
{
    public string? OutputFolder { get; init; }
    public string Destination => Path.Combine(OutputFolder ?? Path.GetDirectoryName(File.FullPath) ?? "", NewName);
    internal FileStamp? Snapshot { get; init; }
}

public sealed record RenameMove(string Source, string Destination)
{
    internal FileStamp? Snapshot { get; init; }
}

public static class RenameEngine
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly Regex TemplateParts = new(@"\{\{|\}\}|\{([^{}]+)\}|[{}]");
    private static readonly Regex ReservedNames = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    internal static bool TemplateUses(string template, params string[] fields)
    {
        foreach (Match part in TemplateParts.Matches(template))
        {
            if (!part.Groups[1].Success)
                continue;

            var field = part.Groups[1].Value.Split(':', 2)[0].Trim();
            if (fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool TemplateUsesLocation(string template) =>
        TemplateUses(template, "lugar", "pais", "codigo_pais", "ciudad", "indicador");

    internal static bool TemplateUsesGpsLocation(string template) =>
        TemplateUses(template, "pais", "codigo_pais", "ciudad");

    public static IReadOnlyList<RenameRow> Preview(
        IReadOnlyList<FileItem> files, RenameOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Regex? searchPattern = null;
        if (options.Regex && options.Search.Length > 0)
        {
            try
            {
                var flags = RegexOptions.CultureInvariant;
                if (!options.MatchCase)
                    flags |= RegexOptions.IgnoreCase;

                searchPattern = new Regex(options.Search, flags, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                return ErrorForAll("La expresión regular no es válida.");
            }
        }

        var rows = new List<RenameRow>(files.Count);
        var sourceGroups = new Dictionary<string, List<int>>(PathComparer);
        var destinationGroups = new Dictionary<string, List<int>>(PathComparer);
        var reserved = new HashSet<string>(PathComparer);
        var sourcePaths = new HashSet<string>(files.Select(file => Path.GetFullPath(file.FullPath)), PathComparer);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            try
            {
                var source = FullFilePath(file.FullPath);
                AddIndex(sourceGroups, source, index);
                var original = Path.GetFileName(source);
                var name = MakeName(file, original, options, searchPattern, index);
                ValidateName(name);

                var outputFolder = string.IsNullOrWhiteSpace(options.OutputFolder) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputFolder);
                if (!Directory.Exists(outputFolder))
                    throw new IOException("La carpeta de salida no existe.");
                if (!PathComparer.Equals(Path.GetPathRoot(source), Path.GetPathRoot(outputFolder)))
                    throw new IOException("La carpeta de salida debe estar en la misma unidad que el archivo de origen.");
                var destination = Path.Combine(outputFolder, name);
                if (options.AutoUnique)
                {
                    var (baseName, extension) = SplitName(name);
                    var suffix = 1;
                    while (reserved.Contains(destination) ||
                           (!PathComparer.Equals(source, destination) && (sourcePaths.Contains(destination) || Path.Exists(destination))))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        name = $"{baseName}_{suffix++:D3}{extension}";
                        ValidateName(name);
                        destination = Path.Combine(outputFolder, name);
                    }
                    reserved.Add(destination);
                }
                var changed = !StringComparer.Ordinal.Equals(source, destination);
                var snapshot = changed ? FileStamp.Capture(source) : (FileStamp?)null;
                if (!changed && !File.Exists(source))
                    throw new IOException("El archivo ya no está disponible.");

                var row = new RenameRow(file, name,
                    changed ? RenameStatus.Ready : RenameStatus.Unchanged,
                    changed ? "Listo" : "Sin cambios")
                {
                    Snapshot = snapshot,
                    OutputFolder = outputFolder
                };
                AddIndex(destinationGroups, destination, index);
                rows.Add(row);
            }
            catch (RegexMatchTimeoutException)
            {
                return ErrorForAll("La expresión regular tardó demasiado. Prueba un patrón más simple.");
            }
            catch (Exception error) when (IsFileError(error) || error is FormatException)
            {
                rows.Add(Error(file, error.Message));
            }
        }

        foreach (var indices in sourceGroups.Values.Where(group => group.Count > 1))
        {
            foreach (var index in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows[index] = WithError(rows[index], "El archivo aparece más de una vez en la lista.");
            }
        }

        foreach (var indices in destinationGroups.Values.Where(group => group.Count > 1))
        {
            foreach (var index in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows[index].Status == RenameStatus.Ready)
                    rows[index] = WithError(rows[index], "Otro archivo de la lista quedaría con este nombre.");
            }
        }

        var movingSources = new Dictionary<string, FileStamp>(PathComparer);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Status == RenameStatus.Ready)
                movingSources.Add(FullFilePath(row.File.FullPath), row.Snapshot!.Value);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.Status != RenameStatus.Ready)
                continue;

            try
            {
                CheckDestination(FullFilePath(row.Destination), movingSources);
            }
            catch (Exception error) when (IsFileError(error))
            {
                rows[index] = WithError(row, error.Message);
            }
        }

        return rows;

        RenameRow[] ErrorForAll(string message)
        {
            var errors = new RenameRow[files.Count];
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                errors[index] = Error(files[index], message);
            }
            return errors;
        }
    }

    public static IReadOnlyList<RenameMove> Apply(IReadOnlyList<RenameRow> rows)
    {
        if (rows.Any(row => row.Status == RenameStatus.Error))
            throw new InvalidOperationException("Resuelve los errores de la vista previa antes de renombrar.");

        var moves = new List<RenameMove>();
        foreach (var row in rows.Where(row => row.Status == RenameStatus.Ready))
        {
            ValidateName(row.NewName);
            moves.Add(new RenameMove(row.File.FullPath, row.Destination) { Snapshot = row.Snapshot });
        }
        return Execute(moves);
    }

    public static void Undo(IReadOnlyList<RenameMove> moves)
    {
        var reverse = moves.Select(move => new RenameMove(move.Destination, move.Source) { Snapshot = move.Snapshot })
            .ToArray();
        Execute(reverse);
    }

    private static string MakeName(FileItem file, string original, RenameOptions options, Regex? searchPattern, int index)
    {
        var (originalStem, originalExtension) = SplitName(original);
        var workingName = Replace(options.EditExtension ? original : originalStem, options, searchPattern);
        var (stem, extension) = options.EditExtension ? SplitName(workingName) : (workingName, originalExtension);
        var numberInTemplate = false;

        if (options.Template.Length > 0)
        {
            workingName = TemplateParts.Replace(options.Template, part =>
            {
                if (part.Value is "{{" or "}}")
                    return part.Value[..1];

                if (!part.Groups[1].Success)
                    throw new FormatException("Hay una llave de plantilla sin cerrar o sin un campo válido.");

                var field = part.Groups[1].Value.Split(':', 2);
                var key = field[0].Trim().ToLowerInvariant();
                var format = field.Length == 2 ? field[1] : null;
                if (format is not null && key is not ("fecha" or "hora" or "modificado" or "creado"))
                    throw new FormatException($"El campo {{{key}}} no admite un formato.");

                switch (key)
                {
                    case "nombre": return stem;
                    case "ext": return extension.Length == 0 ? "" : extension[1..];
                    case "n":
                        numberInTemplate = true;
                        return FormatNumber(options, index);
                    case "fecha": return FormatDate(file.Metadata?.DateTaken, format, "fecha de captura");
                    case "fecha_segura": return FormatDate(file.Metadata?.DateTaken ?? file.Modified, "yyyyMMdd_HHmmss", "fecha");
                    case "hora": return FormatDate(file.Metadata?.DateTaken, format ?? "HHmmss", "hora de captura");
                    case "modificado": return FormatDate(file.Modified, format, "fecha de modificación");
                    case "creado": return FormatDate(file.Created, format, "fecha de creación");
                    case "lugar":
                        return FormatLocation(options.Location.Length > 0
                            ? options.Location
                            : file.ResolvedLocation?.Label ?? "");
                    case "pais":
                        return FormatLocationPart(options.Country.Length > 0
                            ? options.Country
                            : file.ResolvedLocation?.Country ?? "", "país");
                    case "codigo_pais":
                        return FormatLocationPart(options.CountryCode.Length > 0
                            ? options.CountryCode
                            : file.ResolvedLocation?.CountryCode ?? "", "código de país");
                    case "indicador": return FormatLocationPart(options.Indicator, "indicador");
                    case "ciudad":
                        return FormatLocationPart(file.ResolvedLocation?.City ?? options.City, "ciudad");
                    default: return FormatMetadata(file.Metadata, key);
                }
            });
        }

        workingName = options.Casing switch
        {
            TextCase.Lower => workingName.ToLower(Spanish),
            TextCase.Upper => workingName.ToUpper(Spanish),
            TextCase.Title => Spanish.TextInfo.ToTitleCase(workingName.ToLower(Spanish)),
            _ => workingName
        };

        if (workingName.Length == 0)
            throw new FormatException("Escribe un nombre para el archivo.");

        if (!options.EditExtension)
            return options.Number && !numberInTemplate
                ? $"{workingName}_{FormatNumber(options, index)}{originalExtension}"
                : workingName + originalExtension;

        if (!options.Number || numberInTemplate)
            return workingName;

        var (outputStem, outputExtension) = SplitName(workingName);
        return $"{outputStem}_{FormatNumber(options, index)}{outputExtension}";
    }

    private static string FormatNumber(RenameOptions options, int index)
    {
        if (options.Start < 0 || options.Digits is < 1 or > 12)
            throw new FormatException("Usa un inicio mayor o igual a 0 y entre 1 y 12 dígitos.");

        return ((long)options.Start + index).ToString($"D{options.Digits}", CultureInfo.InvariantCulture);
    }

    private static string Replace(string value, RenameOptions options, Regex? searchPattern)
    {
        if (options.Search.Length == 0)
            return value;

        if (searchPattern is not null)
            return searchPattern.Replace(value, options.Replacement, options.ReplaceAll ? -1 : 1);

        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (options.ReplaceAll)
            return value.Replace(options.Search, options.Replacement, comparison);

        var position = value.IndexOf(options.Search, comparison);
        return position < 0
            ? value
            : value[..position] + options.Replacement + value[(position + options.Search.Length)..];
    }

    private static (string Stem, string Extension) SplitName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    private static string FormatDate(DateTime? date, string? format, string label)
    {
        if (date is null)
            throw new FormatException($"Este archivo no tiene {label} registrada.");

        try
        {
            return date.Value.ToString(format ?? "yyyy-MM-dd", Spanish);
        }
        catch (FormatException)
        {
            throw new FormatException($"El formato de {label} no es válido.");
        }
    }

    private static string FormatMetadata(FileMetadata? metadata, string key)
    {
        var field = ExifFields.Find(key) ?? throw new FormatException($"El campo de plantilla {{{key}}} no existe.");
        var value = metadata is null ? "" : field.ReadValue(metadata);
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"El campo «{field.Label}» no está disponible en este archivo.");

        if (key == "exposicion")
            value = value.Replace('/', '-');

        var cleaned = CleanMetadata(value);
        if (cleaned.Length == 0)
            throw new FormatException($"El campo «{field.Label}» no contiene texto válido para un nombre de archivo.");

        return cleaned;
    }

    private static string FormatLocation(string value)
    {
        var cleaned = CleanMetadata(value);
        if (cleaned.Length == 0)
            throw new FormatException("Escribe un lugar para usar el campo {lugar}.");

        return cleaned;
    }

    private static string FormatLocationPart(string value, string label)
    {
        var cleaned = CleanMetadata(value);
        if (cleaned.Length == 0)
            throw new FormatException($"El campo «{label}» no está definido para este lote.");

        return cleaned;
    }

    private static string CleanMetadata(string value) =>
        string.Concat(value.Trim().Select(character => InvalidNameCharacters.Contains(character) ? '_' : character))
            .TrimEnd('.', ' ');

    private static string FullFilePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path).Length == 0)
            throw new IOException("La ruta del archivo debe ser absoluta y contener un nombre.");

        return Path.GetFullPath(path);
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0)
            throw new FormatException("Escribe un nombre para el archivo.");
        if (name.Length > 255)
            throw new FormatException("El nombre supera los 255 caracteres.");
        if (name.IndexOfAny(InvalidNameCharacters) >= 0)
            throw new FormatException("El nombre contiene caracteres que Windows no admite.");
        if (name.EndsWith('.') || name.EndsWith(' '))
            throw new FormatException("Windows no admite nombres que terminan en punto o espacio.");
        if (ReservedNames.IsMatch(name.Split('.', 2)[0].TrimEnd(' ')))
            throw new FormatException("El nombre está reservado por Windows.");
    }

    private static void CheckDestination(string path, IReadOnlyDictionary<string, FileStamp> movingSources)
    {
        if (!Path.Exists(path))
            return;

        if (movingSources.TryGetValue(path, out var original) && !Directory.Exists(path) &&
            FileStamp.Capture(path) == original)
            return;

        throw new IOException($"El nombre «{Path.GetFileName(path)}» ya está ocupado en la carpeta.");
    }

    private static void AddIndex(Dictionary<string, List<int>> groups, string path, int index)
    {
        if (!groups.TryGetValue(path, out var indices))
            groups.Add(path, indices = []);
        indices.Add(index);
    }

    private static RenameRow Error(FileItem file, string detail) => new(file, "", RenameStatus.Error, detail);

    private static RenameRow WithError(RenameRow row, string detail) => row with { Status = RenameStatus.Error, Detail = detail };

    private static IReadOnlyList<RenameMove> Execute(IReadOnlyList<RenameMove> moves)
    {
        var normalized = new List<RenameMove>(moves.Count);
        var sources = new Dictionary<string, FileStamp>(PathComparer);
        var destinations = new HashSet<string>(PathComparer);
        foreach (var move in moves)
        {
            var source = FullFilePath(move.Source);
            var destination = FullFilePath(move.Destination);
            if (!PathComparer.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination)))
                throw new IOException("La carpeta de salida debe estar en la misma unidad que el archivo de origen.");

            ValidateName(Path.GetFileName(destination));
            if (StringComparer.Ordinal.Equals(source, destination))
                continue;

            var current = FileStamp.Capture(source);
            VerifyUnchanged(source, move.Snapshot, current);
            if (!sources.TryAdd(source, current) || !destinations.Add(destination))
                throw new IOException("Hay nombres de origen o de destino repetidos en el lote.");

            normalized.Add(new RenameMove(source, destination) { Snapshot = current });
        }

        foreach (var move in normalized)
            CheckDestination(move.Destination, sources);

        var staged = new List<StagedMove>(normalized.Count);
        try
        {
            foreach (var move in normalized)
            {
                var temporary = Path.Combine(Path.GetDirectoryName(move.Source)!, $".renombra-{Guid.NewGuid():N}.tmp");
                var entry = new StagedMove(move, temporary, FileStamp.Open(move.Source));
                staged.Add(entry);
                VerifyUnchanged(move.Source, move.Snapshot, FileStamp.Read(entry.Handle, move.Source));
                File.Move(move.Source, entry.Temporary, overwrite: false);
                entry.Location = MoveLocation.Temporary;
            }

            foreach (var entry in staged)
            {
                MoveTracked(entry, entry.Temporary, entry.Move.Destination);
                entry.Location = MoveLocation.Destination;
            }

            return staged.Select(entry => entry.Move with
            {
                Snapshot = FileStamp.Read(entry.Handle, entry.Move.Destination)
            }).ToArray();
        }
        catch (Exception error) when (IsFileError(error))
        {
            var recovery = RollBack(staged);
            var result = recovery.Count == 0
                ? "Se restauraron los nombres anteriores."
                : $"Quedaron archivos pendientes de restaurar:{Environment.NewLine}{string.Join(Environment.NewLine, recovery)}";
            throw new IOException($"El lote no se completó. {error.Message}{Environment.NewLine}{result}", error);
        }
        finally
        {
            foreach (var entry in staged)
                entry.Handle.Dispose();
        }
    }

    private static List<string> RollBack(List<StagedMove> staged)
    {
        var failures = new List<string>();
        for (var index = staged.Count - 1; index >= 0; index--)
        {
            var entry = staged[index];
            if (entry.Location == MoveLocation.Destination)
                Restore(entry, entry.Move.Destination, entry.Temporary, MoveLocation.Temporary, failures);
        }

        for (var index = staged.Count - 1; index >= 0; index--)
        {
            var entry = staged[index];
            if (entry.Location == MoveLocation.Temporary)
                Restore(entry, entry.Temporary, entry.Move.Source, MoveLocation.Source, failures);
        }

        return failures;
    }

    private static void Restore(StagedMove entry, string from, string to, MoveLocation location, List<string> failures)
    {
        try
        {
            MoveTracked(entry, from, to);
            entry.Location = location;
        }
        catch (Exception error) when (IsFileError(error))
        {
            failures.Add($"«{from}» → «{entry.Move.Source}». {error.Message}");
        }
    }

    private static void MoveTracked(StagedMove entry, string from, string to)
    {
        VerifyUnchanged(from, FileStamp.Read(entry.Handle, from), FileStamp.Capture(from));
        File.Move(from, to, overwrite: false);
    }

    private static void VerifyUnchanged(string path, FileStamp? expected, FileStamp current)
    {
        if (expected is not null && expected.Value != current)
            throw new IOException($"El archivo «{path}» cambió desde la última comprobación. Vuelve a cargarlo antes de aplicar cambios.");
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private enum MoveLocation { Source, Temporary, Destination }

    private sealed class StagedMove(RenameMove move, string temporary, SafeFileHandle handle)
    {
        public RenameMove Move { get; } = move;
        public string Temporary { get; } = temporary;
        public SafeFileHandle Handle { get; } = handle;
        public MoveLocation Location { get; set; }
    }
}

internal readonly record struct FileStamp(uint Volume, ulong FileId, ulong Length, ulong Created, ulong Modified)
{
    private const uint ShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;

    public static FileStamp Capture(string path)
    {
        using var handle = Open(path);
        return Read(handle, path);
    }

    public static SafeFileHandle Open(string path)
    {
        // Zero access opens metadata only. Opening the reparse point tracks the entry
        // that File.Move will rename, including links with an unavailable target.
        var handle = CreateFileW(ExtendedPath(path), 0, ShareReadWriteDelete,
            IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw AccessError(path, error);
    }

    public static FileStamp Read(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw AccessError(path, Marshal.GetLastWin32Error());
        if ((info.Attributes & FileAttributes.Directory) != 0)
            throw new IOException($"«{path}» es una carpeta. Añade los archivos que contiene.");

        return new FileStamp(info.Volume, Combine(info.IdHigh, info.IdLow),
            Combine(info.LengthHigh, info.LengthLow), Combine(info.CreatedHigh, info.CreatedLow),
            Combine(info.ModifiedHigh, info.ModifiedLow));
    }

    private static IOException AccessError(string path, int code) =>
        new($"No se pudo comprobar «{path}». {new Win32Exception(code).Message}");

    private static ulong Combine(uint high, uint low) => ((ulong)high << 32) | low;

    private static string ExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public FileAttributes Attributes;
        public uint CreatedLow;
        public uint CreatedHigh;
        public uint AccessedLow;
        public uint AccessedHigh;
        public uint ModifiedLow;
        public uint ModifiedHigh;
        public uint Volume;
        public uint LengthHigh;
        public uint LengthLow;
        public uint Links;
        public uint IdHigh;
        public uint IdLow;
    }
}
