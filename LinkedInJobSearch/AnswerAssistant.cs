using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record ConfirmedAnswer(
    string Question,
    string Answer,
    DateTimeOffset ConfirmedAtUtc);

public enum AnswerSuggestionKind
{
    None,
    PreviousAnswer,
    ResumeAnswer,
    ResumeAnswerUnavailable,
    ResumeExcerpt,
    SensitiveQuestion,
    AmbiguousPreviousAnswers,
    AmbiguousResumeExcerpts
}

public sealed record AnswerSuggestion(
    AnswerSuggestionKind Kind,
    string? Answer,
    double Similarity,
    string? MatchedQuestion = null,
    IReadOnlyList<ConfirmedAnswer>? ConflictingAnswers = null);

public static class AnswerMatcher
{
    public const double PreviousAnswerThreshold = 0.50;
    public const double ResumeExcerptThreshold = 0.20;
    public const double AmbiguityMargin = 0.10;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex SensitivePattern = new(
        @"\b(work authorization|employment authorization|authorized to work|eligible to work|right to work|work permit|visa|immigration|sponsor(?:ship)?|citizenship|nationality|social security|ssn|disability|disabled|veteran|military service|criminal|convict(?:ion|ed|ions)|background check|clearance|salary|compensation|pay range|pay expectations|expected pay|desired pay|hourly rate|race|ethnicity|gender|sex|sexual orientation|religion|marital status|age|date of birth|demographic|equal employment|eeo|protected class|email|phone|mobile|telephone|street address|home address|contact information)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "by", "can",
        "could", "did", "do", "does", "for", "from", "had", "has", "have", "how",
        "i", "in", "into", "is", "it", "me", "of", "on", "or", "our", "please",
        "role", "the", "their", "them", "there", "this", "to", "us", "was", "we",
        "were", "what", "when", "where", "which", "who", "why", "will", "with",
        "would", "you", "your"
    };

    public static bool IsSensitive(string question) => SensitivePattern.IsMatch(question);

    public static string Normalize(string text)
    {
        var tokens = WordPattern.Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => !StopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    public static double Similarity(string left, string right)
    {
        var leftTokens = Normalize(left).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = Normalize(right).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return (double)intersection / union;
    }

    public static AnswerSuggestion FindPreviousAnswer(
        string question,
        IReadOnlyCollection<ConfirmedAnswer> confirmedAnswers)
    {
        if (IsSensitive(question))
        {
            return new AnswerSuggestion(AnswerSuggestionKind.SensitiveQuestion, null, 0);
        }

        var matches = confirmedAnswers
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Question) &&
                !string.IsNullOrWhiteSpace(item.Answer))
            .Where(item => !IsSensitive(item.Question))
            .Select(item => new { Item = item, Score = Similarity(question, item.Question) })
            .Where(match => match.Score >= PreviousAnswerThreshold)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => Normalize(match.Item.Question), StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
        {
            return new AnswerSuggestion(AnswerSuggestionKind.None, null, 0);
        }

        var best = matches[0];
        var competingAnswers = matches
            .Where(match => best.Score - match.Score <= AmbiguityMargin)
            .Select(match => match.Item)
            .GroupBy(item => Normalize(item.Answer), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (competingAnswers.Count > 1)
        {
            return new AnswerSuggestion(
                AnswerSuggestionKind.AmbiguousPreviousAnswers,
                null,
                best.Score,
                ConflictingAnswers: competingAnswers);
        }

        return new AnswerSuggestion(
            AnswerSuggestionKind.PreviousAnswer,
            best.Item.Answer,
            best.Score,
            best.Item.Question);
    }

    public static AnswerSuggestion FindResumeExcerpt(string question, string resumeText)
    {
        if (IsSensitive(question))
        {
            return new AnswerSuggestion(AnswerSuggestionKind.SensitiveQuestion, null, 0);
        }

        var excerpts = Regex.Split(resumeText, @"(?<=[.!?])\s+|\r?\n")
            .Select(excerpt => excerpt.Trim().Trim('-', '*', '\u2022', ' '))
            .Where(excerpt => !IsSensitive(excerpt))
            .Where(excerpt => excerpt.Length > 0)
            .Select(excerpt => new { Text = excerpt, Score = Similarity(question, excerpt) })
            .Where(match => match.Score >= ResumeExcerptThreshold)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => Normalize(match.Text), StringComparer.Ordinal)
            .ToList();
        if (excerpts.Count == 0)
        {
            return new AnswerSuggestion(AnswerSuggestionKind.None, null, 0);
        }

        var best = excerpts[0];
        var tiedExcerpts = excerpts
            .Where(match => best.Score - match.Score <= AmbiguityMargin)
            .Select(match => match.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tiedExcerpts.Count > 1)
        {
            return new AnswerSuggestion(
                AnswerSuggestionKind.AmbiguousResumeExcerpts,
                null,
                best.Score);
        }

        return new AnswerSuggestion(AnswerSuggestionKind.ResumeExcerpt, best.Text, best.Score);
    }

    public static AnswerSuggestion FindResumeAnswer(
        string question,
        IReadOnlyDictionary<string, string> resumeAnswers)
    {
        if (IsSensitive(question))
        {
            return new AnswerSuggestion(AnswerSuggestionKind.SensitiveQuestion, null, 0);
        }

        var matchingKeys = ResumeAnswerQuestionKeys
            .Where(item => item.Pattern.IsMatch(question))
            .Select(item => item.Key)
            .ToList();
        if (matchingKeys.Count == 0)
        {
            return new AnswerSuggestion(AnswerSuggestionKind.None, null, 0);
        }

        var matches = matchingKeys
            .Select(key => resumeAnswers.TryGetValue(key, out var answer) ? answer : null)
            .Where(answer => !string.IsNullOrWhiteSpace(answer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count == 1
            ? new AnswerSuggestion(AnswerSuggestionKind.ResumeAnswer, matches[0], 1)
            : new AnswerSuggestion(AnswerSuggestionKind.ResumeAnswerUnavailable, null, 0);
    }

    private static readonly (Regex Pattern, string Key)[] ResumeAnswerQuestionKeys =
    [
        (
            new Regex(
                @"how many years of professional software engineering experience",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "professional_software_engineering_years"
        ),
        (
            new Regex(
                @"how many years of hands-on microsoft azure experience",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "microsoft_azure_years"
        ),
        (
            new Regex(
                @"do you have professional experience developing applications with c#/.net",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "csharp_dotnet_experience"
        ),
        (
            new Regex(
                @"do you have client-facing technical consulting or pre-sales experience",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "client_facing_technical_consulting_or_presales"
        ),
        (
            new Regex(
                @"have you personally designed and deployed production software systems",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "production_software_systems_experience"
        )
    ];
}

public static class ManualAnswerWindow
{
    public static readonly TimeSpan IdleDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static bool HasBeenIdleFor(TimeSpan inactivity, TimeSpan requiredDuration) =>
        inactivity >= requiredDuration;

    public static bool ShouldWaitForQuestionPage(
        bool hasQuestionFields,
        bool hasQuestionSection,
        bool isResumeSelectionPage,
        bool isReviewPage = false) =>
        hasQuestionSection || (!isResumeSelectionPage && !isReviewPage && hasQuestionFields);

    public static bool ShouldReviewQuestionPage(
        bool isQuestionPage,
        int visibleQuestionCount,
        bool modalIsVisible) =>
        isQuestionPage && visibleQuestionCount > 0 && modalIsVisible;

    public static bool HasAnswer(string fieldType, string? value, bool? isChecked = null) =>
        fieldType == "checkbox"
            ? isChecked.HasValue
            : !string.IsNullOrWhiteSpace(value);

    public static bool IsUnchanged(string? before, string? after) =>
        string.Equals(before, after, StringComparison.Ordinal);

    public static bool ShouldSuggestAnswer(string? currentAnswer, string? suggestion) =>
        string.IsNullOrWhiteSpace(currentAnswer) && !string.IsNullOrWhiteSpace(suggestion);
}

public enum ApplyButtonKind
{
    None,
    EasyApply,
    ExternalApplyLink,
    Apply
}

public static class ApplyButtonHandling
{
    public static ApplyButtonKind Select(int easyApplyCount, int externalApplyLinkCount, int applyCount)
    {
        if (easyApplyCount == 1)
        {
            return ApplyButtonKind.EasyApply;
        }

        if (easyApplyCount > 1)
        {
            return ApplyButtonKind.None;
        }

        if (externalApplyLinkCount == 1)
        {
            return ApplyButtonKind.ExternalApplyLink;
        }

        if (externalApplyLinkCount > 1)
        {
            return ApplyButtonKind.None;
        }

        return applyCount == 1 ? ApplyButtonKind.Apply : ApplyButtonKind.None;
    }

    public static Uri? ResolveDestination(Uri currentPage, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) ||
            !Uri.TryCreate(currentPage, href, out var destination))
        {
            return null;
        }

        if (destination.Host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase) ||
            destination.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase))
        {
            var redirect = destination.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(parameter => parameter.Split('=', 2))
                .FirstOrDefault(parts =>
                    Uri.UnescapeDataString(parts[0].Replace('+', ' ')).Equals("url", StringComparison.OrdinalIgnoreCase));
            if (redirect is { Length: 2 })
            {
                var target = Uri.UnescapeDataString(redirect[1].Replace('+', ' '));
                if (Uri.TryCreate(target, UriKind.Absolute, out var resolved))
                {
                    destination = resolved;
                }
            }
        }

        return destination;
    }

    public static bool IsExternalApplyLink(
        Uri currentPage,
        string? applySpanText,
        string? href)
    {
        var destination = ResolveDestination(currentPage, href);
        return IsApplySpanText(applySpanText) && destination is not null &&
            !string.Equals(
                currentPage.GetLeftPart(UriPartial.Authority),
                destination.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsApplySpanText(string? text) =>
        string.Equals(text?.Trim(), "Apply", StringComparison.Ordinal);

    public static bool IsExternalDestination(Uri currentPage, string? href)
    {
        var destination = ResolveDestination(currentPage, href);
        return destination is not null &&
            !string.Equals(
                currentPage.GetLeftPart(UriPartial.Authority),
                destination.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldContinueAfterManualHandoff(bool enterReceived, bool sourcePageIsClosed) =>
        enterReceived && !sourcePageIsClosed;

    public static bool IsSafeExternalDestination(Uri destination) =>
        destination.Scheme == Uri.UriSchemeHttps;
}

public sealed class ConfirmedAnswerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<ConfirmedAnswer> entries;

    private ConfirmedAnswerStore(List<ConfirmedAnswer> entries) => this.entries = entries;

    public IReadOnlyList<ConfirmedAnswer> Entries => entries;

    public static async Task<ConfirmedAnswerStore> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new ConfirmedAnswerStore([]);
        }

        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<List<ConfirmedAnswer>>(stream, JsonOptions);
        return new ConfirmedAnswerStore(loaded ?? []);
    }

    public async Task AddAndSaveAsync(string path, string question, string answer)
    {
        entries.Add(new ConfirmedAnswer(question.Trim(), answer.Trim(), DateTimeOffset.UtcNow));
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed class ResumeAnswerStore
{
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.Ordinal)
    {
        "professional_software_engineering_years",
        "microsoft_azure_years",
        "csharp_dotnet_experience",
        "client_facing_technical_consulting_or_presales",
        "production_software_systems_experience"
    };

    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly Dictionary<string, string> answers;

    private ResumeAnswerStore(Dictionary<string, string> answers) => this.answers = answers;

    public static async Task<ResumeAnswerStore> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new ResumeAnswerStore(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions);
        var answers = loaded ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownKeys = answers.Keys.Where(key => !SupportedKeys.Contains(key)).OrderBy(key => key).ToList();
        if (unknownKeys.Count > 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} contains unsupported answer key(s): {string.Join(", ", unknownKeys)}.");
        }

        return new ResumeAnswerStore(new Dictionary<string, string>(answers, StringComparer.Ordinal));
    }

    public AnswerSuggestion FindAnswer(string question) =>
        AnswerMatcher.FindResumeAnswer(question, answers);
}
