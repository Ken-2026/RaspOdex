using System.Diagnostics;
using System.IO;
using System.Text;

namespace Renombra.Core;

internal static class PhotoOcr
{
    internal static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tesseract.exe");
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe");
        var executable = File.Exists(bundled) ? bundled : system;
        if (!File.Exists(executable)) throw new IOException("Falta Tesseract para leer texto; desmarca OCR o instala el componente.");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        process.StartInfo.ArgumentList.Add(path); process.StartInfo.ArgumentList.Add("stdout");
        if (executable == bundled)
        { process.StartInfo.ArgumentList.Add("--tessdata-dir"); process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tessdata")); }
        foreach (var arg in new[] { "-l", "spa+eng", "--psm", "11" }) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("No se pudo iniciar el OCR.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } token.ThrowIfCancellationRequested(); throw new IOException("El OCR tardó demasiado."); }
        var text = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("El OCR no pudo leer la imagen: " + (error.Length > 300 ? error[..300] : error));
        return text;
    }
}
