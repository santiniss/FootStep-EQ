using System.Diagnostics;
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
    string Message);

/// <summary>
/// Checks GitHub Releases API for newer versions of StrikeEQ.
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
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FootStepEQ", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        string current = CurrentVersion;
        if (string.IsNullOrWhiteSpace(RepositorySlug) || !RepositorySlug.Contains('/'))
        {
            return new UpdateCheckResult(false, false, current, current, null,
                "Repositório GitHub ainda não configurado (ex.: santiniss/FootStep-EQ).");
        }

        try
        {
            string url = $"https://api.github.com/repos/{RepositorySlug.Trim()}/releases/latest";
            using var resp = await Http.GetAsync(url);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(true, false, current, current,
                    $"https://github.com/{RepositorySlug.Trim()}/releases",
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

            string cleanLatest = tag.TrimStart('v', 'V');
            bool newer = IsNewerVersion(cleanLatest, current);

            string msg = newer
                ? $"Nova versão disponível: v{cleanLatest} (atual: v{current})!"
                : $"FootStep EQ já está atualizado (v{current}).";

            return new UpdateCheckResult(true, newer, current, cleanLatest, htmlUrl, msg);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, current, current, null,
                $"Não foi possível verificar atualizações: {ex.Message}");
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

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
