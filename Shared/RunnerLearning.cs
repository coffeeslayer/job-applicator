using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

public enum LearnedActionKind
{
    Apply,
    Next,
    Submit,
    Dismiss
}

public enum BlockerResolutionKind
{
    ClickedLearnedButton,
    ManualHandled,
    AlreadySubmitted,
    ReloadProfile,
    SkipListing,
    SkipDomain
}

public sealed record BlockerResolution(
    BlockerResolutionKind Kind,
    string? LearnedLabel = null,
    string? Host = null);

public enum ErrorRecoveryAction
{
    Retry,
    MarkApplied,
    SkipListing,
    SkipDomain,
    Quit
}

public static class NoTimeoutPlaywright
{
    public static void Configure(IBrowserContext context)
    {
        context.SetDefaultTimeout(0);
        context.SetDefaultNavigationTimeout(0);
        foreach (var page in context.Pages)
        {
            Configure(page);
        }
        context.Page += (_, page) => Configure(page);
    }

    public static void Configure(IPage page)
    {
        page.SetDefaultTimeout(0);
        page.SetDefaultNavigationTimeout(0);
    }

    public static async Task SafeScrollIntoViewAsync(ILocator locator)
    {
        try
        {
            await locator.EvaluateAsync(
                "el => { if (el && typeof el.scrollIntoView === 'function') { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } }");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
        }
    }

    public static TaskCompletionSource TrackBrowserClose(IBrowserContext context)
    {
        var browserClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Close += (_, _) => browserClosed.TrySetResult();
        return browserClosed;
    }

    public static async Task KeepBrowserOpenUntilClosedAsync(
        IBrowserContext? context,
        TaskCompletionSource? browserClosed,
        string reason)
    {
        if (context is null || browserClosed is null || browserClosed.Task.IsCompleted)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{reason}");
        Console.WriteLine("Browser is being kept open (no timeout — it will never be closed automatically).");
        Console.WriteLine("Close the browser window manually when you are finished inspecting it to exit.");
        await browserClosed.Task;
    }

    public static async Task DumpPageDiagnosticsAsync(
        IPage? page,
        string projectRoot,
        string runnerName,
        string reason,
        IReadOnlyList<string>? unansweredQuestions = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        try
        {
            var logsDir = Path.Combine(projectRoot, "logs");
            Directory.CreateDirectory(logsDir);
            var safeRunner = Regex.Replace(runnerName.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(safeRunner))
            {
                safeRunner = "runner";
            }

            var diagnosticsPath = Path.Combine(logsDir, $"{safeRunner}-stall.json");
            object? pageSnapshot = null;
            string? url = null;
            string? title = null;

            if (page is not null && !page.IsClosed)
            {
                url = page.Url;
                try
                {
                    title = await page.TitleAsync();
                }
                catch (PlaywrightException)
                {
                }

                try
                {
                    pageSnapshot = await page.EvaluateAsync<JsonElement>(
                        """
                        () => {
                            const clean = text => (text || '').replace(/\s+/g, ' ').trim();
                            const isVisible = el => {
                                if (!el) return false;
                                const style = window.getComputedStyle(el);
                                if (style.display === 'none' || style.visibility === 'hidden') return false;
                                const rect = el.getBoundingClientRect();
                                return rect.width > 0 || rect.height > 0;
                            };

                            const buttons = Array.from(document.querySelectorAll('button, input[type="submit"], input[type="button"], [role="button"], a[href]'))
                                .filter(isVisible)
                                .slice(0, 60)
                                .map(el => ({
                                    tag: el.tagName.toLowerCase(),
                                    role: el.getAttribute('role') || '',
                                    type: el.getAttribute('type') || '',
                                    text: clean(el.innerText || el.value || el.getAttribute('aria-label') || '').slice(0, 120),
                                    disabled: Boolean(el.disabled || el.getAttribute('aria-disabled') === 'true'),
                                    testId: el.getAttribute('data-testid') || el.getAttribute('data-cy') || ''
                                }))
                                .filter(b => b.text.length > 0 || b.testId.length > 0);

                            const controls = Array.from(document.querySelectorAll('input, textarea, select, [role="combobox"], [role="radiogroup"], [role="radio"], [role="checkbox"]'))
                                .slice(0, 120)
                                .map(el => {
                                    const type = (el.getAttribute('type') || '').toLowerCase();
                                    if (type === 'hidden') return null;
                                    const rect = el.getBoundingClientRect();
                                    const visible = isVisible(el);
                                    const labels = el.labels ? Array.from(el.labels).map(l => clean(l.innerText)).filter(Boolean) : [];
                                    const ancestorLabel = el.closest('label') ? clean(el.closest('label').innerText) : '';
                                    const container = el.closest('fieldset, [role="radiogroup"], [role="group"], .field, .form-field, [class*="question" i], [class*="field" i]');
                                    return {
                                        tag: el.tagName.toLowerCase(),
                                        type,
                                        role: el.getAttribute('role') || '',
                                        name: el.getAttribute('name') || '',
                                        id: el.id || '',
                                        visible,
                                        width: Math.round(rect.width),
                                        height: Math.round(rect.height),
                                        disabled: Boolean(el.disabled || el.getAttribute('aria-disabled') === 'true'),
                                        required: Boolean(el.required || el.getAttribute('aria-required') === 'true'),
                                        invalid: Boolean(el.matches?.(':invalid') || el.getAttribute('aria-invalid') === 'true'),
                                        checked: Boolean(el.checked || el.getAttribute('aria-checked') === 'true'),
                                        value: typeof el.value === 'string' ? el.value.slice(0, 120) : '',
                                        ariaLabel: clean(el.getAttribute('aria-label') || ''),
                                        placeholder: clean(el.getAttribute('placeholder') || ''),
                                        labels,
                                        ancestorLabel: ancestorLabel.slice(0, 160),
                                        containerText: clean(container?.innerText || '').slice(0, 260)
                                    };
                                })
                                .filter(Boolean);

                            const errors = Array.from(document.querySelectorAll('[role="alert"], .error, [class*="error" i], [class*="invalid" i]'))
                                .filter(isVisible)
                                .map(el => clean(el.innerText))
                                .filter(t => t.length > 0)
                                .slice(0, 20);

                            return {
                                buttons,
                                controls,
                                errors,
                                bodySnippet: (document.body?.innerText || '').slice(0, 2500)
                            };
                        }
                        """);
                }
                catch (PlaywrightException ex)
                {
                    pageSnapshot = new { captureError = ex.Message };
                }
            }

            var payload = new
            {
                timestamp = DateTimeOffset.Now,
                runner = runnerName,
                reason,
                url,
                title,
                unansweredQuestions = unansweredQuestions ?? [],
                pageSnapshot
            };

            await File.WriteAllTextAsync(
                diagnosticsPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  [Diagnostics] Wrote stall snapshot to {diagnosticsPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [Diagnostics] Failed to write stall snapshot: {ex.Message}");
        }
    }

    public static async Task<bool> SafeCheckAsync(ILocator input, ILocator? labelFallback = null)
    {
        try
        {
            if (await input.IsCheckedAsync())
            {
                return true;
            }
        }
        catch (PlaywrightException)
        {
            return false;
        }

        try
        {
            var isVisuallyHiddenOrCovered = await input.EvaluateAsync<bool>(
                """
                el => {
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    if (style.opacity === '0' || style.visibility === 'hidden' || rect.width <= 2 || rect.height <= 2) {
                        return true;
                    }
                    const cx = rect.left + rect.width / 2;
                    const cy = rect.top + rect.height / 2;
                    const topEl = document.elementFromPoint(cx, cy);
                    if (!topEl) return false;
                    return topEl !== el && !el.contains(topEl);
                }
                """);

            if (!isVisuallyHiddenOrCovered && await input.IsVisibleAsync())
            {
                await SafeScrollIntoViewAsync(input);
                await input.CheckAsync(new LocatorCheckOptions { Force = true });
                return await input.IsCheckedAsync();
            }
        }
        catch (PlaywrightException)
        {
        }

        if (labelFallback is not null)
        {
            try
            {
                if (await labelFallback.CountAsync() > 0 && await labelFallback.First.IsVisibleAsync())
                {
                    await SafeScrollIntoViewAsync(labelFallback.First);
                    await labelFallback.First.ClickAsync(new LocatorClickOptions { Force = true });
                    if (await input.IsCheckedAsync())
                    {
                        return true;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        try
        {
            var associatedLabel = input.Locator("xpath=ancestor::label[1]");
            if (await associatedLabel.CountAsync() > 0 && await associatedLabel.First.IsVisibleAsync())
            {
                await associatedLabel.First.ClickAsync(new LocatorClickOptions { Force = true });
                if (await input.IsCheckedAsync())
                {
                    return true;
                }
            }
        }
        catch (PlaywrightException)
        {
        }

        try
        {
            await input.CheckAsync(new LocatorCheckOptions { Force = true });
            return await input.IsCheckedAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    public static async Task<string> SafeGetBodyTextAsync(IPage page)
    {
        if (page.IsClosed)
        {
            return "";
        }

        try
        {
            return await page.Locator("body").InnerTextAsync();
        }
        catch (PlaywrightException)
        {
            return "";
        }
    }
}

public sealed class ConsoleLogTee : IDisposable
{
    private readonly TextWriter originalOut;
    private readonly TextWriter originalErr;
    private readonly StreamWriter fileWriter;
    private readonly object writeLock = new();
    private readonly TeeTextWriter outTee;
    private readonly TeeTextWriter errTee;
    private bool disposed;

    private ConsoleLogTee(TextWriter originalOut, TextWriter originalErr, StreamWriter fileWriter)
    {
        this.originalOut = originalOut;
        this.originalErr = originalErr;
        this.fileWriter = fileWriter;
        outTee = new TeeTextWriter(originalOut, fileWriter, writeLock, prefix: null);
        errTee = new TeeTextWriter(originalErr, fileWriter, writeLock, prefix: "[ERR] ");
        Console.SetOut(outTee);
        Console.SetError(errTee);
    }

    public static ConsoleLogTee Attach(string projectRoot, string runnerName)
    {
        var logsDir = Path.Combine(projectRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var safeRunner = Regex.Replace(runnerName.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(safeRunner))
        {
            safeRunner = "runner";
        }

        var logPath = Path.Combine(logsDir, $"{safeRunner}.log");
        var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var tee = new ConsoleLogTee(Console.Out, Console.Error, writer);
        Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {runnerName} runner started (timeouts disabled; browser will stay open; log: {logPath}).");
        return tee;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Console.SetOut(originalOut);
        Console.SetError(originalErr);
        lock (writeLock)
        {
            fileWriter.Dispose();
        }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter primary;
        private readonly StreamWriter logFile;
        private readonly object sync;
        private readonly string? prefix;

        public TeeTextWriter(TextWriter primary, StreamWriter logFile, object sync, string? prefix)
        {
            this.primary = primary;
            this.logFile = logFile;
            this.sync = sync;
            this.prefix = prefix;
        }

        public override Encoding Encoding => primary.Encoding;

        public override void Write(char value)
        {
            lock (sync)
            {
                primary.Write(value);
                logFile.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (sync)
            {
                primary.Write(value);
                logFile.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (sync)
            {
                primary.WriteLine(value);
                if (prefix is not null && !string.IsNullOrEmpty(value))
                {
                    logFile.Write(prefix);
                }
                logFile.WriteLine(value);
            }
        }
    }
}

public sealed class LearnedLogicData
{
    public HashSet<string> SkippedDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ApplyButtonLabelsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> NextButtonLabelsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> SubmitButtonLabelsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> DismissButtonLabelsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ConfirmationPhrasesByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> QuestionAnswers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LearnedLogicStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;
    private LearnedLogicData data;

    private LearnedLogicStore(string path, LearnedLogicData data)
    {
        this.path = path;
        this.data = data;
    }

    public string FilePath => path;

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JobApplicator",
            "learned-logic.json");

    public static async Task<LearnedLogicStore> LoadAsync(string? customPath = null)
    {
        var resolvedPath = customPath ?? GetDefaultPath();
        var data = await ReadDataAsync(resolvedPath);
        return new LearnedLogicStore(resolvedPath, data);
    }

    public static string ExtractHost(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host.ToLowerInvariant();
        }
        return "unknown";
    }

    public bool IsSkippedDomain(string? url)
    {
        var host = ExtractHost(url);
        if (host == "unknown")
        {
            return false;
        }

        return data.SkippedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> GetLearnedButtons(string? url, LearnedActionKind kind)
    {
        var host = ExtractHost(url);
        var map = GetButtonMap(data, kind);
        return map.TryGetValue(host, out var labels) ? labels : [];
    }

    public IReadOnlyList<string> GetConfirmationPhrases(string? url)
    {
        var host = ExtractHost(url);
        return data.ConfirmationPhrasesByHost.TryGetValue(host, out var phrases) ? phrases : [];
    }

    public bool MatchesLearnedConfirmation(string? url, string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return false;
        }

        foreach (var phrase in GetConfirmationPhrases(url))
        {
            if (!string.IsNullOrWhiteSpace(phrase) &&
                pageText.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public string? TryGetAnswer(string question)
    {
        var normalized = NormalizeKey(question);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (data.QuestionAnswers.TryGetValue(normalized, out var exact) &&
            !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        return null;
    }

    public async Task AddSkippedDomainAsync(string? url)
    {
        var host = ExtractHost(url);
        if (host == "unknown")
        {
            return;
        }

        await UpdateAndSaveAsync(current => current.SkippedDomains.Add(host));
    }

    public async Task AddButtonLabelAsync(string? url, LearnedActionKind kind, string label)
    {
        var host = ExtractHost(url);
        var trimmed = Regex.Replace(label ?? "", @"\s+", " ").Trim();
        if (host == "unknown" || string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        await UpdateAndSaveAsync(current =>
        {
            var map = GetButtonMap(current, kind);
            if (!map.TryGetValue(host, out var list))
            {
                list = [];
                map[host] = list;
            }
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }
        });
    }

    public async Task AddConfirmationPhraseAsync(string? url, string phrase)
    {
        var host = ExtractHost(url);
        var trimmed = Regex.Replace(phrase ?? "", @"\s+", " ").Trim();
        if (host == "unknown" || string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        await UpdateAndSaveAsync(current =>
        {
            if (!current.ConfirmationPhrasesByHost.TryGetValue(host, out var list))
            {
                list = [];
                current.ConfirmationPhrasesByHost[host] = list;
            }
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }
        });
    }

    public async Task SaveQuestionAnswerAsync(string question, string answer)
    {
        var normalized = NormalizeKey(question);
        var trimmedAnswer = answer.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(trimmedAnswer))
        {
            return;
        }

        await UpdateAndSaveAsync(current => current.QuestionAnswers[normalized] = trimmedAnswer);
    }

    private async Task UpdateAndSaveAsync(Action<LearnedLogicData> mutate)
    {
        // Reload latest from disk first so concurrent runners do not overwrite each other's learned rules.
        var latest = await ReadDataAsync(path);
        MergeInto(data, latest);
        mutate(latest);
        data = latest;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<LearnedLogicData> ReadDataAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new LearnedLogicData();
        }

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var loaded = await JsonSerializer.DeserializeAsync<LearnedLogicData>(stream);
            if (loaded is null)
            {
                return new LearnedLogicData();
            }

            return NormalizeCollections(loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"Could not read learned logic at {filePath}; starting fresh: {ex.Message}");
            return new LearnedLogicData();
        }
    }

    private static LearnedLogicData NormalizeCollections(LearnedLogicData loaded) => new()
    {
        SkippedDomains = new HashSet<string>(loaded.SkippedDomains ?? [], StringComparer.OrdinalIgnoreCase),
        ApplyButtonLabelsByHost = new Dictionary<string, List<string>>(loaded.ApplyButtonLabelsByHost ?? [], StringComparer.OrdinalIgnoreCase),
        NextButtonLabelsByHost = new Dictionary<string, List<string>>(loaded.NextButtonLabelsByHost ?? [], StringComparer.OrdinalIgnoreCase),
        SubmitButtonLabelsByHost = new Dictionary<string, List<string>>(loaded.SubmitButtonLabelsByHost ?? [], StringComparer.OrdinalIgnoreCase),
        DismissButtonLabelsByHost = new Dictionary<string, List<string>>(loaded.DismissButtonLabelsByHost ?? [], StringComparer.OrdinalIgnoreCase),
        ConfirmationPhrasesByHost = new Dictionary<string, List<string>>(loaded.ConfirmationPhrasesByHost ?? [], StringComparer.OrdinalIgnoreCase),
        QuestionAnswers = new Dictionary<string, string>(loaded.QuestionAnswers ?? [], StringComparer.OrdinalIgnoreCase)
    };

    private static void MergeInto(LearnedLogicData source, LearnedLogicData target)
    {
        foreach (var domain in source.SkippedDomains)
        {
            target.SkippedDomains.Add(domain);
        }

        MergeListMap(source.ApplyButtonLabelsByHost, target.ApplyButtonLabelsByHost);
        MergeListMap(source.NextButtonLabelsByHost, target.NextButtonLabelsByHost);
        MergeListMap(source.SubmitButtonLabelsByHost, target.SubmitButtonLabelsByHost);
        MergeListMap(source.DismissButtonLabelsByHost, target.DismissButtonLabelsByHost);
        MergeListMap(source.ConfirmationPhrasesByHost, target.ConfirmationPhrasesByHost);

        foreach (var (k, v) in source.QuestionAnswers)
        {
            target.QuestionAnswers.TryAdd(k, v);
        }
    }

    private static void MergeListMap(Dictionary<string, List<string>> source, Dictionary<string, List<string>> target)
    {
        foreach (var (host, labels) in source)
        {
            if (!target.TryGetValue(host, out var existing))
            {
                existing = [];
                target[host] = existing;
            }
            foreach (var label in labels)
            {
                if (!existing.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Add(label);
                }
            }
        }
    }

    private static Dictionary<string, List<string>> GetButtonMap(LearnedLogicData target, LearnedActionKind kind) =>
        kind switch
        {
            LearnedActionKind.Apply => target.ApplyButtonLabelsByHost,
            LearnedActionKind.Next => target.NextButtonLabelsByHost,
            LearnedActionKind.Submit => target.SubmitButtonLabelsByHost,
            LearnedActionKind.Dismiss => target.DismissButtonLabelsByHost,
            _ => target.NextButtonLabelsByHost
        };

    private static string NormalizeKey(string question) =>
        Regex.Replace(question ?? "", @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
}

public static class InteractiveBlockerResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task WritePendingQuestionsAsync(
        string projectRoot,
        string runnerName,
        string jobTitle,
        string jobUrl,
        IReadOnlyList<string> questions)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var pendingPath = Path.Combine(projectRoot, "pending-questions.json");
        var payload = new
        {
            runner = runnerName,
            jobTitle,
            jobUrl,
            timestamp = DateTimeOffset.UtcNow,
            unansweredQuestions = questions
        };
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void ClearPendingQuestions(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var pendingPath = Path.Combine(projectRoot, "pending-questions.json");
        if (File.Exists(pendingPath))
        {
            try
            {
                File.Delete(pendingPath);
            }
            catch (IOException)
            {
            }
        }
    }

    public static async Task<List<ILocator>> FindLearnedButtonsAsync(
        IPage page,
        LearnedLogicStore? store,
        LearnedActionKind kind)
    {
        var results = new List<ILocator>();
        if (store is null || page.IsClosed)
        {
            return results;
        }

        var labels = store.GetLearnedButtons(page.Url, kind);
        foreach (var label in labels)
        {
            try
            {
                var buttonMatches = page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
                for (var i = 0; i < await buttonMatches.CountAsync(); i++)
                {
                    var candidate = buttonMatches.Nth(i);
                    if (await candidate.IsVisibleAsync() && !await candidate.IsDisabledAsync())
                    {
                        results.Add(candidate);
                    }
                }

                if (kind == LearnedActionKind.Apply)
                {
                    var linkMatches = page.GetByRole(AriaRole.Link, new() { Name = label, Exact = true });
                    for (var i = 0; i < await linkMatches.CountAsync(); i++)
                    {
                        var candidate = linkMatches.Nth(i);
                        if (await candidate.IsVisibleAsync())
                        {
                            results.Add(candidate);
                        }
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return results;
    }

    public static async Task<bool> TryDismissLearnedPopupsAsync(IPage page, LearnedLogicStore? store)
    {
        var dismissButtons = await FindLearnedButtonsAsync(page, store, LearnedActionKind.Dismiss);
        if (dismissButtons.Count != 1)
        {
            return false;
        }

        try
        {
            await dismissButtons[0].ClickAsync();
            Console.WriteLine("  Dismissed popup using learned rule for this site.");
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    public static async Task<(string? Answer, PersonalProfile? UpdatedProfile)> AskAndLearnQuestionAsync(
        string question,
        string fieldType,
        IReadOnlyList<string> availableChoices,
        string jobTitle,
        string jobUrl,
        string runnerName,
        string? personalProfilePath,
        PersonalProfile? currentProfile,
        LearnedLogicStore? learnedLogic,
        CancellationToken cancellationToken)
    {
        var projectRoot = string.IsNullOrWhiteSpace(personalProfilePath)
            ? ""
            : Path.GetDirectoryName(personalProfilePath) ?? "";

        await WritePendingQuestionsAsync(projectRoot, runnerName, jobTitle, jobUrl, [question]);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine("===============================================================");
            Console.WriteLine($"[QUESTION NEEDED - {runnerName}] {jobTitle}");
            Console.WriteLine($"  Field ({fieldType}): {question}");
            if (availableChoices.Count > 0)
            {
                Console.WriteLine("  Available choices:");
                for (var i = 0; i < availableChoices.Count; i++)
                {
                    Console.WriteLine($"    [{i + 1}] {availableChoices[i]}");
                }
            }
            Console.WriteLine("  No timeout — waiting for your reply whenever you're ready.");
            Console.WriteLine("  Type your answer (or choice number) to fill & save it to personal.md so we never ask again.");
            Console.WriteLine("  Or type: [R] reload personal.md | [M] handle manually in browser | [S] skip field");
            Console.WriteLine("===============================================================");
            Console.Write("Your answer: ");

            var input = await Console.In.ReadLineAsync(cancellationToken);
            if (input is null)
            {
                return (null, currentProfile);
            }

            var trimmed = input.Trim();
            if (trimmed.Length == 0 || trimmed.Equals("M", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Leaving this field for manual browser input.");
                return (null, currentProfile);
            }
            if (trimmed.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Skipping this field.");
                return (null, currentProfile);
            }
            if (trimmed.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(personalProfilePath))
                {
                    var reloaded = await PersonalProfile.LoadAsync(personalProfilePath);
                    var found = reloaded?.AnswerForQuestion(question);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        Console.WriteLine($"  Loaded answer from updated personal.md: {found}");
                        ClearPendingQuestions(projectRoot);
                        return (found, reloaded);
                    }
                    Console.WriteLine("  Reloaded personal.md, but no matching answer was found yet.");
                    currentProfile = reloaded ?? currentProfile;
                }
                continue;
            }

            string resolvedAnswer = trimmed;
            if (availableChoices.Count > 0 &&
                int.TryParse(trimmed, out var choiceNumber) &&
                choiceNumber >= 1 &&
                choiceNumber <= availableChoices.Count)
            {
                resolvedAnswer = availableChoices[choiceNumber - 1];
            }

            if (learnedLogic is not null)
            {
                await learnedLogic.SaveQuestionAnswerAsync(question, resolvedAnswer);
            }

            var updatedProfile = currentProfile;
            if (!string.IsNullOrWhiteSpace(personalProfilePath))
            {
                updatedProfile = await PersonalProfile.SaveAnswerAsync(personalProfilePath, question, resolvedAnswer);
                Console.WriteLine($"  Saved to personal.md & learned logic so future runs answer automatically: \"{resolvedAnswer}\"");
            }
            else
            {
                Console.WriteLine($"  Saved to learned logic: \"{resolvedAnswer}\"");
            }

            ClearPendingQuestions(projectRoot);
            return (resolvedAnswer, updatedProfile);
        }
    }

    public static async Task<BlockerResolution> AskAndLearnActionAsync(
        IPage page,
        string runnerName,
        string jobTitle,
        string blockerReason,
        LearnedActionKind defaultActionKind,
        LearnedLogicStore? learnedLogic,
        CancellationToken cancellationToken)
    {
        var host = LearnedLogicStore.ExtractHost(page.IsClosed ? null : page.Url);
        var controls = await DiscoverVisibleClickableControlsAsync(page);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine("===============================================================");
            Console.WriteLine($"[GUIDANCE NEEDED - {runnerName}] {jobTitle}");
            Console.WriteLine($"  Page: {(page.IsClosed ? "(closed)" : page.Url)}");
            Console.WriteLine($"  Reason: {blockerReason}");
            if (controls.Count > 0)
            {
                Console.WriteLine("  Visible buttons/links I can click and remember for this site:");
                for (var i = 0; i < controls.Count; i++)
                {
                    Console.WriteLine($"    [{i + 1}] ({controls[i].Role}) {controls[i].Label}");
                }
            }
            else
            {
                Console.WriteLine("  No obvious clickable buttons were detected on this page.");
            }
            Console.WriteLine("  No timeout — waiting for your guidance whenever you're ready.");
            Console.WriteLine($"  Enter a number [1-{controls.Count}] to click & remember as '{defaultActionKind}' for {host},");
            Console.WriteLine("  or prefix with role: N:<num> (Next), U:<num> (Submit), P:<num> (Apply), X:<num> (Dismiss popup).");
            Console.WriteLine("  Or type: [M] I handled it in browser (re-check) | [A] already submitted | [R] reload personal.md | [S] skip job | [D] always skip this domain");
            Console.WriteLine("===============================================================");
            Console.Write("Your choice: ");

            var raw = await Console.In.ReadLineAsync(cancellationToken);
            if (raw is null)
            {
                return new BlockerResolution(BlockerResolutionKind.SkipListing, Host: host);
            }

            var input = raw.Trim();
            if (input.Equals("M", StringComparison.OrdinalIgnoreCase) || input.Length == 0)
            {
                return new BlockerResolution(BlockerResolutionKind.ManualHandled, Host: host);
            }
            if (input.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                await OfferToLearnConfirmationPhraseAsync(page, learnedLogic, cancellationToken);
                return new BlockerResolution(BlockerResolutionKind.AlreadySubmitted, Host: host);
            }
            if (input.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                return new BlockerResolution(BlockerResolutionKind.ReloadProfile, Host: host);
            }
            if (input.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                return new BlockerResolution(BlockerResolutionKind.SkipListing, Host: host);
            }
            if (input.Equals("D", StringComparison.OrdinalIgnoreCase))
            {
                if (learnedLogic is not null && host != "unknown")
                {
                    await learnedLogic.AddSkippedDomainAsync(page.Url);
                    Console.WriteLine($"  Remembered: future jobs on '{host}' will be skipped automatically.");
                }
                return new BlockerResolution(BlockerResolutionKind.SkipDomain, Host: host);
            }

            var actionKind = defaultActionKind;
            var numberText = input;
            if (input.Length > 2 && input[1] == ':')
            {
                actionKind = char.ToUpperInvariant(input[0]) switch
                {
                    'P' => LearnedActionKind.Apply,
                    'N' => LearnedActionKind.Next,
                    'U' => LearnedActionKind.Submit,
                    'X' => LearnedActionKind.Dismiss,
                    _ => defaultActionKind
                };
                numberText = input[2..].Trim();
            }

            if (int.TryParse(numberText, out var selectedIndex) &&
                selectedIndex >= 1 &&
                selectedIndex <= controls.Count)
            {
                var chosen = controls[selectedIndex - 1];
                try
                {
                    await NoTimeoutPlaywright.SafeScrollIntoViewAsync(chosen.Locator);
                    await chosen.Locator.ClickAsync();
                }
                catch (PlaywrightException ex)
                {
                    Console.WriteLine($"  Clicking '{chosen.Label}' failed ({ex.Message}); trying forced click...");
                    try
                    {
                        await chosen.Locator.ClickAsync(new LocatorClickOptions { Force = true });
                    }
                    catch (PlaywrightException forcedEx)
                    {
                        Console.WriteLine($"  Forced click also failed: {forcedEx.Message}. Complete it in the browser and type M.");
                        continue;
                    }
                }

                if (learnedLogic is not null && host != "unknown")
                {
                    await learnedLogic.AddButtonLabelAsync(page.Url, actionKind, chosen.Label);
                    Console.WriteLine($"  Learned: on '{host}', '{chosen.Label}' is a '{actionKind}' control. Future runs will use it automatically.");
                }

                return new BlockerResolution(BlockerResolutionKind.ClickedLearnedButton, chosen.Label, host);
            }

            Console.WriteLine("  Unrecognized input. Enter a listed number, or M / A / R / S / D.");
        }
    }

    public static async Task<bool?> AskAndLearnConfirmationAsync(
        IPage page,
        string runnerName,
        string jobTitle,
        LearnedLogicStore? learnedLogic,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine("===============================================================");
            Console.WriteLine($"[CONFIRMATION CHECK - {runnerName}] {jobTitle}");
            Console.WriteLine("  I could not automatically confirm whether this application was submitted.");
            Console.WriteLine("  No timeout — waiting for your reply whenever you're ready.");
            Console.WriteLine("  [Y] Yes, it was submitted (record as applied & learn confirmation phrase)");
            Console.WriteLine("  [N] No, not submitted (leave unrecorded)");
            Console.WriteLine("  [R] Re-check the browser page");
            Console.WriteLine("===============================================================");
            Console.Write("Submitted? [Y/N/R]: ");

            var input = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
            if (input is null || input.Equals("N", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (input.Equals("Y", StringComparison.OrdinalIgnoreCase))
            {
                await OfferToLearnConfirmationPhraseAsync(page, learnedLogic, cancellationToken);
                return true;
            }
            if (input.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
    }

    public static async Task<ErrorRecoveryAction> AskOnErrorAsync(
        IPage? page,
        string runnerName,
        string jobTitle,
        Exception ex,
        LearnedLogicStore? learnedLogic,
        CancellationToken cancellationToken)
    {
        var url = page is not null && !page.IsClosed ? page.Url : null;
        var host = LearnedLogicStore.ExtractHost(url);

        Console.Error.WriteLine();
        Console.Error.WriteLine("===============================================================");
        Console.Error.WriteLine($"[RUNNER BLOCKER / ERROR - {runnerName}] {jobTitle}");
        if (!string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine($"  Page: {url}");
        }
        Console.Error.WriteLine($"  Error: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        Console.Error.WriteLine("===============================================================");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine("Browser is staying open with no timeout. How would you like to proceed?");
            Console.WriteLine("  [R] Retry this listing (after fixing anything in the browser)");
            Console.WriteLine("  [A] Mark this listing as applied and continue");
            Console.WriteLine("  [S] Skip this listing and continue");
            if (host != "unknown")
            {
                Console.WriteLine($"  [D] Always skip domain '{host}' in future runs and continue");
            }
            Console.WriteLine("  [Q] Quit runner cleanly");
            Console.Write("Your choice: ");

            var choice = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
            if (choice is null || choice.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorRecoveryAction.Quit;
            }
            if (choice.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorRecoveryAction.Retry;
            }
            if (choice.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorRecoveryAction.MarkApplied;
            }
            if (choice.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorRecoveryAction.SkipListing;
            }
            if (choice.Equals("D", StringComparison.OrdinalIgnoreCase) && host != "unknown")
            {
                if (learnedLogic is not null)
                {
                    await learnedLogic.AddSkippedDomainAsync(url);
                    Console.WriteLine($"  Remembered: future jobs on '{host}' will be skipped automatically.");
                }
                return ErrorRecoveryAction.SkipDomain;
            }
        }
    }

    private static async Task OfferToLearnConfirmationPhraseAsync(
        IPage page,
        LearnedLogicStore? learnedLogic,
        CancellationToken cancellationToken)
    {
        if (learnedLogic is null || page.IsClosed)
        {
            return;
        }

        var host = LearnedLogicStore.ExtractHost(page.Url);
        if (host == "unknown")
        {
            return;
        }

        var bodyText = await NoTimeoutPlaywright.SafeGetBodyTextAsync(page);
        var candidateLines = bodyText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length is >= 8 and <= 120)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (candidateLines.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  To auto-detect submission next time on '{host}', pick a confirmation phrase from this page (or press Enter to skip):");
        for (var i = 0; i < candidateLines.Count; i++)
        {
            Console.WriteLine($"    [{i + 1}] {candidateLines[i]}");
        }
        Console.Write("  Phrase number or custom text (Enter to skip): ");
        var reply = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }

        var phrase = int.TryParse(reply, out var idx) && idx >= 1 && idx <= candidateLines.Count
            ? candidateLines[idx - 1]
            : reply;
        await learnedLogic.AddConfirmationPhraseAsync(page.Url, phrase);
        Console.WriteLine($"  Learned confirmation phrase for '{host}': \"{phrase}\"");
    }

    private sealed record VisibleControl(string Role, string Label, ILocator Locator);

    private static async Task<List<VisibleControl>> DiscoverVisibleClickableControlsAsync(IPage page)
    {
        var results = new List<VisibleControl>();
        if (page.IsClosed)
        {
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var elements = page.Locator("button, input[type='submit'], a[href], [role='button']");
            var count = Math.Min(await elements.CountAsync(), 120);
            for (var i = 0; i < count && results.Count < 25; i++)
            {
                var el = elements.Nth(i);
                if (!await el.IsVisibleAsync())
                {
                    continue;
                }

                var label = await el.EvaluateAsync<string>(
                    "node => (node.innerText || node.value || node.getAttribute('aria-label') || '').replace(/\\s+/g, ' ').trim()");
                if (string.IsNullOrWhiteSpace(label) || label.Length > 80)
                {
                    continue;
                }

                var tag = await el.EvaluateAsync<string>("node => node.tagName.toLowerCase()");
                var role = tag == "a" ? "link" : "button";
                if (seen.Add($"{role}:{label}"))
                {
                    results.Add(new VisibleControl(role, label, el));
                }
            }
        }
        catch (PlaywrightException)
        {
        }

        return results;
    }
}
