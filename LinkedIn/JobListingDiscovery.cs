using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

public static class JobListingDiscovery
{
    public const string CardSelector =
        "div.job-card-container, li[data-occludable-job-id], [data-job-id], [data-occludable-job-id], li.jobs-search-results__list-item, [data-view-name='job-card'], [class*='job-card'], [class*='jobCard'], li, article, [role='listitem'], [role='option'], [role='button'], [tabindex='0']";

    private static readonly Regex JobIdFromUrl = new(
        @"/jobs/view/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? ExtractJobId(
        string? dataJobId,
        string? occludableJobId,
        IEnumerable<string?> jobLinks)
    {
        if (IsJobId(dataJobId))
        {
            return dataJobId!.Trim();
        }

        if (IsJobId(occludableJobId))
        {
            return occludableJobId!.Trim();
        }

        foreach (var href in jobLinks)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var match = JobIdFromUrl.Match(href);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    public static string? ExtractCurrentJobId(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var currentJobId = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .FirstOrDefault(parts =>
                Uri.UnescapeDataString(parts[0]).Equals("currentJobId", StringComparison.OrdinalIgnoreCase));
        if (currentJobId is null || currentJobId.Length != 2)
        {
            return null;
        }

        var value = Uri.UnescapeDataString(currentJobId[1]);
        return IsJobId(value) ? value.Trim() : null;
    }

    public static string CreateIdentity(string? jobId, string visibleText)
    {
        if (IsJobId(jobId))
        {
            return $"job:{jobId!.Trim()}";
        }

        var normalizedText = Regex.Replace(visibleText, @"\s+", " ").Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)));
        return $"listing:{hash}";
    }

    public static bool IsLeftPaneCandidate(
        double x,
        double y,
        double width,
        double height,
        double viewportWidth,
        double viewportHeight,
        string visibleText)
    {
        return !string.IsNullOrWhiteSpace(visibleText)
            && x >= 0
            && x < viewportWidth * 0.55
            && y < viewportHeight
            && width >= 180
            && height >= 45
            && height <= 520;
    }

    private static bool IsJobId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsDigit);
}
