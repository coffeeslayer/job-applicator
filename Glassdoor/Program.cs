using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private const string MatchesUrl = "https://www.glassdoor.com/recruiter/matches";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex SkippedApplicationHost = new(@"talenthop|bestjobtool", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JobListingUrl = new(@"/job-listing/|/partner/jobListing|[?&]jl=\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SubmitButtonName = new(@"^(submit application|submit|send application|apply to job)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NextButtonName = new(@"^(next|continue|review)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ApplyButtonName = new(@"^(easy apply|apply(?: now)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConfirmationText = new(
        @"application (?:has been|was) submitted|thank you for applying|thanks for applying|application received|successfully submitted|your application is on its way",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassdoorJobSearch");
        var profileDirectory = Path.Combine(dataDirectory, "PlaywrightProfile");
        var appliedJobsPath = Path.Combine(dataDirectory, "applied-jobs.json");
        var personalProfilePath = PersonalProfile.FindPath(Environment.CurrentDirectory, AppContext.BaseDirectory);
        var personalProfile = await PersonalProfile.LoadAsync(personalProfilePath);
        var projectRoot = Path.GetDirectoryName(personalProfilePath)!;
        var glassdoorDirectory = Path.Combine(projectRoot, "Glassdoor");
        var resumePath = Path.Combine(projectRoot, "resume.md");
        var resumeText = File.Exists(resumePath) ? await File.ReadAllTextAsync(resumePath) : "";
        LoadDotEnv(Path.Combine(glassdoorDirectory, ".env"));

        LocalModelAnswerClient? localModel = null;
        var modelConfiguration = LocalModelConfiguration.FromEnvironment();
        if (modelConfiguration is not null)
        {
            localModel = new LocalModelAnswerClient(modelConfiguration);
            try
            {
                if (!await localModel.IsAvailableAsync(cancellation.Token))
                {
                    Console.WriteLine("The configured local model endpoint is unavailable; resume-grounded drafts are disabled for this run.");
                    localModel.Dispose();
                    localModel = null;
                }
            }
            catch (HttpRequestException)
            {
                Console.WriteLine("The configured local model endpoint is unavailable; resume-grounded drafts are disabled for this run.");
                localModel?.Dispose();
                localModel = null;
            }
        }
        else
        {
            Console.WriteLine("No local model configured; unsupported questions will be left for manual review.");
        }

        Directory.CreateDirectory(profileDirectory);
        var appliedJobs = await LoadAppliedJobsAsync(appliedJobsPath);
        if (personalProfile is null)
        {
            Console.WriteLine($"Personal settings were not found at {personalProfilePath}. Create personal.md with your application answers.");
        }

        Console.WriteLine($"Applied jobs are tracked in {appliedJobsPath} ({appliedJobs.Count} recorded).");
        Console.WriteLine("The browser profile is local to your Windows account.");

        using var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;
        var keepJobPagesForTroubleshooting = false;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions { Headless = false });
            var searchPage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            Console.WriteLine($"Opening {MatchesUrl}");
            await searchPage.GotoAsync(MatchesUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await AcceptCookiesIfPresentAsync(searchPage);
            await WaitForSignInAsync(searchPage, TimeSpan.FromMinutes(5), cancellation.Token);
            if (!IsMatchesPage(searchPage.Url))
            {
                await searchPage.GotoAsync(MatchesUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            }

            Console.WriteLine();
            Console.WriteLine("Glassdoor Recruiter matches are open.");
            Console.WriteLine("Press Enter to scan the listings on this page.");
            if (Console.ReadLine() is null)
            {
                return 0;
            }

            var seenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!cancellation.IsCancellationRequested && !searchPage.IsClosed)
            {
                if (!IsMatchesPage(searchPage.Url))
                {
                    await searchPage.GotoAsync(MatchesUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                }

                var listings = await ReadListingsAsync(searchPage);
                if (listings.Count == 0)
                {
                    Console.WriteLine("No job links recognized yet; waiting up to 15 seconds for Glassdoor matches to finish loading...");
                    listings = await WaitForListingsAsync(searchPage, TimeSpan.FromSeconds(15), cancellation.Token);
                    if (listings.Count == 0)
                    {
                        await ReportListingDiagnosticsAsync(searchPage);
                    }
                }

                var newListings = listings.Where(listing => seenThisRun.Add(listing.Key)).ToList();
                Console.WriteLine($"Found {listings.Count} job link(s); {newListings.Count} not yet scanned this run.");

                foreach (var listing in newListings)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (ShouldSkipApplicationUrl(listing.Url))
                    {
                        Console.WriteLine($"Skipping excluded application URL: {listing.Url}");
                        continue;
                    }
                    if (listing.AlreadyApplied)
                    {
                        Console.WriteLine($"Skipping listing already marked Applied: {listing.Title}");
                        continue;
                    }
                    if (appliedJobs.Contains(listing.Key))
                    {
                        Console.WriteLine($"Skipping previously recorded application: {listing.Title}");
                        continue;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"Opening: {listing.Title}");
                    var jobPage = await context.NewPageAsync();
                    IPage? applicationPopup = null;
                    try
                    {
                        await jobPage.GotoAsync(listing.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                        if (ShouldSkipApplicationUrl(jobPage.Url))
                        {
                            Console.WriteLine($"Skipping excluded application URL after navigation: {jobPage.Url}");
                            continue;
                        }

                        var applyControls = await FindVisibleApplyControlsAsync(jobPage);
                        if (applyControls.Count == 1)
                        {
                            Console.WriteLine("Opening the unique Apply control. The app will fill recognized fields and submit when required fields are valid.");
                            var pagesBeforeApply = context.Pages.ToHashSet();
                            await applyControls[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
                            await jobPage.WaitForTimeoutAsync(800);
                            applicationPopup = context.Pages.FirstOrDefault(page => !pagesBeforeApply.Contains(page));
                            var applicationPage = applicationPopup ?? jobPage;
                            if (ShouldSkipApplicationUrl(applicationPage.Url))
                            {
                                Console.WriteLine($"Skipping excluded application URL: {applicationPage.Url}");
                                continue;
                            }

                            var outcome = await CompleteApplicationAsync(
                                applicationPage,
                                personalProfile,
                                string.IsNullOrWhiteSpace(resumeText) ? null : localModel,
                                resumeText,
                                listing.Title,
                                cancellation.Token);
                            if (outcome == ApplicationOutcome.SubmittedConfirmed)
                            {
                                appliedJobs.Add(listing.Key);
                                await SaveAppliedJobsAsync(appliedJobsPath, appliedJobs);
                                Console.WriteLine("Glassdoor or the employer site confirmed the application. Recorded as applied.");
                                await RestoreMatchesAsync(searchPage);
                                continue;
                            }
                            if (outcome == ApplicationOutcome.NeedsManual)
                            {
                                Console.WriteLine("Some questions were not supported by personal.md or resume.md. Leaving the form for manual review instead of submitting.");
                            }
                            else if (outcome == ApplicationOutcome.SubmittedUnconfirmed)
                            {
                                Console.WriteLine("Submit was clicked, but no confirmation message was detected. Check the page before recording it.");
                            }
                        }
                        else if (applyControls.Count > 1)
                        {
                            Console.WriteLine("Several Apply controls are visible; leaving the job page for you to choose.");
                        }
                        else
                        {
                            Console.WriteLine("No unique Apply control was found; leaving the job page open for manual review.");
                        }

                        Console.WriteLine("Complete the application in the browser if you want to apply.");
                        Console.WriteLine("After submitting, type A and press Enter to record it. Type S to skip, or just press Enter to leave it unrecorded.");
                        var action = Console.ReadLine()?.Trim();
                        if (action?.Equals("A", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            appliedJobs.Add(listing.Key);
                            await SaveAppliedJobsAsync(appliedJobsPath, appliedJobs);
                            Console.WriteLine("Recorded as applied.");
                            await RestoreMatchesAsync(searchPage);
                        }
                        else if (action?.Equals("S", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            Console.WriteLine("Skipped without recording.");
                        }
                        else if (action is null)
                        {
                            return 0;
                        }
                    }
                    catch
                    {
                        keepJobPagesForTroubleshooting = true;
                        throw;
                    }
                    finally
                    {
                        if (!keepJobPagesForTroubleshooting && applicationPopup is not null && !applicationPopup.IsClosed)
                        {
                            await applicationPopup.CloseAsync();
                        }
                        if (!keepJobPagesForTroubleshooting && !jobPage.IsClosed)
                        {
                            await jobPage.CloseAsync();
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Scan finished. Scroll or adjust the matches in the Glassdoor tab, then press Enter to scan new results.");
                Console.WriteLine("Type Q and press Enter to exit.");
                var next = Console.ReadLine();
                if (next is null || next.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Run cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Glassdoor runner failed with an exception:");
            Console.Error.WriteLine(ex.ToString());
            if (context is not null)
            {
                Console.WriteLine("Chromium and the failed application tab are being kept open for troubleshooting. Press Enter after inspecting them to close the browser and exit.");
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

            if ((key is "GLASSDOOR_EMAIL" or "GLASSDOOR_PASSWORD") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static async Task WaitForSignInAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await IsSignedOutAsync(page))
        {
            return;
        }

        var email = Environment.GetEnvironmentVariable("GLASSDOOR_EMAIL")?.Trim() ?? "";
        var password = Environment.GetEnvironmentVariable("GLASSDOOR_PASSWORD") ?? "";
        if (string.IsNullOrEmpty(email) != string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException("Set both GLASSDOOR_EMAIL and GLASSDOOR_PASSWORD in Glassdoor/.env, or leave both empty for manual sign-in.");
        }

        if (email.Length > 0 && password.Length > 0)
        {
            await SignInWithCredentialsAsync(page, email, password, cancellationToken);
            if (!await IsSignedOutAsync(page))
            {
                Console.WriteLine("Glassdoor sign-in successful.");
                return;
            }
        }

        Console.WriteLine("Complete Glassdoor sign-in or any verification in the Chromium window. The runner will continue after sign-in is detected.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                throw new PlaywrightException("The Glassdoor page was closed before sign-in.");
            }
            if (!await IsSignedOutAsync(page) && Uri.TryCreate(page.Url, UriKind.Absolute, out var current) &&
                current.Host.EndsWith("glassdoor.com", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Glassdoor sign-in detected; continuing.");
                return;
            }
            await page.WaitForTimeoutAsync(1000);
        }

        throw new PlaywrightException("Glassdoor sign-in was not detected within five minutes. Sign in in Chromium and rerun the command.");
    }

    private static async Task<bool> IsSignedOutAsync(IPage page)
    {
        if (page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            page.Url.Contains("signin", StringComparison.OrdinalIgnoreCase) ||
            page.Url.Contains("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var signIn = page.GetByRole(
                AriaRole.Link,
                new() { NameRegex = new Regex(@"^\s*(sign in|log in)\s*$", RegexOptions.IgnoreCase) });
            var signInButtons = page.GetByRole(
                AriaRole.Button,
                new() { NameRegex = new Regex(@"^\s*(sign in|log in)\s*$", RegexOptions.IgnoreCase) });
            return (await GetVisibleLocatorsAsync(signIn)).Count > 0 ||
                (await GetVisibleLocatorsAsync(signInButtons)).Count > 0;
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
        Console.WriteLine("Signing in to Glassdoor with credentials from .env...");
        var signInLinks = await GetVisibleLocatorsAsync(page.GetByRole(
            AriaRole.Link,
            new() { NameRegex = new Regex(@"^\s*(sign in|log in)\s*$", RegexOptions.IgnoreCase) }));
        var signInButtons = await GetVisibleLocatorsAsync(page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex(@"^\s*(sign in|log in)\s*$", RegexOptions.IgnoreCase) }));
        var openers = signInLinks.Concat(signInButtons).ToList();
        if (openers.Count == 1 && !await HasVisibleEmailFieldAsync(page))
        {
            await openers[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(800);
        }

        var emailField = page.Locator("input[type='email'], input[name='username'], input[autocomplete='username']").First;
        try
        {
            await emailField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            await emailField.FillAsync(email);
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"Glassdoor's email sign-in form was not available. Complete sign-in in the browser. ({ex.Message})");
            return;
        }

        if (!await HasVisiblePasswordFieldAsync(page))
        {
            var continueButtons = await GetVisibleLocatorsAsync(page.GetByRole(
                AriaRole.Button,
                new() { NameRegex = new Regex(@"^(continue(?: with email)?|next)$", RegexOptions.IgnoreCase) }));
            if (continueButtons.Count != 1)
            {
                Console.WriteLine("Glassdoor did not show a unique Continue button after the email. Complete sign-in in the browser.");
                return;
            }
            await continueButtons[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        }

        var passwordField = page.Locator("input[type='password']").First;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await passwordField.IsVisibleAsync())
            {
                break;
            }
            await page.WaitForTimeoutAsync(500);
        }

        if (!await passwordField.IsVisibleAsync())
        {
            Console.WriteLine("Glassdoor did not show a password form. Complete any sign-in or verification step manually in Chromium.");
            return;
        }

        await passwordField.FillAsync(password);
        var submitButtons = await GetVisibleLocatorsAsync(page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex(@"^(sign in|log in|continue)$", RegexOptions.IgnoreCase) }));
        if (submitButtons.Count != 1)
        {
            Console.WriteLine("Glassdoor's password sign-in button was not unique. The password was entered but not submitted.");
            return;
        }

        await submitButtons[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        Console.WriteLine("Glassdoor sign-in submitted. Waiting for the account session or additional verification in Chromium...");
    }

    private static async Task<bool> HasVisibleEmailFieldAsync(IPage page)
    {
        var fields = page.Locator("input[type='email'], input[name='username'], input[autocomplete='username']");
        return (await GetVisibleLocatorsAsync(fields)).Count > 0;
    }

    private static async Task<bool> HasVisiblePasswordFieldAsync(IPage page)
    {
        return (await GetVisibleLocatorsAsync(page.Locator("input[type='password']"))).Count > 0;
    }

    private static async Task AcceptCookiesIfPresentAsync(IPage page)
    {
        var acceptButtons = page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex(@"^\s*(accept all|accept|ok)\s*$", RegexOptions.IgnoreCase) });
        var visible = await GetVisibleLocatorsAsync(acceptButtons);
        if (visible.Count != 1)
        {
            return;
        }

        try
        {
            await visible[0].ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            Console.WriteLine("Accepted the cookie prompt.");
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"Could not accept cookies ({ex.Message}); continuing.");
        }
    }

    private static bool IsMatchesPage(string url) =>
        url.Contains("/recruiter/matches", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipApplicationUrl(string url) => SkippedApplicationHost.IsMatch(url);

    private static async Task<List<JobListing>> ReadListingsAsync(IPage page)
    {
        if (page.IsClosed)
        {
            return [];
        }

        var probes = await page.Locator("a[href]").EvaluateAllAsync<ListingProbe[]>(
            """
            links => links.filter(link => {
                const style = window.getComputedStyle(link);
                const rect = link.getBoundingClientRect();
                return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
            }).map(link => {
                const card = link.closest('article, li, [role="listitem"], [data-test*="job" i]') || link.parentElement;
                return {
                    href: link.href,
                    text: (link.innerText || '').replace(/\s+/g, ' ').trim(),
                    cardText: (card?.innerText || '').replace(/\s+/g, ' ').trim()
                };
            })
            """);

        var listings = new Dictionary<string, JobListing>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in probes)
        {
            if (string.IsNullOrWhiteSpace(probe.Href) || !Uri.TryCreate(probe.Href, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.EndsWith("glassdoor.com", StringComparison.OrdinalIgnoreCase) ||
                !JobListingUrl.IsMatch(uri.PathAndQuery) ||
                uri.AbsolutePath.Contains("/recruiter/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = ListingKey(uri);
            var title = probe.Text;
            if (title.Length == 0 || ApplyButtonName.IsMatch(title))
            {
                title = uri.Segments.LastOrDefault()?.Trim('/') ?? "Glassdoor job";
            }

            var listing = new JobListing(key, uri.AbsoluteUri, title, Regex.IsMatch(probe.CardText, @"\bApplied\b", RegexOptions.IgnoreCase));
            if (!listings.TryGetValue(key, out var existing) || title.Length > existing.Title.Length)
            {
                listings[key] = listing;
            }
        }

        return listings.Values.ToList();
    }

    private static string ListingKey(Uri uri)
    {
        var jobId = Regex.Match(uri.Query, @"[?&]jl=(\d+)", RegexOptions.IgnoreCase);
        if (jobId.Success)
        {
            return "jl:" + jobId.Groups[1].Value;
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    private static async Task<List<JobListing>> WaitForListingsAsync(IPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listings = await ReadListingsAsync(page);
            if (listings.Count > 0)
            {
                return listings;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return page.IsClosed ? [] : await ReadListingsAsync(page);
    }

    private static async Task ReportListingDiagnosticsAsync(IPage page)
    {
        if (page.IsClosed)
        {
            Console.WriteLine("The Glassdoor tab closed before listings could be read.");
            return;
        }

        var linkCount = await page.Locator("a[href]").CountAsync();
        var candidates = await page.Locator("a[href]").EvaluateAllAsync<string[]>(
            "links => links.slice(0, 12).map(link => `${(link.innerText || '').trim().replace(/\\s+/g, ' ').slice(0, 70)} | ${link.href}`)");
        Console.WriteLine($"After waiting, the scanner saw {linkCount} link(s), but no Glassdoor job links matching the current rule.");
        Console.WriteLine($"Current page: {page.Url}");
        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            Console.WriteLine($"  Link: {candidate}");
        }
    }

    private static async Task<List<ILocator>> FindVisibleApplyControlsAsync(IPage page)
    {
        var controls = new List<ILocator>();
        foreach (var role in new[] { AriaRole.Button, AriaRole.Link })
        {
            var candidates = page.GetByRole(role, new() { NameRegex = ApplyButtonName });
            controls.AddRange(await GetVisibleLocatorsAsync(candidates));
        }

        return controls;
    }

    private static async Task<ApplicationOutcome> CompleteApplicationAsync(
        IPage page,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        string resumeText,
        string jobTitle,
        CancellationToken cancellationToken)
    {
        for (var step = 0; step < 6; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                return ApplicationOutcome.NeedsManual;
            }

            ExternalDraftResult fillResult;
            try
            {
                fillResult = await ExternalApplicationFormAssistant.DraftBlankFieldsAsync(
                    page,
                    localModel,
                    jobTitle,
                    resumeText,
                    personalProfile?.Residence,
                    cancellationToken,
                    personalProfile);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Could not contact the local model; leaving the application open for review. ({ex.Message})");
                return ApplicationOutcome.NeedsManual;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"The local model returned an unusable answer; leaving the application open for review. ({ex.Message})");
                return ApplicationOutcome.NeedsManual;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"The local model request timed out; leaving the application open for review. ({ex.Message})");
                return ApplicationOutcome.NeedsManual;
            }

            if (fillResult.FilledCount > 0)
            {
                Console.WriteLine($"Filled {fillResult.FilledCount} recognized application field(s).");
            }
            if (fillResult.UnansweredQuestions.Count > 0)
            {
                foreach (var question in fillResult.UnansweredQuestions)
                {
                    Console.WriteLine($"  Unanswered: {question}");
                }
                return ApplicationOutcome.NeedsManual;
            }

            if (await HasVisiblePasswordFieldAsync(page))
            {
                Console.WriteLine("A password field is visible; leaving the page for you instead of submitting.");
                return ApplicationOutcome.NeedsManual;
            }

            var submitButtons = await GetApplicationButtonsAsync(page, SubmitButtonName);
            if (submitButtons.Count > 1)
            {
                Console.WriteLine("Several submit controls are visible; leaving the form for you to choose.");
                return ApplicationOutcome.NeedsManual;
            }
            if (submitButtons.Count == 1)
            {
                var invalidRequiredFields = page.Locator(
                    "input:required:invalid, textarea:required:invalid, select:required:invalid, [aria-required='true'][aria-invalid='true']");
                if (await invalidRequiredFields.CountAsync() > 0)
                {
                    Console.WriteLine($"Required application fields remain incomplete for {jobTitle}; leaving the form open for you.");
                    return ApplicationOutcome.NeedsManual;
                }

                Console.WriteLine($"Submitting the completed application for {jobTitle}.");
                await submitButtons[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
                return await WaitForConfirmationAsync(page, cancellationToken)
                    ? ApplicationOutcome.SubmittedConfirmed
                    : ApplicationOutcome.SubmittedUnconfirmed;
            }

            var nextButtons = await GetApplicationButtonsAsync(page, NextButtonName);
            if (nextButtons.Count != 1)
            {
                return ApplicationOutcome.NeedsManual;
            }

            await nextButtons[0].ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(800);
        }

        Console.WriteLine("The application did not reach a unique submit button; leaving it for manual review.");
        return ApplicationOutcome.NeedsManual;
    }

    private static async Task<List<ILocator>> GetApplicationButtonsAsync(IPage page, Regex name)
    {
        var matches = new List<ILocator>();
        foreach (var role in new[] { AriaRole.Button, AriaRole.Link })
        {
            var candidates = page.GetByRole(role, new() { NameRegex = name });
            foreach (var candidate in await GetVisibleLocatorsAsync(candidates))
            {
                var inApplication = await candidate.EvaluateAsync<bool>(
                    """
                    element => Boolean(element.closest('form, [role="dialog"], [role="alertdialog"], [class*="modal" i], [class*="application" i]'))
                    """);
                if (inApplication)
                {
                    matches.Add(candidate);
                }
            }
        }

        return matches;
    }

    private static async Task<bool> WaitForConfirmationAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && !page.IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConfirmationText.IsMatch(await page.Locator("body").InnerTextAsync()))
            {
                return true;
            }
            await page.WaitForTimeoutAsync(500);
        }

        return !page.IsClosed && ConfirmationText.IsMatch(await page.Locator("body").InnerTextAsync());
    }

    private static async Task RestoreMatchesAsync(IPage searchPage)
    {
        if (!searchPage.IsClosed)
        {
            await searchPage.GotoAsync(MatchesUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            Console.WriteLine("Restored the Glassdoor Recruiter matches page.");
        }
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

    private static async Task<HashSet<string>> LoadAppliedJobsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var urls = await JsonSerializer.DeserializeAsync<List<string>>(stream);
            return new HashSet<string>(urls ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Could not read application history; starting with an empty list: {path}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task SaveAppliedJobsAsync(string path, HashSet<string> keys)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase), JsonOptions);
    }

    private enum ApplicationOutcome
    {
        NeedsManual,
        SubmittedConfirmed,
        SubmittedUnconfirmed
    }

    private sealed record ListingProbe(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("cardText")] string CardText);

    private sealed record JobListing(string Key, string Url, string Title, bool AlreadyApplied);
}
