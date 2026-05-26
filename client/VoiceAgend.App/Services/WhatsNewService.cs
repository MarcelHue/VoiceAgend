using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace VoiceAgend.App.Services;

/// <summary>
/// Holt die letzten Releases vom GitHub-Repo, damit "What's New" nach einem Update
/// die wichtigsten Änderungen zeigen kann.
/// </summary>
public sealed class WhatsNewService
{
    public sealed record ReleaseEntry(string TagName, string Title, DateTime PublishedAt, string Body, string HtmlUrl);

    /// <summary>
    /// Repo-URL (z. B. "https://github.com/owner/repo"). Wird primär aus dem AssemblyMetadata
    /// 'UpdateRepoUrl' gelesen (vom Build-Workflow injiziert), fällt sonst auf den bekannten
    /// Default zurück, damit auch Dev-Builds die "What's New"-Liste laden können.
    /// </summary>
    public static string RepoUrl
    {
        get
        {
            var injected = typeof(WhatsNewService).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value;
            return !string.IsNullOrWhiteSpace(injected)
                ? injected!
                : "https://github.com/MarcelHue/VoiceAgend";
        }
    }

    public static (string Owner, string Repo)? ParseOwnerRepo()
    {
        try
        {
            var uri = new Uri(RepoUrl);
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 2) return (parts[0], parts[1]);
        }
        catch { }
        return null;
    }

    public async Task<IReadOnlyList<ReleaseEntry>> FetchLatestReleasesAsync(int count = 2, CancellationToken ct = default)
    {
        var or = ParseOwnerRepo();
        if (or is null) return Array.Empty<ReleaseEntry>();
        var (owner, repo) = or.Value;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceAgend-WhatsNew/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={Math.Max(1, count)}";

        try
        {
            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var list = new List<ReleaseEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var bodyRaw = el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var pubStr = el.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                var htmlUrl = el.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
                var draft = el.TryGetProperty("draft", out var dr) && dr.GetBoolean();
                if (draft) continue;
                if (!DateTime.TryParse(pubStr, out var pub)) pub = DateTime.MinValue;

                // Velopack erstellt Releases ohne Body — GitHub zeigt im Web stattdessen
                // die Commit-Message des Tags an. Wir machen es genauso: leerer Body →
                // Commit-Message holen.
                if (string.IsNullOrWhiteSpace(bodyRaw) && !string.IsNullOrWhiteSpace(tag))
                {
                    bodyRaw = await FetchCommitMessageForTagAsync(http, owner, repo, tag, ct);
                }

                list.Add(new ReleaseEntry(
                    TagName: tag,
                    Title: string.IsNullOrWhiteSpace(name) ? tag : name,
                    PublishedAt: pub,
                    Body: bodyRaw,
                    HtmlUrl: htmlUrl));
                if (list.Count >= count) break;
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn("WhatsNew fetch failed: " + ex.Message);
            return Array.Empty<ReleaseEntry>();
        }
    }

    private static async Task<string> FetchCommitMessageForTagAsync(
        HttpClient http, string owner, string repo, string tag, CancellationToken ct)
    {
        try
        {
            // GET /commits/{ref} akzeptiert Tag-Namen direkt als ref.
            var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(tag)}";
            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("commit", out var commit)
                && commit.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"WhatsNew commit-fallback for {tag}: {ex.Message}");
        }
        return "";
    }

    /// <summary>Erste nicht-leere, nicht-Bullet-Zeile — z. B. die Commit-Subject-Zeile.</summary>
    public static string ExtractHeadline(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ ")) return "";
            if (line.StartsWith("#")) line = line.TrimStart('#').Trim();
            return line;
        }
        return "";
    }

    /// <summary>Stripped Markdown → einfache Liste mit Bullet-Items für die UI.
    /// Eingerückte Folgezeilen werden an den vorherigen Bullet angehängt, sodass
    /// mehrzeilige Bullet-Punkte (typisch in Commit-Messages) zusammenbleiben.</summary>
    public static IReadOnlyList<string> ExtractBulletLines(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var bullets = new List<string>();
        foreach (var raw in lines)
        {
            var rightTrimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(rightTrimmed)) continue;
            var leadingWs = raw.Length - raw.TrimStart().Length;
            var trimmed = rightTrimmed.TrimStart();

            // Neuer Bullet
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                bullets.Add(trimmed[2..].Trim());
                continue;
            }
            if (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.' && trimmed[2] == ' ')
            {
                bullets.Add(trimmed[3..].Trim());
                continue;
            }
            // Eingerückte Folgezeile → an letzten Bullet anhängen
            if (leadingWs > 0 && bullets.Count > 0)
            {
                bullets[^1] = bullets[^1] + " " + trimmed;
            }
        }
        // Wenn keine Bullets — den ganzen Text als einen Eintrag durchreichen (auf 240 Zeichen gekürzt)
        if (bullets.Count == 0)
        {
            var plain = body.Replace("\r", "").Trim();
            if (plain.Length > 0)
                bullets.Add(plain.Length > 240 ? plain[..240] + "…" : plain);
        }
        return bullets;
    }

    public static string CurrentVersion()
    {
        var v = typeof(WhatsNewService).Assembly.GetName().Version;
        return v?.ToString(3) ?? "0.0.0";
    }
}
