using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace StrikeEQ.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string? ReleaseUrl,
    string? DirectExeDownloadUrl,
    string Message);

/// <summary>
/// Checks GitHub Releases API for newer versions of FootStep EQ and supports
/// 1-click automatic download, PE validation, self-replacement, and relaunch.
/// </summary>
public sealed class GitHubUpdateService
{
    private static readonly HttpClient Http = CreateClient();

    public string RepositorySlug { get; set; } = "santiniss/FootStep-EQ";

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FootStepEQ", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        string current = CurrentVersion;
        if (string.IsNullOrWhiteSpace(RepositorySlug) || !RepositorySlug.Contains('/'))
        {
            return new UpdateCheckResult(false, false, current, current, null, null,
                "Repositório GitHub ainda não configurado (ex.: santiniss/FootStep-EQ).");
        }

        try
        {
            string url = $"https://api.github.com/repos/{RepositorySlug.Trim()}/releases/latest";
            using var resp = await Http.GetAsync(url);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(true, false, current, current,
                    $"https://github.com/{RepositorySlug.Trim()}/releases", null,
                    $"Você está na v{current} (nenhuma release publicada no GitHub ainda).");
            }

            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? (tagEl.GetString() ?? current)
                : current;
            string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? (urlEl.GetString() ?? $"https://github.com/{RepositorySlug.Trim()}/releases")
                : $"https://github.com/{RepositorySlug.Trim()}/releases";

            string? directExeUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    string? dlUrl = asset.TryGetProperty("browser_download_url", out var dEl) ? dEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(dlUrl))
                    {
                        directExeUrl = dlUrl;
                        break;
                    }
                }
            }

            string cleanLatest = tag.TrimStart('v', 'V');
            bool newer = IsNewerVersion(cleanLatest, current);

            string msg = newer
                ? $"Nova versão disponível: v{cleanLatest} (atual: v{current})!"
                : $"FootStep EQ já está atualizado (v{current}).";

            return new UpdateCheckResult(true, newer, current, cleanLatest, htmlUrl, directExeUrl, msg);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, current, current, null, null,
                $"Não foi possível verificar atualizações: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the new FootStepEQ.exe asset to %TEMP%, validates its PE ("MZ") header,
    /// and schedules a clean swap + automatic relaunch script.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndApplyUpdateAsync(
        string downloadUrl,
        string targetVersion,
        IProgress<double>? progress = null)
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return (false, "Não foi possível localizar o executável atual do FootStep EQ.");

        string safeVer = string.Concat(targetVersion.Where(c => char.IsLetterOrDigit(c) || c == '.'));
        string tempExe = Path.Combine(Path.GetTempPath(), $"FootStepEQ_update_{safeVer}_{Guid.NewGuid():N}.exe");

        try
        {
            using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            await using (var remoteStream = await resp.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int bytesRead;
                while ((bytesRead = await remoteStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    readTotal += bytesRead;
                    if (totalBytes > 0)
                        progress?.Report(Math.Clamp((double)readTotal / totalBytes * 100.0, 0, 100));
                }
            }

            // Validate downloaded file is a real Windows PE executable ('MZ' signature, >= 100 KB)
            var fi = new FileInfo(tempExe);
            if (!fi.Exists || fi.Length < 100_000)
            {
                TryDelete(tempExe);
                return (false, "O arquivo baixado parece incompleto. Tente novamente.");
            }

            byte[] header = new byte[2];
            using (var checkFs = File.OpenRead(tempExe))
            {
                if (checkFs.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                {
                    TryDelete(tempExe);
                    return (false, "O arquivo baixado não é um executável Windows válido.");
                }
            }

            progress?.Report(100.0);

            // Launch self-replacing updater script that waits for this PID to exit, swaps the EXE, and restarts it
            int pid = Environment.ProcessId;
            string scriptPath = Path.Combine(Path.GetTempPath(), $"footstepeq_updater_{Guid.NewGuid():N}.cmd");
            string script =
                "@echo off\r\n" +
                ":waitloop\r\n" +
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find /I \"{pid}\" >NUL\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >NUL\r\n" +
                "  goto waitloop\r\n" +
                ")\r\n" +
                "timeout /t 1 /nobreak >NUL\r\n" +
                $"move /y \"{tempExe}\" \"{currentExe}\" >NUL\r\n" +
                "if errorlevel 1 (\r\n" +
                $"  copy /y \"{tempExe}\" \"{currentExe}\" >NUL\r\n" +
                $"  del /f /q \"{tempExe}\" >NUL 2>&1\r\n" +
                ")\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                "del /f /q \"%~f0\" >NUL 2>&1\r\n";

            await File.WriteAllTextAsync(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            return (true, $"Atualização v{targetVersion} baixada! Reiniciando o FootStep EQ...");
        }
        catch (Exception ex)
        {
            TryDelete(tempExe);
            return (false, $"Falha ao baixar atualização: {ex.Message}");
        }
    }

    public static bool IsNewerVersion(string candidate, string current)
    {
        if (Version.TryParse(Normalize(candidate), out var vCand) &&
            Version.TryParse(Normalize(current), out var vCurr))
        {
            return vCand > vCurr;
        }
        return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('-', '+')[0].Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => string.Join('.', parts.Take(3))
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
