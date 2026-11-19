using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Renombra.Core;

internal static class LocalAiSetup
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromSeconds(8) };

    internal static async Task PrepareAsync(string model, Action<string> progress)
    {
        model = model switch { "qwen3:0.6b" => model, "qwen3.5:0.8b" => model, _ => "qwen3:1.7b" };
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (!File.Exists(exe))
        {
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(winget)) throw new IOException("No encuentro el instalador de aplicaciones de Windows (winget). Instala Ollama desde ollama.com y vuelve a intentarlo.");
            progress("Instalando Ollama desde su distribuidor oficial; puede tardar varios minutos…");
            await RunAsync(winget, ["install", "--id", "Ollama.Ollama", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"]);
            if (!File.Exists(exe)) throw new IOException("Ollama no apareció en la carpeta esperada. Comprueba su instalación y vuelve a intentarlo.");
        }
        progress("Comprobando el servicio de IA local…");
        try
        {
            using var response = await Client.GetAsync("api/tags");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("models").EnumerateArray().Any(x => x.TryGetProperty("name", out var name) && name.GetString() == model)) return;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new IOException("Ollama está instalado, pero su servicio no responde. Abre Ollama y vuelve a pulsar Preparar IA.", e); }
        progress("Descargando el modelo " + model + "; puede tardar varios minutos…");
        await RunAsync(exe, ["pull", model]);
    }

    private static async Task RunAsync(string executable, string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("No se pudo iniciar el instalador.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new IOException("La instalación tardó demasiado."); }
        await stdout;
        var detail = await stderr;
        if (process.ExitCode != 0) throw new IOException("No se completó la instalación. " + (detail.Length > 400 ? detail[^400..] : detail));
    }
}
