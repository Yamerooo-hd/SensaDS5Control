using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SensaDS5Control.Core;

public sealed record UpdateCheckResult(string TagName, string? Uf2DownloadUrl, string? AppZipDownloadUrl, string? Notes);

public static class GitHubUpdater
{
    public const string RepoOwner = "Yamerooo-hd";
    public const string RepoName = "SensaDS5Control";

    public static async Task<UpdateCheckResult?> CheckLatestReleaseAsync(string? token, CancellationToken ct)
    {
        using var http = CreateClient(token);
        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        string? uf2Url = null;
        string? appUrl = null;

        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase)) uf2Url = dl;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) appUrl = dl;
            }
        }
        return new UpdateCheckResult(tag, uf2Url, appUrl, notes);
    }

    /// Fallback for private dev builds: fetches latest SensaBridge.uf2 directly from GitHub Actions artifacts if no Release exists.
    public static async Task<byte[]?> DownloadLatestFirmwareBytesAsync(string? token, CancellationToken ct)
    {
        var rel = await CheckLatestReleaseAsync(token, ct);
        using var http = CreateClient(token);
        if (rel?.Uf2DownloadUrl is { } dl)
        {
            return await http.GetByteArrayAsync(dl, ct);
        }
        if (string.IsNullOrWhiteSpace(token)) return null;

        var runsUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/actions/runs?per_page=10&status=success";
        using var runsDoc = JsonDocument.Parse(await http.GetStringAsync(runsUrl, ct));
        foreach (var run in runsDoc.RootElement.GetProperty("workflow_runs").EnumerateArray())
        {
            if (run.GetProperty("name").GetString() != "Build SensaBridge Firmware") continue;
            long id = run.GetProperty("id").GetInt64();
            using var artsDoc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/actions/runs/{id}/artifacts", ct));
            foreach (var art in artsDoc.RootElement.GetProperty("artifacts").EnumerateArray())
            {
                string zipUrl = art.GetProperty("archive_download_url").GetString()!;
                var zipBytes = await http.GetByteArrayAsync(zipUrl, ct);
                using var ms = new MemoryStream(zipBytes);
                using var zf = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zf.Entries.FirstOrDefault(e => e.Name.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;
                using var es = entry.Open();
                using var outMs = new MemoryStream();
                await es.CopyToAsync(outMs, ct);
                return outMs.ToArray();
            }
        }
        return null;
    }

    private static HttpClient CreateClient(string? token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SensaDS5Control", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token.Trim());
        return http;
    }
}
