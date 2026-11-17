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
        IBrowserContext? context = null;
        try
        {
            var personalProfilePath = PersonalProfile.FindPath(Environment.CurrentDirectory, AppContext.BaseDirectory);
            var repositoryRoot = Path.GetDirectoryName(personalProfilePath)!;
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
            using var playwright = await Playwright.CreateAsync();
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                browserProfilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = "chrome",
                    Headless = false,
                    Args = ["--start-maximized"],
                    ViewportSize = ViewportSize.NoViewport,
                    Timeout = 30_000
                });

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            Console.WriteLine($"Navigating to {SearchUrl}");
            await page.GotoAsync(SearchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            await AcceptCookiesIfPresentAsync(page);
            await WaitForSearchResultsAsync(page, cancellation.Token);
            Console.WriteLine($"Dice search results loaded: {page.Url}");

            await WaitForSignInAsync(page, TimeSpan.FromMinutes(5), cancellation.Token);
            if (!page.Url.StartsWith("https://www.dice.com/jobs", StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(SearchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
                await WaitForSearchResultsAsync(page, cancellation.Token);
            }

            var jobs = await FindJobsAsync(page);
            var pending = FilterPendingJobs(jobs, appliedJobs);
            Console.WriteLine($"Dice reports {jobs.Count} matching results.");
            foreach (var job in jobs)
            {
                Console.WriteLine($"- {job.Id}: {(pending.Any(item => item.Id == job.Id) ? "ready" : "already processed")}");
            }

            if (jobs.Count == 0)
            {
                Console.WriteLine("No matching job listings loaded; no applications will be submitted.");
                return 0;
            }

            Console.WriteLine($"Starting applications for {pending.Count} eligible result(s)...");
            var appliedThisRun = 0;
            while (pending.Count > 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var job = pending[0];
                Console.WriteLine($"\nApplying to Dice listing {job.Id}: {job.Title}");
                if (!await ApplyToJobAsync(page, job, personalProfile, localModel, resumeText, cancellation.Token))
                {
                    pending.RemoveAt(0);
                    await ReturnToSearchAsync(page, cancellation.Token);
                    continue;
                }

                appliedJobs[job.Id] = new AppliedJob(job.Url, DateTimeOffset.UtcNow);
                await SaveAppliedJobsAsync(statePath, appliedJobs);
                appliedThisRun++;
                Console.WriteLine("Dice showed the success screen; application recorded locally.");
                await ReturnToSearchAsync(page, cancellation.Token);
                jobs = await FindJobsAsync(page);
                pending = FilterPendingJobs(jobs, appliedJobs);
            }

            Console.WriteLine($"\nFinished. Submitted {appliedThisRun} application(s) in this run.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Run cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Dice runner failed with an exception:");
            Console.Error.WriteLine(ex.ToString());
            if (context is not null)
            {
                Console.WriteLine("Chromium is being kept open for troubleshooting. Press Enter after inspecting it to close the browser and exit.");
                await Console.In.ReadLineAsync();
            }
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (context is not null)
            {
                await context.CloseAsync();
            }
            localModel?.Dispose();
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

    private static List<JobListing> FilterPendingJobs(IEnumerable<JobListing> jobs, IReadOnlyDictionary<string, AppliedJob> appliedJobs) => jobs
        .Where(job => !appliedJobs.ContainsKey(job.Id))
        .Where(job => !ExcludedApplicationUrl.IsMatch(job.Url))
        .Where(job => !Regex.IsMatch(job.CardText, @"\bApplied\b", RegexOptions.IgnoreCase))
        .ToList();

    private static async Task WaitForSearchResultsAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResultCountPattern.IsMatch(await page.Locator("body").InnerTextAsync()))
            {
                return;
            }
            await page.WaitForTimeoutAsync(500);
        }

        throw new PlaywrightException($"Dice search results did not load. Current page: {page.Url}");
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
                await visible[0].ClickAsync(new LocatorClickOptions { Timeout = 5000 });
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

    private static async Task WaitForSignInAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
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

        Console.WriteLine("Complete Dice sign-in or any verification in the Chrome window. The runner will continue after sign-in is detected.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                throw new PlaywrightException("The Dice Chrome page was closed before sign-in.");
            }
            if (!await IsSignedOutAsync(page) && Uri.TryCreate(page.Url, UriKind.Absolute, out var current) && current.Host.EndsWith("dice.com", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Dice sign-in detected; continuing.");
                return;
            }
            await page.WaitForTimeoutAsync(1000);
        }

        throw new PlaywrightException("Dice sign-in was not detected within five minutes. Sign in in Chrome and rerun the command.");
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
            await page.GotoAsync("https://www.dice.com/dashboard/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        var emailField = page.Locator("input[type='email']").First;
        try
        {
            await emailField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            await emailField.FillAsync(email);
            await page.GetByRole(AriaRole.Button, new() { Name = "Continue with email", Exact = true }).ClickAsync();
        }
        catch (PlaywrightException ex)
        {
            throw new PlaywrightException($"Dice's email sign-in form was not available. No credentials were submitted. ({ex.Message})");
        }

        var passwordField = page.Locator("input[type='password'][autocomplete='current-password'], input[type='password'][name='password']").First;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await page.Locator("input[name='firstName'], input[name='confirmPassword']").CountAsync() > 0)
            {
                throw new PlaywrightException("Dice showed account registration instead of sign-in. No registration was submitted; check DICE_EMAIL.");
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
            throw new PlaywrightException("Dice's password sign-in button was not unique. The password was entered but not submitted.");
        }
        await buttons[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        Console.WriteLine("Dice sign-in submitted. Waiting for the account session or additional verification in Chrome...");
    }

    private static async Task<bool> ApplyToJobAsync(
        IPage page,
        JobListing job,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        string resumeText,
        CancellationToken cancellationToken)
    {
        await page.GotoAsync(job.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (ExcludedApplicationUrl.IsMatch(page.Url))
        {
            Console.WriteLine($"Skipping excluded application URL: {page.Url}");
            return false;
        }

        var applyAction = page.Locator("a[data-testid='apply-button'], button[data-testid='apply-button'], [role='button'][data-testid='apply-button']");
        var visibleApplyActions = await GetVisibleLocatorsAsync(applyAction);
        if (visibleApplyActions.Count != 1)
        {
            throw new PlaywrightException($"Could not find a unique Dice Apply control for {job.Url}. The listing may not support Easy Apply.");
        }
        await visibleApplyActions[0].ClickAsync(new LocatorClickOptions { Timeout = 20_000 });
        await ClickUniqueButtonAsync(page, "Next", cancellationToken);
        await ClickSubmitOrAnswerQuestionsAsync(page, job.Title, personalProfile, localModel, resumeText, cancellationToken);
        await WaitForApplicationSuccessAsync(page, cancellationToken);
        return true;
    }

    private static async Task ClickSubmitOrAnswerQuestionsAsync(
        IPage page,
        string jobTitle,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        string resumeText,
        CancellationToken cancellationToken)
    {
        for (var step = 0; step < 8; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var submitButtons = await GetVisibleLocatorsAsync(page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }));
            if (submitButtons.Count > 1)
            {
                throw new PlaywrightException("Several Submit buttons are visible; stopping without guessing.");
            }

            if (submitButtons.Count == 1)
            {
                var invalidRequiredFields = page.Locator(
                    "input:required:invalid, textarea:required:invalid, select:required:invalid, [aria-required='true'][aria-invalid='true']");
                if (await invalidRequiredFields.CountAsync() == 0)
                {
                    await submitButtons[0].ClickAsync();
                    return;
                }

                Console.WriteLine("Required application fields are still incomplete. Complete them in Chrome before continuing.");
                await WaitForManualInputAsync(cancellationToken);
                continue;
            }

            var draft = await ExternalApplicationFormAssistant.DraftBlankFieldsAsync(
                page,
                localModel,
                jobTitle,
                resumeText,
                personalProfile?.Residence,
                cancellationToken,
                personalProfile);
            if (draft.FilledCount > 0)
            {
                Console.WriteLine($"Filled {draft.FilledCount} Dice field(s) from personal.md or resume-grounded local drafts.");
            }
            if (draft.UnansweredQuestions.Count > 0)
            {
                Console.WriteLine("Answer or review these unsupported or ambiguous questions in Chrome:");
                foreach (var question in draft.UnansweredQuestions)
                {
                    Console.WriteLine($"  - {question}");
                }
                await WaitForManualInputAsync(cancellationToken);
            }

            var nextButtons = await GetVisibleLocatorsAsync(page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }));
            if (nextButtons.Count != 1)
            {
                throw new PlaywrightException("Dice did not show a unique Next or Submit button after reviewing the application fields.");
            }
            await nextButtons[0].ClickAsync();
            await page.WaitForTimeoutAsync(500);
        }

        throw new PlaywrightException("Dice did not show a valid Submit button after the application steps.");
    }

    private static async Task WaitForManualInputAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Complete the fields in Chrome, then press Enter here to continue (Ctrl+C cancels).");
        if (await Console.In.ReadLineAsync(cancellationToken) is null)
        {
            throw new InvalidDataException("Console input ended before the manual application review was completed.");
        }
    }

    private static async Task ClickUniqueButtonAsync(IPage page, string name, CancellationToken cancellationToken)
    {
        var buttons = await GetVisibleLocatorsAsync(page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }));
        if (buttons.Count != 1)
        {
            throw new PlaywrightException($"Could not find a unique {name} button. Stopping without retrying.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await buttons[0].ClickAsync(new LocatorClickOptions { Timeout = 20_000 });
    }

    private static async Task WaitForApplicationSuccessAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var successUrl = Regex.IsMatch(page.Url, @"/wizard/success(?:[?#]|$)", RegexOptions.IgnoreCase);
            var bodyText = await page.Locator("body").InnerTextAsync();
            if (successUrl || Regex.IsMatch(bodyText, @"application is on its way", RegexOptions.IgnoreCase))
            {
                return;
            }
            await page.WaitForTimeoutAsync(500);
        }

        throw new PlaywrightException($"Dice did not show an application success page after Submit. Current page: {page.Url}. Check Applied Jobs before retrying.");
    }

    private static async Task ReturnToSearchAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine($"Returning to the filtered search: {SearchUrl}");
        await page.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        await AcceptCookiesIfPresentAsync(page);
        await WaitForSearchResultsAsync(page, cancellationToken);
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
