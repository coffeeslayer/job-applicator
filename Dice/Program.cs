using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private const string SearchUrl =
        "https://www.dice.com/jobs?filters.easyApply=true&filters.postedDate=THREE&filters.workplaceTypes=Remote&q=.net+developer+with+react";

    private static readonly Regex JobIdPattern = new(@"/job-detail/([^/?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExcludedApplicationUrl = new(@"talenthop|bestjobtool", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResultCountPattern = new(@"(\d+)\s+results?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: dotnet run");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        LocalModelAnswerClient? localModel = null;
        ResumeTailorService? resumeTailor = null;
        ConsoleLogTee? logTee = null;
        IPlaywright? playwright = null;
        IBrowserContext? context = null;
        TaskCompletionSource? browserClosed = null;
        var keepOpenReason = "Dice runner finished.";
        string? repositoryRoot = null;
        IPage? activePage = null;
        try
        {
            var personalProfilePath = PersonalProfile.FindPath(Environment.CurrentDirectory, AppContext.BaseDirectory);
            repositoryRoot = Path.GetDirectoryName(personalProfilePath)!;
            logTee = ConsoleLogTee.Attach(repositoryRoot, "Dice");
            var diceDirectory = Path.Combine(repositoryRoot, "Dice");
            var personalProfile = await PersonalProfile.LoadAsync(personalProfilePath);
            var resumePath = Path.Combine(repositoryRoot, "resume.md");
            var resumeText = File.Exists(resumePath) ? await File.ReadAllTextAsync(resumePath) : "";
            var statePath = Path.Combine(diceDirectory, ".dice-application-state.json");
            var browserProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiceJobSearch",
                "ChromeProfile");

            LoadDotEnv(Path.Combine(diceDirectory, ".env"));
            var appliedJobs = await LoadAppliedJobsAsync(statePath);
            localModel = await CreateLocalModelAsync(cancellation.Token);

            Directory.CreateDirectory(browserProfilePath);
            Console.WriteLine($"Opening the dedicated Dice Chrome profile at {browserProfilePath}...");
            playwright = await Playwright.CreateAsync();
            resumeTailor = await ResumeTailorService.CreateAsync(repositoryRoot, personalProfile, playwright);
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                browserProfilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = "chrome",
                    Headless = false,
                    Args = ["--start-maximized"],
                    ViewportSize = ViewportSize.NoViewport,
                    Timeout = 0
                });
            NoTimeoutPlaywright.Configure(context);
            browserClosed = NoTimeoutPlaywright.TrackBrowserClose(context);

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            NoTimeoutPlaywright.Configure(page);
            activePage = page;
            await page.BringToFrontAsync();
            Console.WriteLine($"Navigating to {SearchUrl}");
            await page.GotoAsync(SearchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 0
            });
            await AcceptCookiesIfPresentAsync(page);
            await WaitForSearchResultsAsync(page, repositoryRoot, cancellation.Token);
            Console.WriteLine($"Dice search results loaded: {page.Url}");

            await WaitForSignInAsync(page, repositoryRoot, cancellation.Token);
            if (!page.Url.StartsWith("https://www.dice.com/jobs", StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(SearchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 0
                });
                await WaitForSearchResultsAsync(page, repositoryRoot, cancellation.Token);
            }

            var skippedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = await FindJobsAsync(page);
            var pending = FilterPendingJobs(jobs, appliedJobs, skippedThisRun);
            Console.WriteLine($"Dice reports {jobs.Count} matching results.");
            foreach (var job in jobs)
            {
                Console.WriteLine($"- {job.Id}: {(pending.Any(item => item.Id == job.Id) ? "ready" : "already processed")} ({job.Title})");
            }

            if (jobs.Count == 0)
            {
                keepOpenReason = "No matching job listings loaded; no applications will be submitted.";
                Console.WriteLine(keepOpenReason);
                return 0;
            }

            Console.WriteLine($"Starting applications for {pending.Count} eligible result(s)...");
            var appliedThisRun = 0;
            while (pending.Count > 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (page.IsClosed)
                {
                    keepOpenReason = "Dice Chrome page was closed.";
                    Console.WriteLine(keepOpenReason);
                    return 0;
                }

                var job = pending[0];
                Console.WriteLine($"\nApplying to Dice listing {job.Id}: {job.Title}");
                if (!await ApplyToJobAsync(page, job, personalProfile, localModel, resumeTailor, resumeText, repositoryRoot, cancellation.Token))
                {
                    skippedThisRun.Add(job.Id);
                    pending.RemoveAt(0);
                    if (!page.IsClosed)
                    {
                        await ReturnToSearchAsync(page, repositoryRoot, cancellation.Token);
                    }
                    continue;
                }

                appliedJobs[job.Id] = new AppliedJob(job.Url, DateTimeOffset.UtcNow);
                await SaveAppliedJobsAsync(statePath, appliedJobs);
                appliedThisRun++;
                Console.WriteLine("Dice showed the success screen; application recorded locally.");
                await ReturnToSearchAsync(page, repositoryRoot, cancellation.Token);
                jobs = await FindJobsAsync(page);
                pending = FilterPendingJobs(jobs, appliedJobs, skippedThisRun);
            }

            keepOpenReason = $"Finished. Submitted {appliedThisRun} application(s) in this run.";
            Console.WriteLine($"\n{keepOpenReason}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            keepOpenReason = "Run cancelled.";
            Console.WriteLine(keepOpenReason);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Dice runner failed with an exception:");
            Console.Error.WriteLine(ex.ToString());
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(activePage, repositoryRoot, "Dice", $"Unhandled exception: {ex.Message}");
            }
            keepOpenReason = "Dice runner stopped after an error.";
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await NoTimeoutPlaywright.KeepBrowserOpenUntilClosedAsync(context, browserClosed, keepOpenReason);
            if (resumeTailor is not null)
            {
                await resumeTailor.DisposeAsync();
            }
            playwright?.Dispose();
            localModel?.Dispose();
            logTee?.Dispose();
        }
    }

    private static async Task<LocalModelAnswerClient?> CreateLocalModelAsync(CancellationToken cancellationToken)
    {
        var configuration = LocalModelConfiguration.FromEnvironment();
        if (configuration is null)
        {
            Console.WriteLine("No local model configured; unsupported fields will need manual review.");
            return null;
        }

        var model = new LocalModelAnswerClient(configuration);
        try
        {
            if (await model.IsAvailableAsync(cancellationToken))
            {
                Console.WriteLine($"Resume-grounded drafts are available using local model '{configuration.Model}'.");
                return model;
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        model.Dispose();
        Console.WriteLine("The configured local model is unavailable; unsupported fields will need manual review.");
        return null;
    }

    private static async Task<Dictionary<string, AppliedJob>> LoadAppliedJobsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, AppliedJob>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(path);
        var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, AppliedJob>>(stream, JsonOptions)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} must contain a JSON object.");
        return new Dictionary<string, AppliedJob>(entries, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveAppliedJobsAsync(string path, Dictionary<string, AppliedJob> appliedJobs)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, appliedJobs, JsonOptions);
    }

    private static void LoadDotEnv(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if ((key is "DICE_EMAIL" or "DICE_PASSWORD") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static async Task<List<JobListing>> FindJobsAsync(IPage page)
    {
        var bodyText = await page.Locator("body").InnerTextAsync();
        var countMatch = ResultCountPattern.Match(bodyText);
        if (!countMatch.Success || !int.TryParse(countMatch.Groups[1].Value, out var resultCount))
        {
            throw new InvalidDataException("Could not determine the result count. Confirm the Dice search page loaded.");
        }

        var links = page.Locator("a[href*='/job-detail/']");
        var jobs = new Dictionary<string, JobListing>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < await links.CountAsync(); index++)
        {
            var link = links.Nth(index);
            var href = await link.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(new Uri(SearchUrl), href, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("www.dice.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var idMatch = JobIdPattern.Match(uri.AbsolutePath);
            if (!idMatch.Success)
            {
                continue;
            }

            var id = idMatch.Groups[1].Value;
            var cardText = "";
            try
            {
                cardText = (await link.Locator("xpath=..").InnerTextAsync()).Trim();
            }
            catch (PlaywrightException)
            {
            }

            var title = Regex.Replace((await link.InnerTextAsync()).Trim(), @"\s+", " ");
            if (title.Length == 0)
            {
                title = cardText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? id;
            }

            if (!jobs.TryGetValue(id, out var existing) || cardText.Length > existing.CardText.Length)
            {
                jobs[id] = new JobListing(id, uri.GetLeftPart(UriPartial.Path), title, cardText);
            }
        }

        var results = jobs.Values.Take(resultCount).ToList();
        if (results.Count != resultCount)
        {
            throw new InvalidDataException(
                $"Dice reports {resultCount} results, but only {results.Count} listing links were found. No applications were submitted.");
        }

        return results;
    }

    private static List<JobListing> FilterPendingJobs(
        IEnumerable<JobListing> jobs,
        IReadOnlyDictionary<string, AppliedJob> appliedJobs,
        IReadOnlySet<string> skippedThisRun) => jobs
        .Where(job => !appliedJobs.ContainsKey(job.Id))
        .Where(job => !skippedThisRun.Contains(job.Id))
        .Where(job => !ExcludedApplicationUrl.IsMatch(job.Url))
        .Where(job => !Regex.IsMatch(job.CardText, @"\bApplied\b", RegexOptions.IgnoreCase))
        .ToList();

    private static async Task WaitForSearchResultsAsync(IPage page, string repositoryRoot, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var loggedDelay = false;
        while (!page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bodyText = await NoTimeoutPlaywright.SafeGetBodyTextAsync(page);
            if (ResultCountPattern.IsMatch(bodyText))
            {
                return;
            }
            if (!loggedDelay && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15))
            {
                loggedDelay = true;
                Console.WriteLine($"Still waiting for Dice search results to load (no timeout). Current page: {page.Url}");
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", "Waiting for search results");
            }
            await page.WaitForTimeoutAsync(500);
        }
    }

    private static async Task AcceptCookiesIfPresentAsync(IPage page)
    {
        var acceptButtons = page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex(@"^\s*Accept all\s*$", RegexOptions.IgnoreCase) });
        var visible = await GetVisibleLocatorsAsync(acceptButtons);
        if (visible.Count == 1)
        {
            try
            {
                await visible[0].ClickAsync();
                Console.WriteLine("Accepted all cookies.");
            }
            catch (PlaywrightException ex)
            {
                Console.WriteLine($"Could not accept cookies ({ex.Message}); continuing.");
            }
        }
        else
        {
            Console.WriteLine("Cookie consent button not available; continuing.");
        }
    }

    private static async Task WaitForSignInAsync(IPage page, string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!await IsSignedOutAsync(page))
        {
            return;
        }

        var email = Environment.GetEnvironmentVariable("DICE_EMAIL")?.Trim() ?? "";
        var password = Environment.GetEnvironmentVariable("DICE_PASSWORD") ?? "";
        if (string.IsNullOrEmpty(email) != string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException("Set both DICE_EMAIL and DICE_PASSWORD in Dice/.env, or leave both empty for manual sign-in.");
        }

        if (email.Length > 0 && password.Length > 0)
        {
            await SignInWithCredentialsAsync(page, email, password, cancellationToken);
            if (!await IsSignedOutAsync(page))
            {
                Console.WriteLine("Dice sign-in successful.");
                return;
            }
        }

        Console.WriteLine("Complete Dice sign-in or any verification in the Chrome window. No timeout — the runner will continue automatically after sign-in is detected.");
        var lastReminder = DateTimeOffset.UtcNow;
        var dumpedDiagnostics = false;
        while (!page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSignedOutAsync(page) &&
                Uri.TryCreate(page.Url, UriKind.Absolute, out var current) &&
                current.Host.EndsWith("dice.com", StringComparison.OrdinalIgnoreCase) &&
                !page.Url.Contains("/dashboard/login", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Dice sign-in detected; continuing.");
                return;
            }
            if (DateTimeOffset.UtcNow - lastReminder > TimeSpan.FromSeconds(30))
            {
                lastReminder = DateTimeOffset.UtcNow;
                Console.WriteLine($"Still waiting for Dice sign-in in Chrome (no timeout). Current page: {page.Url}");
                if (!dumpedDiagnostics)
                {
                    dumpedDiagnostics = true;
                    await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", "Waiting for Dice sign-in");
                }
            }
            await page.WaitForTimeoutAsync(1000);
        }
    }

    private static async Task<bool> IsSignedOutAsync(IPage page)
    {
        try
        {
            var loginLinks = page.GetByRole(
                AriaRole.Link,
                new() { NameRegex = new Regex(@"^\s*Login\s*$", RegexOptions.IgnoreCase) });
            var visible = await GetVisibleLocatorsAsync(loginLinks);
            return visible.Count > 0;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task SignInWithCredentialsAsync(
        IPage page,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Signing in to Dice with credentials from .env...");
        if (!page.Url.StartsWith("https://www.dice.com/dashboard/login", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync("https://www.dice.com/dashboard/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 0 });
        }

        // If Dice redirected away from the login page (e.g. to /home-feed), the session
        // is still active — treat this as already signed in and return without submitting credentials.
        if (!page.Url.Contains("/dashboard/login", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Dice redirected to {page.Url} instead of the login page — session is still active; skipping credential submission.");
            return;
        }

        var emailField = page.Locator("input[type='email']").First;
        var waitStart = DateTimeOffset.UtcNow;
        while (!page.IsClosed && DateTimeOffset.UtcNow - waitStart < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!page.Url.Contains("/dashboard/login", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Dice redirected to {page.Url} — session is active.");
                return;
            }
            if (await emailField.IsVisibleAsync())
            {
                break;
            }
            await page.WaitForTimeoutAsync(300);
        }

        if (!await emailField.IsVisibleAsync())
        {
            Console.WriteLine("Dice's email sign-in form did not appear yet; leaving Chrome open for manual sign-in.");
            return;
        }

        await emailField.FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue with email", Exact = true }).ClickAsync();

        var passwordField = page.Locator("input[type='password'][autocomplete='current-password'], input[type='password'][name='password']").First;
        var passwordWaitStart = DateTimeOffset.UtcNow;
        while (!page.IsClosed && DateTimeOffset.UtcNow - passwordWaitStart < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await page.Locator("input[name='firstName'], input[name='confirmPassword']").CountAsync() > 0)
            {
                Console.WriteLine("Dice showed account registration instead of sign-in. Complete sign-in manually in Chrome.");
                return;
            }
            if (await passwordField.IsVisibleAsync())
            {
                break;
            }
            await page.WaitForTimeoutAsync(500);
        }

        if (!await passwordField.IsVisibleAsync())
        {
            Console.WriteLine("Dice did not show a password form. Complete any sign-in or verification step manually in Chrome.");
            return;
        }

        await passwordField.FillAsync(password);
        var form = passwordField.Locator("xpath=ancestor::form");
        var signInButtons = form.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex(@"^(sign in|log in|continue)$", RegexOptions.IgnoreCase) });
        var buttons = await GetVisibleLocatorsAsync(signInButtons);
        if (buttons.Count != 1)
        {
            Console.WriteLine("Dice's password sign-in button was not unique. Complete sign-in manually in Chrome.");
            return;
        }
        await buttons[0].ClickAsync();
        Console.WriteLine("Dice sign-in submitted. Waiting for the account session or additional verification in Chrome...");
    }

    private static async Task<bool> ApplyToJobAsync(
        IPage page,
        JobListing job,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        ResumeTailorService? resumeTailor,
        string resumeText,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        await page.GotoAsync(job.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 0 });
        if (ExcludedApplicationUrl.IsMatch(page.Url))
        {
            Console.WriteLine($"Skipping excluded application URL: {page.Url}");
            return false;
        }

        // Wait for React to hydrate either the Easy Apply button or an Applied indicator.
        var applyAction = page.Locator("[data-testid='apply-button']");
        var hydrationStart = DateTimeOffset.UtcNow;
        List<ILocator> visibleApplyActions = [];
        while (!page.IsClosed && DateTimeOffset.UtcNow - hydrationStart < TimeSpan.FromSeconds(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visibleApplyActions = await GetVisibleLocatorsAsync(applyAction);
            if (visibleApplyActions.Count > 0)
            {
                break;
            }
            var body = await NoTimeoutPlaywright.SafeGetBodyTextAsync(page);
            if (Regex.IsMatch(body, @"\bApplication Submitted\b|\bYou(?:'ve| have) applied\b", RegexOptions.IgnoreCase))
            {
                Console.WriteLine($"Listing already marked as applied on Dice ({job.Url}); skipping.");
                return false;
            }
            await page.WaitForTimeoutAsync(300);
        }

        if (visibleApplyActions.Count == 0)
        {
            Console.WriteLine($"No Easy Apply button appeared for {job.Url}; dumping diagnostics and skipping.");
            await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"No Easy Apply button on {job.Title}");
            return false;
        }

        var applyText = Regex.Replace(await visibleApplyActions[0].InnerTextAsync(), @"\s+", " ").Trim();
        if (applyText.Contains("Applied", StringComparison.OrdinalIgnoreCase) || await visibleApplyActions[0].IsDisabledAsync())
        {
            Console.WriteLine($"Apply button shows '{applyText}' (disabled or already applied) for {job.Url}; skipping.");
            return false;
        }

        if (visibleApplyActions.Count > 1)
        {
            Console.WriteLine($"Found {visibleApplyActions.Count} Apply buttons for {job.Url}; using the first one.");
        }

        TailoredResumeArtifact? tailoredResume = null;
        if (resumeTailor is not null)
        {
            var (jdText, company) = await JobDescriptionExtractor.ExtractFromPageAsync(page, "Dice", job.Title);
            tailoredResume = await resumeTailor.TailorForJobAsync(
                new JobTailoringContext("Dice", job.Title, company, page.Url, jdText),
                cancellationToken);
        }

        var effectiveResumeText = tailoredResume?.MarkdownText ?? resumeText;
        Console.WriteLine($"Clicking '{applyText}'...");
        await visibleApplyActions[0].ClickAsync();
        await ClickSubmitOrAnswerQuestionsAsync(page, job, personalProfile, localModel, effectiveResumeText, repositoryRoot, cancellationToken, tailoredResume?.PdfPath);
        return await WaitForApplicationSuccessAsync(page, job, repositoryRoot, cancellationToken);
    }

    private static async Task ClickSubmitOrAnswerQuestionsAsync(
        IPage page,
        JobListing job,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        string resumeText,
        string repositoryRoot,
        CancellationToken cancellationToken,
        string? tailoredResumePdfPath = null)
    {
        var diceReplaceAttempted = false;
        for (var step = 0; step < 12; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                return;
            }

            if (await IsApplicationSuccessAsync(page))
            {
                return;
            }

            var (submitButtons, nextButtons) = await WaitForWizardActionsAsync(page, job.Title, repositoryRoot, cancellationToken);
            if (page.IsClosed || await IsApplicationSuccessAsync(page))
            {
                return;
            }

            if (!diceReplaceAttempted &&
                !string.IsNullOrWhiteSpace(tailoredResumePdfPath) &&
                File.Exists(tailoredResumePdfPath))
            {
                diceReplaceAttempted = await TryUploadDiceTailoredResumeAsync(page, tailoredResumePdfPath, cancellationToken);
            }

            // Always draft/fill any blank fields on the current wizard step BEFORE clicking Next or Submit.
            var draft = await ExternalApplicationFormAssistant.DraftBlankFieldsAsync(
                page,
                localModel,
                job.Title,
                resumeText,
                personalProfile?.Residence,
                cancellationToken,
                personalProfile,
                tailoredResumePdfPath);
            if (draft.FilledCount > 0)
            {
                Console.WriteLine($"  Step {step + 1}: filled {draft.FilledCount} Dice field(s) from personal.md or resume-grounded local drafts.");
            }

            if (draft.UnansweredQuestions.Count > 0)
            {
                Console.WriteLine($"  Step {step + 1}: unanswered or ambiguous questions on {page.Url}:");
                foreach (var question in draft.UnansweredQuestions)
                {
                    Console.WriteLine($"    - {question}");
                }
                await InteractiveBlockerResolver.WritePendingQuestionsAsync(repositoryRoot, "Dice", job.Title, job.Url, draft.UnansweredQuestions);
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(
                    page,
                    repositoryRoot,
                    "Dice",
                    $"Unanswered questions on step {step + 1} for {job.Title}",
                    draft.UnansweredQuestions);
                await WaitForManualInputOrProgressAsync(page, cancellationToken);
                if (page.IsClosed || await IsApplicationSuccessAsync(page))
                {
                    return;
                }
                continue;
            }

            // Re-query buttons after filling fields (filling fields can enable Submit/Next or change state).
            (submitButtons, nextButtons) = await GetWizardActionsAsync(page);
            if (submitButtons.Count > 1)
            {
                Console.WriteLine($"  Step {step + 1}: several Submit buttons ({submitButtons.Count}) are visible; waiting for manual guidance.");
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Multiple Submit buttons on {job.Title}");
                await WaitForManualInputOrProgressAsync(page, cancellationToken);
                continue;
            }

            var visibleInvalidRequired = await GetVisibleInvalidRequiredFieldsAsync(page);
            if (visibleInvalidRequired.Count > 0)
            {
                Console.WriteLine($"  Step {step + 1}: {visibleInvalidRequired.Count} required field(s) are still incomplete or invalid.");
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Invalid required fields on step {step + 1} for {job.Title}");
                await WaitForManualInputOrProgressAsync(page, cancellationToken);
                if (page.IsClosed || await IsApplicationSuccessAsync(page))
                {
                    return;
                }
                continue;
            }

            if (submitButtons.Count == 1)
            {
                if (await submitButtons[0].IsDisabledAsync())
                {
                    Console.WriteLine($"  Step {step + 1}: Submit button is disabled; dumping diagnostics and waiting for manual resolution.");
                    await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Disabled Submit button on {job.Title}");
                    await WaitForManualInputOrProgressAsync(page, cancellationToken);
                    continue;
                }

                Console.WriteLine($"  Step {step + 1}: clicking Submit...");
                await submitButtons[0].ClickAsync();
                await page.WaitForTimeoutAsync(800);
                return;
            }

            if (nextButtons.Count == 1)
            {
                if (await nextButtons[0].IsDisabledAsync())
                {
                    Console.WriteLine($"  Step {step + 1}: Next button is disabled; dumping diagnostics and waiting for manual resolution.");
                    await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Disabled Next button on {job.Title}");
                    await WaitForManualInputOrProgressAsync(page, cancellationToken);
                    continue;
                }

                Console.WriteLine($"  Step {step + 1}: clicking Next...");
                await nextButtons[0].ClickAsync();
                await page.WaitForTimeoutAsync(600);
                continue;
            }

            if (await IsApplicationSuccessAsync(page))
            {
                return;
            }

            Console.WriteLine($"  Step {step + 1}: Dice did not show a unique Next or Submit button ({nextButtons.Count} Next, {submitButtons.Count} Submit).");
            await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"No unique Next/Submit button on step {step + 1} for {job.Title}");
            await WaitForManualInputOrProgressAsync(page, cancellationToken);
        }

        Console.WriteLine($"Reached maximum wizard steps for {job.Title}; dumping diagnostics and waiting for manual completion.");
        await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Exceeded wizard steps for {job.Title}");
        await WaitForManualInputOrProgressAsync(page, cancellationToken);
    }

    private static async Task<List<ILocator>> GetVisibleInvalidRequiredFieldsAsync(IPage page)
    {
        var invalid = page.Locator(
            "input:required:invalid, textarea:required:invalid, select:required:invalid, [aria-required='true'][aria-invalid='true']");
        return await GetVisibleLocatorsAsync(invalid);
    }

    private static async Task WaitForManualInputOrProgressAsync(IPage page, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Waiting in Chrome (non-interactive console, no timeout). Complete the step in Chrome or close Chrome when done.");
            var startingUrl = page.Url;
            while (!page.IsClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsApplicationSuccessAsync(page) || !string.Equals(page.Url, startingUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                await page.WaitForTimeoutAsync(1000);
            }
            return;
        }

        Console.WriteLine("Complete the step in Chrome, then press Enter here to continue (no timeout; Ctrl+C cancels).");
        var readTask = Console.In.ReadLineAsync(cancellationToken).AsTask();
        while (!readTask.IsCompleted && !page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsApplicationSuccessAsync(page))
            {
                return;
            }
            await Task.WhenAny(readTask, Task.Delay(500, cancellationToken));
        }
    }

    private static async Task<(List<ILocator> Submit, List<ILocator> Next)> GetWizardActionsAsync(IPage page)
    {
        var submit = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*(Submit|Submit application)\s*$", RegexOptions.IgnoreCase) });
        var next = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*(Next|Continue|Review)\s*$", RegexOptions.IgnoreCase) });
        return (await GetVisibleLocatorsAsync(submit), await GetVisibleLocatorsAsync(next));
    }

    private static async Task<(List<ILocator> Submit, List<ILocator> Next)> WaitForWizardActionsAsync(
        IPage page,
        string jobTitle,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var dumped = false;
        while (!page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsApplicationSuccessAsync(page))
            {
                return ([], []);
            }

            var (submitButtons, nextButtons) = await GetWizardActionsAsync(page);
            if (submitButtons.Count > 0 || nextButtons.Count > 0)
            {
                return (submitButtons, nextButtons);
            }

            if (!dumped && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15))
            {
                dumped = true;
                Console.WriteLine($"Still waiting for Dice wizard Next/Submit buttons on {page.Url} (no timeout)...");
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Waiting for wizard actions on {jobTitle}");
                return (submitButtons, nextButtons);
            }

            await page.WaitForTimeoutAsync(250);
        }

        return ([], []);
    }

    private static async Task<bool> IsApplicationSuccessAsync(IPage page)
    {
        if (page.IsClosed)
        {
            return false;
        }
        var successUrl = Regex.IsMatch(page.Url, @"/wizard/success(?:[?#]|$)", RegexOptions.IgnoreCase);
        if (successUrl)
        {
            return true;
        }
        var bodyText = await NoTimeoutPlaywright.SafeGetBodyTextAsync(page);
        return Regex.IsMatch(bodyText, @"application is on its way|application submitted|thank you for applying", RegexOptions.IgnoreCase);
    }

    private static async Task<bool> WaitForApplicationSuccessAsync(
        IPage page,
        JobListing job,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var dumped = false;
        while (!page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsApplicationSuccessAsync(page))
            {
                return true;
            }
            if (!dumped && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15))
            {
                dumped = true;
                Console.WriteLine($"Still waiting for Dice confirmation screen after Submit on {page.Url} (no timeout).");
                await NoTimeoutPlaywright.DumpPageDiagnosticsAsync(page, repositoryRoot, "Dice", $"Waiting for confirmation after Submit on {job.Title}");
            }
            await page.WaitForTimeoutAsync(500);
        }

        return false;
    }

    private static async Task<bool> TryUploadDiceTailoredResumeAsync(
        IPage page,
        string tailoredResumePdfPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var bodyText = await NoTimeoutPlaywright.SafeGetBodyTextAsync(page);
            if (!Regex.IsMatch(bodyText, @"\bresume\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            var uploaded = await ResumeUploadAssistant.UploadResumeAsync(
                page.Locator("body"),
                cancellationToken,
                tailoredResumePdfPath,
                replaceExisting: true);
            if (uploaded > 0)
            {
                await page.WaitForTimeoutAsync(800);
                return true;
            }

            // Dice often shows a saved profile resume with a "Replace" button that opens a file chooser or reveals a file input.
            var replaceButtons = await GetVisibleLocatorsAsync(
                page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^(replace|upload\s+resume|change\s+resume)$", RegexOptions.IgnoreCase) }));
            if (replaceButtons.Count == 1)
            {
                try
                {
                    var chooser = await page.RunAndWaitForFileChooserAsync(
                        async () => await replaceButtons[0].ClickAsync(),
                        new PageRunAndWaitForFileChooserOptions { Timeout = 3000 });
                    await chooser.SetFilesAsync(tailoredResumePdfPath);
                    Console.WriteLine($"  Replaced Dice resume with tailored PDF: {Path.GetFileName(tailoredResumePdfPath)}");
                    await page.WaitForTimeoutAsync(1000);
                    return true;
                }
                catch (TimeoutException)
                {
                    await page.WaitForTimeoutAsync(500);
                    uploaded = await ResumeUploadAssistant.UploadResumeAsync(
                        page.Locator("body"),
                        cancellationToken,
                        tailoredResumePdfPath,
                        replaceExisting: true);
                    if (uploaded > 0)
                    {
                        await page.WaitForTimeoutAsync(800);
                        return true;
                    }
                }
            }
        }
        catch (PlaywrightException)
        {
        }

        return false;
    }

    private static async Task ReturnToSearchAsync(IPage page, string repositoryRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine($"Returning to the filtered search: {SearchUrl}");
        await page.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 0 });
        await AcceptCookiesIfPresentAsync(page);
        await WaitForSearchResultsAsync(page, repositoryRoot, cancellationToken);
    }

    private static async Task<List<ILocator>> GetVisibleLocatorsAsync(ILocator locator)
    {
        var matches = new List<ILocator>();
        for (var index = 0; index < await locator.CountAsync(); index++)
        {
            var item = locator.Nth(index);
            if (await item.IsVisibleAsync())
            {
                matches.Add(item);
            }
        }
        return matches;
    }

    private sealed record JobListing(string Id, string Url, string Title, string CardText);

    private sealed record AppliedJob(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("submitted_at")] DateTimeOffset SubmittedAt);
}
