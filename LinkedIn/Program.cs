using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private const string SearchUrl =
        "https://www.linkedin.com/jobs/search-results/?currentJobId=4489288400&keywords=senior%20software%20engineer%20cloud%2C%20remote&origin=PREFERENCES_LANDING&referralSearchId=lfADMfcl%2F86A9Rd4DbZuog%3D%3D&geoId=100939624&f_TPR=r604800";
    private const string MouseIdleTrackingScript =
        """
        (() => {
            const key = '__linkedinJobSearchMouseIdle';
            if (window[key]) {
                window[key].reset();
                return window[key].documentId;
            }
            let lastMovement = performance.now();
            const onMouseMove = () => { lastMovement = performance.now(); };
            window.addEventListener('mousemove', onMouseMove, { capture: true, passive: true });
            const state = {
                documentId: performance.timeOrigin,
                reset() { lastMovement = performance.now(); },
                get inactivityMs() { return performance.now() - lastMovement; }
            };
            window[key] = state;
            return state.documentId;
        })();
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex DatePattern = new(
        @"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SkippedApplicationUrl = new(@"talenthop|bestjobtool", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResidenceQuestion = new(@"city\s*,\s*state\s+and\s+zip[\s\S]*currently residing", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            "LinkedInJobSearch");
        var profileDirectory = Path.Combine(dataDirectory, "PlaywrightProfile");
        var appliedJobsPath = Path.Combine(dataDirectory, "applied-jobs.json");
        var uncertainJobsPath = Path.Combine(dataDirectory, "uncertain-jobs.json");
        var confirmedAnswersPath = Path.Combine(dataDirectory, "confirmed-answers.json");
        var questionAnswersPath = Path.Combine(dataDirectory, "question-answers.json");
        var projectRoot = ResumePaths.FindProjectRoot(
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        var personalProfilePath = PersonalProfile.FindPath(projectRoot, Environment.CurrentDirectory);
        var personalProfile = await PersonalProfile.LoadAsync(personalProfilePath);
        var residenceCityStateZip = personalProfile?.Residence;
        var resumeTextPath = ResumePaths.GetResumePath(projectRoot);
        var resumeAnswersPath = Path.Combine(dataDirectory, "resume-answers.json");
        Directory.CreateDirectory(profileDirectory);

        var appliedJobs = await LoadListingIdsAsync(appliedJobsPath);
        var uncertainJobs = await LoadListingIdsAsync(uncertainJobsPath);
        var legacyStatePath = Path.Combine(projectRoot, ".linkedin-application-state.json");
        var importedAppliedIds = await LoadLegacyAppliedJobIdsAsync(legacyStatePath);
        var importedCount = importedAppliedIds.Count(id => appliedJobs.Add(id));
        await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
        Console.WriteLine(
            $"Applied job IDs are tracked in: {appliedJobsPath} ({appliedJobs.Count} recorded; " +
            $"imported {importedCount} from the previous application history).");
        var confirmedAnswers = await ConfirmedAnswerStore.LoadAsync(confirmedAnswersPath);
        var questionAnswers = await QuestionAnswerStore.LoadAsync(questionAnswersPath);
        foreach (var confirmedAnswer in confirmedAnswers.Entries)
        {
            if (!questionAnswers.ContainsExactQuestion(confirmedAnswer.Question))
            {
                await questionAnswers.AddAndSaveAsync(
                    questionAnswersPath,
                    confirmedAnswer.Question,
                    confirmedAnswer.Answer);
            }
        }
        Console.WriteLine($"Exact question/answer key-value pairs: {questionAnswersPath} ({questionAnswers.Count} saved).");
        var resumeAnswers = await ResumeAnswerStore.LoadAsync(resumeAnswersPath);
        var resumeText = File.Exists(resumeTextPath)
            ? await File.ReadAllTextAsync(resumeTextPath)
            : "";
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            Console.WriteLine($"Optional repository-root resume for excerpt suggestions: {resumeTextPath}");
        }
        Console.WriteLine($"Optional local resume-based answers: {resumeAnswersPath}");

        LocalModelAnswerClient? localModel = null;
        var localModelAvailable = false;
        var localModelConfiguration = LocalModelConfiguration.FromEnvironment();
        if (localModelConfiguration is null)
        {
            Console.WriteLine(
                "Local AI form drafts are disabled: no local model is configured. " +
                "Set LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL and LINKEDIN_JOB_SEARCH_LOCAL_MODEL to enable them.");
        }
        else
        {
            localModel = new LocalModelAnswerClient(localModelConfiguration);
            try
            {
                localModelAvailable = await localModel.IsAvailableAsync(cancellation.Token);
                Console.WriteLine(localModelAvailable
                    ? $"Local AI form drafts are available using model '{localModelConfiguration.Model}'."
                    : "The configured local model endpoint is unavailable; AI drafts are disabled for this run.");
            }
            catch (HttpRequestException)
            {
                Console.WriteLine("The configured local model endpoint is unavailable; AI drafts are disabled for this run.");
            }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
            {
                Console.WriteLine("The local model availability check timed out; AI drafts are disabled for this run.");
            }
        }
        using var localModelLifetime = localModel;

        using var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;

        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions { Headless = false });

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            await page.AddInitScriptAsync(MouseIdleTrackingScript);
            await page.EvaluateAsync(MouseIdleTrackingScript);
            await page.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            var seenListings = new HashSet<string>(StringComparer.Ordinal);
            Console.WriteLine("A persistent Chromium window is open. Sign in to LinkedIn there if needed.");
            Console.WriteLine("When the job search results are visible, press Enter here to begin.");
            if (Console.ReadLine() is null)
            {
                Console.WriteLine("No console input is available; closing without scanning.");
                return 0;
            }

            while (!cancellation.IsCancellationRequested)
            {
                await ProcessListingsAsync(
                    page,
                    appliedJobs,
                    appliedJobsPath,
                    uncertainJobs,
                    uncertainJobsPath,
                    confirmedAnswers,
                    confirmedAnswersPath,
                    questionAnswers,
                    questionAnswersPath,
                    resumeAnswers,
                    resumeText,
                    residenceCityStateZip,
                    personalProfile,
                    localModelAvailable ? localModel : null,
                    seenListings,
                    cancellation.Token);

                if (page.IsClosed)
                {
                    Console.WriteLine("The LinkedIn browser window was closed. Scan finished.");
                    break;
                }

                Console.WriteLine();
                Console.WriteLine("Finished scanning the listings currently loaded in LinkedIn.");
                Console.WriteLine("The browser is still open and signed in. Load more results or adjust the search there,");
                Console.WriteLine("then press Enter to scan again, or type Q and press Enter to exit.");
                var input = Console.ReadLine();
                var action = ScanCompletion.ParseAction(input);
                if (action == ScanCompletionAction.Exit)
                {
                    break;
                }
                if (action == ScanCompletionAction.Invalid)
                {
                    Console.WriteLine("Enter only Enter (scan again) or Q (exit).");
                }
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Run cancelled. No further form or listing actions will be taken.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LinkedIn runner failed with an exception:");
            Console.Error.WriteLine(ex.ToString());
            if (context is not null)
            {
                Console.WriteLine("Chromium and the failed application page are being kept open for troubleshooting. Press Enter after inspecting them to close the browser and exit.");
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
        }
    }

    private static async Task<bool> WaitForMouseIdleAsync(
        IPage page,
        TimeSpan idleDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.IsClosed)
        {
            return false;
        }

        var startingUrl = page.Url;
        double startingDocumentId;
        try
        {
            // Start a fresh idle window when the question page is detected, not when
            // the browser document was first loaded.
            startingDocumentId = await page.EvaluateAsync<double>(MouseIdleTrackingScript);
        }
        catch (PlaywrightException ex)
        {
            if (!page.IsClosed && page.Url == startingUrl)
            {
                Console.Error.WriteLine($"Could not start the mouse-idle wait: {ex.Message}");
            }
            return false;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed || page.Url != startingUrl)
            {
                return false;
            }

            double[] idleState;
            try
            {
                idleState = await page.EvaluateAsync<double[]>(
                    """
                    () => {
                        const state = window.__linkedinJobSearchMouseIdle;
                        return state
                            ? [state.documentId, state.inactivityMs]
                            : [-1, -1];
                    }
                    """);
            }
            catch (PlaywrightException ex)
            {
                if (!page.IsClosed && page.Url == startingUrl)
                {
                    Console.Error.WriteLine($"Could not monitor mouse inactivity: {ex.Message}");
                }
                return false;
            }

            if (idleState.Length != 2 || idleState[0] != startingDocumentId || idleState[1] < 0)
            {
                return false;
            }
            var inactivity = TimeSpan.FromMilliseconds(idleState[1]);
            if (ManualAnswerWindow.HasBeenIdleFor(inactivity, idleDuration))
            {
                return true;
            }

            var remaining = idleDuration - inactivity;
            await Task.Delay(
                remaining < ManualAnswerWindow.PollInterval
                    ? remaining
                    : ManualAnswerWindow.PollInterval,
                cancellationToken);
        }
    }

    private static async Task ProcessListingsAsync(
        IPage page,
        HashSet<string> appliedJobs,
        string appliedJobsPath,
        HashSet<string> uncertainJobs,
        string uncertainJobsPath,
        ConfirmedAnswerStore confirmedAnswers,
        string confirmedAnswersPath,
        QuestionAnswerStore questionAnswers,
        string questionAnswersPath,
        ResumeAnswerStore resumeAnswers,
        string resumeText,
        string? residenceCityStateZip,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        HashSet<string> seenThisRun,
        CancellationToken cancellationToken)
    {
        var stagnantRounds = 0;
        var reportedMissingListings = false;

        while (stagnantRounds < 3)
        {
            var listings = await ReadVisibleJobListingsAsync(page);
            var newListings = listings.Where(listing => seenThisRun.Add(listing.Identity)).ToList();
            if (listings.Count == 0 && !reportedMissingListings)
            {
                var candidateCount = await page.Locator(JobListingDiscovery.CardSelector).CountAsync();
                var jobLinkCount = await page.Locator("a[href*='/jobs/view/']").CountAsync();
                var currentDetailsJobId = JobListingDiscovery.ExtractCurrentJobId(page.Url);
                Console.WriteLine(
                    $"No visible left-pane listing cards were recognized " +
                    $"(candidate elements: {candidateCount}; left-pane cards: {listings.Count}; " +
                    $"job-view links: {jobLinkCount}; current details job ID: {currentDetailsJobId ?? "none"}). " +
                    "The current details job ID is not treated as a result-card ID. " +
                    "Confirm the result list is visible and try scanning again.");
                reportedMissingListings = true;
            }

            foreach (var listing in newListings)
            {
                var jobId = listing.JobId;
                var card = listing.Card;
                var cardText = await card.InnerTextAsync();
                var title = FirstNonEmptyLine(cardText);
                if (ContainsAppliedMarker(cardText))
                {
                    if (jobId is not null)
                    {
                        appliedJobs.Add(jobId);
                        await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
                    }
                    Console.WriteLine($"Skipping listing marked as already applied: {title} ({jobId ?? "ID unavailable"})");
                    continue;
                }

                if (jobId is not null && appliedJobs.Contains(jobId))
                {
                    Console.WriteLine($"Skipping already-applied listing: {title} ({jobId})");
                    continue;
                }
                if (jobId is not null && uncertainJobs.Contains(jobId))
                {
                    Console.WriteLine($"Skipping listing with an unconfirmed prior submission: {title} ({jobId})");
                    continue;
                }

                var previousDetailsJobId = JobListingDiscovery.ExtractCurrentJobId(page.Url);
                Console.WriteLine(
                    $"Selecting left-pane listing: {title} " +
                    $"({jobId ?? "ID pending selection"}; current details ID before selection: {previousDetailsJobId ?? "none"}).");
                var jobLink = card.Locator("a[href*='/jobs/view/']").First;
                if (await jobLink.CountAsync() > 0 && await jobLink.IsVisibleAsync())
                {
                    await jobLink.ClickAsync();
                }
                else
                {
                    await card.ClickAsync();
                }
                await page.WaitForTimeoutAsync(700);
                jobId ??= JobListingDiscovery.ExtractCurrentJobId(page.Url);
                if (jobId is null)
                {
                    Console.WriteLine(
                        "  The listing was selected, but no job ID could be verified from its card or updated details URL; " +
                        "skipping application controls to avoid processing the wrong listing.");
                    continue;
                }
                if (appliedJobs.Contains(jobId))
                {
                    Console.WriteLine($"  Selected listing is already recorded as applied ({jobId}); skipping.");
                    continue;
                }
                if (uncertainJobs.Contains(jobId))
                {
                    Console.WriteLine($"  Selected listing has an unconfirmed prior submission ({jobId}); skipping.");
                    continue;
                }

                var easyApplyButtons = await GetVisibleButtonsAsync(
                    page.GetByRole(AriaRole.Button, new() { Name = "Easy Apply", Exact = true }));
                if (easyApplyButtons.Count > 1)
                {
                    Console.WriteLine("  Multiple Easy Apply buttons are visible; moving on rather than guessing.");
                    continue;
                }

                var externalApplyLinks = easyApplyButtons.Count == 1
                    ? []
                    : await GetVisibleExternalApplyLinksAsync(page);
                var applyButtons = easyApplyButtons.Count == 1 || externalApplyLinks.Count == 1
                    ? []
                    : await GetVisibleButtonsAsync(
                        page.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                var buttonKind = ApplyButtonHandling.Select(
                    easyApplyButtons.Count,
                    externalApplyLinks.Count,
                    applyButtons.Count);

                if (buttonKind == ApplyButtonKind.ExternalApplyLink)
                {
                    if (ShouldSkipApplicationUrl(externalApplyLinks[0].Destination.AbsoluteUri))
                    {
                        Console.WriteLine($"  Skipping excluded application URL: {externalApplyLinks[0].Destination}");
                        continue;
                    }
                    if (!await OpenExternalApplyAndPauseAsync(
                            page,
                            externalApplyLinks[0].Destination,
                            title,
                            jobId,
                            appliedJobs,
                            appliedJobsPath,
                            resumeText,
                            residenceCityStateZip,
                            personalProfile,
                            localModel,
                            cancellationToken))
                    {
                        return;
                    }
                    continue;
                }

                if (buttonKind == ApplyButtonKind.None)
                {
                    if (externalApplyLinks.Count > 1)
                    {
                        Console.WriteLine("  Multiple external Apply links are visible; moving on rather than guessing.");
                    }
                    else
                    {
                        Console.WriteLine("  No unique supported Apply button or external Apply link; moving on.");
                    }
                    continue;
                }

                var applyButton = buttonKind == ApplyButtonKind.EasyApply
                    ? easyApplyButtons[0]
                    : applyButtons[0];
                var pageUriBeforeApply = new Uri(page.Url);
                if (buttonKind == ApplyButtonKind.Apply &&
                    ApplyButtonHandling.IsExternalDestination(
                        pageUriBeforeApply,
                        await applyButton.GetAttributeAsync("href")))
                {
                    var destination = ApplyButtonHandling.ResolveDestination(
                        pageUriBeforeApply,
                        await applyButton.GetAttributeAsync("href"));
                    if (destination is not null && ShouldSkipApplicationUrl(destination.AbsoluteUri))
                    {
                        Console.WriteLine($"  Skipping excluded application URL: {destination}");
                        continue;
                    }
                    if (destination is null ||
                        !await OpenExternalApplyAndPauseAsync(
                            page,
                            destination,
                            title,
                            jobId,
                            appliedJobs,
                            appliedJobsPath,
                            resumeText,
                            residenceCityStateZip,
                            personalProfile,
                            localModel,
                            cancellationToken))
                    {
                        return;
                    }
                    continue;
                }

                var pagesBeforeApply = page.Context.Pages.ToHashSet();
                await applyButton.ClickAsync();
                await page.WaitForTimeoutAsync(500);
                var openedPages = page.Context.Pages.Where(openedPage => !pagesBeforeApply.Contains(openedPage)).ToList();
                if (openedPages.Count > 0)
                {
                    foreach (var openedPage in openedPages)
                    {
                        await openedPage.CloseAsync();
                    }
                    Console.WriteLine("  Apply opened a separate page; closed it and skipped this unsupported flow.");
                    continue;
                }

                if (buttonKind == ApplyButtonKind.Apply &&
                    ApplyButtonHandling.IsExternalDestination(pageUriBeforeApply, page.Url))
                {
                    if (ShouldSkipApplicationUrl(page.Url))
                    {
                        Console.WriteLine($"  Skipping excluded application URL: {page.Url}");
                        try
                        {
                            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                        }
                        catch (PlaywrightException)
                        {
                            Console.WriteLine("  Could not return to LinkedIn after skipping the external URL; stopping this run.");
                            return;
                        }
                        continue;
                    }
                    Console.WriteLine("  Apply navigated to an external site; stopping without interacting with it.");
                    return;
                }

                var result = await CompleteEasyApplyAsync(
                    page,
                    title,
                    confirmedAnswers,
                    confirmedAnswersPath,
                    questionAnswers,
                    questionAnswersPath,
                    resumeAnswers,
                    resumeText,
                    residenceCityStateZip,
                    personalProfile,
                    localModel,
                    cancellationToken);

                if (buttonKind == ApplyButtonKind.Apply &&
                    result == ApplyResult.UnsupportedDialog &&
                    new Uri(page.Url) != pageUriBeforeApply)
                {
                    Console.WriteLine("  Apply opened an unsupported on-site flow; stopping without interacting with it.");
                    return;
                }

                if (result == ApplyResult.WaitInterrupted)
                {
                    Console.WriteLine("  Browser page changed or closed during the idle wait; stopping this run safely.");
                    return;
                }
                if (result == ApplyResult.ManualReviewPending)
                {
                    uncertainJobs.Add(jobId);
                    await SaveListingIdsAsync(uncertainJobsPath, uncertainJobs);
                    Console.WriteLine("  Submission is unconfirmed; recorded this listing to prevent applying again on a rerun.");
                    Console.WriteLine("  Manual application review is still pending; stopping without moving to another listing.");
                    return;
                }

                if (result == ApplyResult.Submitted)
                {
                    appliedJobs.Add(jobId);
                    await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
                    Console.WriteLine("  Application submission was confirmed.");
                }
                else
                {
                    if (result == ApplyResult.UnconfirmedSubmission)
                    {
                        uncertainJobs.Add(jobId);
                        await SaveListingIdsAsync(uncertainJobsPath, uncertainJobs);
                    }
                    Console.WriteLine($"  {result}; moving on without guessing or retrying.");
                }
            }

            var hasUnseenListings = (await ReadVisibleJobListingsAsync(page))
                .Any(listing => !seenThisRun.Contains(listing.Identity));
            if (hasUnseenListings)
            {
                stagnantRounds = 0;
                continue;
            }

            Console.WriteLine("No unprocessed visible listings remain; scrolling the LinkedIn results pane for more.");
            var didScroll = await ScrollResultsAsync(page);
            await page.WaitForTimeoutAsync(900);
            hasUnseenListings = (await ReadVisibleJobListingsAsync(page))
                .Any(listing => !seenThisRun.Contains(listing.Identity));
            if (!didScroll && !hasUnseenListings)
            {
                if (await AdvanceResultsPageAsync(page, cancellationToken))
                {
                    stagnantRounds = 0;
                    continue;
                }
                Console.WriteLine("  The left results pane did not move and no next page advanced; checking again for newly loaded listings.");
            }
            stagnantRounds = didScroll || hasUnseenListings ? 0 : stagnantRounds + 1;
        }
    }

    private static async Task<List<JobListing>> ReadVisibleJobListingsAsync(IPage page)
    {
        var cards = page.Locator(JobListingDiscovery.CardSelector);
        var count = await cards.CountAsync();
        var listings = new List<JobListing>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var viewportWidth = await page.EvaluateAsync<double>("() => window.innerWidth");
        var viewportHeight = await page.EvaluateAsync<double>("() => window.innerHeight");

        for (var index = 0; index < count; index++)
        {
            var card = cards.Nth(index);
            if (!await card.IsVisibleAsync())
            {
                continue;
            }

            var bounds = await card.BoundingBoxAsync();
            if (bounds is null)
            {
                continue;
            }

            var links = card.Locator("a[href*='/jobs/view/']");
            var hrefs = new List<string?>();
            for (var linkIndex = 0; linkIndex < await links.CountAsync(); linkIndex++)
            {
                hrefs.Add(await links.Nth(linkIndex).GetAttributeAsync("href"));
            }

            var id = JobListingDiscovery.ExtractJobId(
                await card.GetAttributeAsync("data-job-id"),
                await card.GetAttributeAsync("data-occludable-job-id"),
                hrefs);
            var text = await card.InnerTextAsync();
            if (!JobListingDiscovery.IsLeftPaneCandidate(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    viewportWidth,
                    viewportHeight,
                    text))
            {
                continue;
            }

            var identity = JobListingDiscovery.CreateIdentity(id, text);
            if (ids.Add(identity))
            {
                listings.Add(new JobListing(identity, id, card));
            }
        }

        return listings;
    }

    private sealed record JobListing(string Identity, string? JobId, ILocator Card);

    private static async Task<List<ILocator>> GetVisibleButtonsAsync(ILocator buttons)
    {
        var visibleButtons = new List<ILocator>();
        for (var index = 0; index < await buttons.CountAsync(); index++)
        {
            var button = buttons.Nth(index);
            if (await button.IsVisibleAsync())
            {
                visibleButtons.Add(button);
            }
        }

        return visibleButtons;
    }

    private static async Task<List<ExternalApplyLink>> GetVisibleExternalApplyLinksAsync(IPage page)
    {
        var links = page.Locator("a[href]");
        var matches = new List<ExternalApplyLink>();
        var currentPage = new Uri(page.Url);
        for (var index = 0; index < await links.CountAsync(); index++)
        {
            var link = links.Nth(index);
            if (!await link.IsVisibleAsync())
            {
                continue;
            }

            var applySpanText = "";
            var spans = link.Locator("span");
            for (var spanIndex = 0; spanIndex < await spans.CountAsync(); spanIndex++)
            {
                var span = spans.Nth(spanIndex);
                if (!await span.IsVisibleAsync())
                {
                    continue;
                }

                var text = await span.InnerTextAsync();
                if (ApplyButtonHandling.IsApplySpanText(text))
                {
                    applySpanText = text;
                    break;
                }
            }

            var href = await link.GetAttributeAsync("href");
            if (!ApplyButtonHandling.IsExternalApplyLink(currentPage, applySpanText, href))
            {
                continue;
            }

            var destination = ApplyButtonHandling.ResolveDestination(currentPage, href);
            if (destination is not null)
            {
                matches.Add(new ExternalApplyLink(destination));
            }
        }

        return matches;
    }

    private static async Task<bool> OpenExternalApplyAndPauseAsync(
        IPage sourcePage,
        Uri destination,
        string jobTitle,
        string jobId,
        HashSet<string> appliedJobs,
        string appliedJobsPath,
        string resumeText,
        string? residenceCityStateZip,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        CancellationToken cancellationToken)
    {
        var keepExternalPageForTroubleshooting = false;
        if (!ApplyButtonHandling.IsSafeExternalDestination(destination))
        {
            Console.Error.WriteLine($"  External Apply destination is not HTTPS; refusing to open it: {destination}");
            return !sourcePage.IsClosed;
        }

        var externalPage = await sourcePage.Context.NewPageAsync();
        try
        {
            await externalPage.GotoAsync(
                destination.AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (ShouldSkipApplicationUrl(externalPage.Url))
            {
                Console.WriteLine($"  Skipping excluded application URL: {externalPage.Url}");
                return !sourcePage.IsClosed;
            }
            if (externalPage.Url.Contains("myworkdayjobs", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Workday application detected; closing this tab and skipping to the next LinkedIn listing.");
                return !sourcePage.IsClosed;
            }
            await AcceptCookieBannerAsync(externalPage);
            Console.WriteLine();
            Console.WriteLine($"External company application opened for {jobTitle}.");
            var submitted = await CompleteExternalApplicationAsync(
                externalPage,
                jobTitle,
                localModel,
                resumeText,
                residenceCityStateZip,
                personalProfile,
                cancellationToken);
            if (submitted || ContainsExternalConfirmation(await externalPage.Locator("body").InnerTextAsync()))
            {
                appliedJobs.Add(jobId);
                await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
                Console.WriteLine("  External application confirmation detected and recorded.");
                return true;
            }

            Console.WriteLine();
            Console.WriteLine("Finish any unanswered application questions and submit the application in the open job tab.");
            Console.WriteLine("If the page already shows a success message, do not submit again. Press SPACE here to close this tab and continue to LinkedIn's next listing.");
            if (Console.IsInputRedirected)
            {
                Console.WriteLine("Spacebar input is unavailable in redirected input; press Enter after finishing instead.");
                if (await Console.In.ReadLineAsync(cancellationToken) is null)
                {
                    return false;
                }
            }
            else
            {
                ConsoleKeyInfo key;
                do
                {
                    key = Console.ReadKey(intercept: true);
                } while (key.Key != ConsoleKey.Spacebar);
            }

            if (sourcePage.IsClosed)
            {
                return false;
            }
            if (!externalPage.IsClosed &&
                ContainsExternalConfirmation(await externalPage.Locator("body").InnerTextAsync()))
            {
                Console.WriteLine("  External application confirmation detected.");
            }
            appliedJobs.Add(jobId);
            await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
            Console.WriteLine("  Recorded this job after your manual completion handoff.");
            return true;
        }
        catch
        {
            keepExternalPageForTroubleshooting = true;
            throw;
        }
        finally
        {
            if (!keepExternalPageForTroubleshooting && !externalPage.IsClosed)
            {
                await externalPage.CloseAsync();
            }
        }
    }

    private static async Task<bool> CompleteExternalApplicationAsync(
        IPage page,
        string jobTitle,
        LocalModelAnswerClient? localModel,
        string resumeText,
        string? residenceCityStateZip,
        PersonalProfile? personalProfile,
        CancellationToken cancellationToken)
    {
        for (var step = 0; step < 12; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                return false;
            }

            if (ContainsExternalConfirmation(await page.Locator("body").InnerTextAsync()))
            {
                return true;
            }

            if (page.Url.Contains("theladders", StringComparison.OrdinalIgnoreCase))
            {
                return await CompleteTheLaddersApplicationAsync(page, cancellationToken);
            }

            try
            {
                var draftResult = await ExternalApplicationFormAssistant.DraftBlankFieldsAsync(
                    page,
                    string.IsNullOrWhiteSpace(resumeText) ? null : localModel,
                    jobTitle,
                    resumeText,
                    residenceCityStateZip,
                    cancellationToken,
                    personalProfile);
                if (draftResult.FilledCount > 0)
                {
                    Console.WriteLine($"  Filled {draftResult.FilledCount} blank field(s) from resume-supported answers.");
                }
                if (draftResult.UnansweredQuestions.Count > 0)
                {
                    Console.WriteLine("  These questions need your input:");
                    foreach (var question in draftResult.UnansweredQuestions)
                    {
                        Console.WriteLine($"    - {question}");
                    }
                    return false;
                }
            }
            catch (HttpRequestException)
            {
                Console.WriteLine("  Local model request failed; remaining form fields are left for you.");
                return false;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("  Local model request timed out; remaining form fields are left for you.");
                return false;
            }
            catch (JsonException)
            {
                Console.WriteLine("  Local model response was invalid; remaining form fields are left for you.");
                return false;
            }

            var invalidRequired = page.Locator(
                "input:required:invalid, textarea:required:invalid, select:required:invalid, " +
                "[aria-required='true']:invalid");
            if (await invalidRequired.CountAsync() > 0)
            {
                Console.WriteLine($"  {await invalidRequired.CountAsync()} required field(s) still need your input.");
                return false;
            }

            var form = page.Locator("form").Last;
            var scope = await page.Locator("form").CountAsync() > 0 ? form : page.Locator("body");
            var submit = await FindUniqueVisibleButtonAsync(scope, [
                "Submit application", "Submit", "Apply now", "Send application", "Finish application"]);
            if (submit is not null)
            {
                await submit.ClickAsync();
                for (var confirmationWait = 0; confirmationWait < 10; confirmationWait++)
                {
                    await page.WaitForTimeoutAsync(500);
                    if (ContainsExternalConfirmation(await page.Locator("body").InnerTextAsync()))
                    {
                        return true;
                    }
                }
                Console.WriteLine("  Submission confirmation was not detected; stopping automatic submission to avoid a duplicate.");
                return false;
            }

            var next = await FindUniqueVisibleButtonAsync(scope, ["Next", "Continue", "Review", "Proceed"]);
            if (next is not null)
            {
                await next.ClickAsync();
                await page.WaitForTimeoutAsync(700);
                continue;
            }

            return false;
        }

        Console.WriteLine("  Reached the form navigation limit; leaving the remaining steps for you.");
        return false;
    }

    private static async Task<bool> CompleteTheLaddersApplicationAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        for (var section = 0; section < 4; section++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                return false;
            }

            ILocator? saveAndNext = null;
            for (var lookupAttempt = 0; lookupAttempt < 10; lookupAttempt++)
            {
                await ScrollPageToBottomAsync(page);
                var buttons = await GetVisibleButtonsAsync(
                    page.GetByRole(AriaRole.Button, new() { Name = "Save & Go to Next Section" }));
                if (buttons.Count > 1)
                {
                    Console.WriteLine($"  TheLadders section {section + 1}: multiple Save & Go to Next Section buttons are visible.");
                    return false;
                }
                if (buttons.Count == 1)
                {
                    saveAndNext = buttons[0];
                    break;
                }
                await page.WaitForTimeoutAsync(300);
            }

            if (saveAndNext is null)
            {
                Console.WriteLine($"  TheLadders section {section + 1}: expected Save & Go to Next Section button was not uniquely visible.");
                return false;
            }

            await saveAndNext.ClickAsync();
            await page.WaitForTimeoutAsync(1200);
        }

        await ScrollPageToBottomAsync(page);
        var checkboxes = page.Locator("input[type='checkbox']");
        var visibleCheckboxes = new List<ILocator>();
        for (var index = 0; index < await checkboxes.CountAsync(); index++)
        {
            var checkbox = checkboxes.Nth(index);
            if (await checkbox.IsVisibleAsync() && await checkbox.IsEnabledAsync())
            {
                visibleCheckboxes.Add(checkbox);
            }
        }
        if (visibleCheckboxes.Count != 2)
        {
            Console.WriteLine($"  TheLadders final page has {visibleCheckboxes.Count} visible enabled checkboxes; expected two, so leaving it for manual review.");
            return false;
        }

        foreach (var checkbox in visibleCheckboxes)
        {
            if (!await checkbox.IsCheckedAsync())
            {
                await checkbox.CheckAsync();
            }
        }

        await ScrollPageToBottomAsync(page);
        var applyButtons = await GetVisibleButtonsAsync(
            page.GetByRole(AriaRole.Button, new() { Name = "Apply to job" }));
        var apply = applyButtons.Count == 1 ? applyButtons[0] : null;
        if (apply is null)
        {
            Console.WriteLine("  TheLadders Apply to job button was not uniquely visible; leaving the final page for manual review.");
            return false;
        }

        await apply.ClickAsync();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await page.WaitForTimeoutAsync(500);
            if (page.IsClosed)
            {
                return false;
            }
            if (ContainsExternalConfirmation(await page.Locator("body").InnerTextAsync()))
            {
                return true;
            }
        }

        Console.WriteLine("  TheLadders confirmation was not detected after Apply to job; leaving the page for manual review.");
        return false;
    }

    private static async Task ScrollPageToBottomAsync(IPage page)
    {
        await page.EvaluateAsync(
            """
            () => {
                const scrollables = Array.from(document.querySelectorAll('*'))
                    .filter(element => element.scrollHeight > element.clientHeight + 20)
                    .sort((left, right) => right.scrollHeight - left.scrollHeight);
                for (const element of scrollables) {
                    element.scrollTop = element.scrollHeight;
                }
                const scrollingElement = document.scrollingElement;
                if (scrollingElement) scrollingElement.scrollTop = scrollingElement.scrollHeight;
                window.scrollTo(0, document.documentElement.scrollHeight);
            }
            """);
        await page.WaitForTimeoutAsync(200);
    }

    private static async Task<ILocator?> FindUniqueVisibleButtonAsync(ILocator scope, IReadOnlyList<string> labels)
    {
        var matches = new List<ILocator>();
        foreach (var label in labels)
        {
            var buttons = await GetVisibleButtonsAsync(
                scope.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }));
            matches.AddRange(buttons);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool ContainsExternalConfirmation(string text) =>
        Regex.IsMatch(
            text,
            @"application (was )?(submitted|received)|we have received your application|thank you for applying|application has been sent",
            RegexOptions.IgnoreCase);

    private static async Task AcceptCookieBannerAsync(IPage page)
    {
        var labels = new[] { "Accept all cookies", "Accept all", "Accept cookies", "Accept", "OK", "Ok", "Okay", "I agree" };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            foreach (var label in labels)
            {
                var buttons = await GetVisibleButtonsAsync(
                    page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }));
                if (buttons.Count == 1)
                {
                    await buttons[0].ClickAsync();
                    Console.WriteLine($"  Accepted the cookie banner using '{label}'.");
                    return;
                }
            }
            await page.WaitForTimeoutAsync(300);
        }
    }

    private static async Task<bool> ScrollResultsAsync(IPage page)
    {
        var moved = await page.EvaluateAsync<bool>(
            """
            () => {
                const viewportWidth = window.innerWidth;
                const isScrollable = element => {
                    if (element.scrollHeight <= element.clientHeight + 20) return false;
                    const overflowY = getComputedStyle(element).overflowY;
                    return overflowY !== 'visible' && overflowY !== 'clip';
                };
                const cards = Array.from(document.querySelectorAll(
                    '.job-card-container, li[data-occludable-job-id], [data-job-id], ' +
                    '[data-occludable-job-id], li.jobs-search-results__list-item, [data-view-name="job-card"]'
                ));
                let resultsPane = null;
                for (const card of cards) {
                    const cardRect = card.getBoundingClientRect();
                    if (cardRect.width < 100 || cardRect.height < 30 || cardRect.left >= viewportWidth * 0.6) continue;
                    for (let ancestor = card.parentElement; ancestor && ancestor !== document.body; ancestor = ancestor.parentElement) {
                        const rect = ancestor.getBoundingClientRect();
                        if (rect.left >= 0 && rect.right < viewportWidth * 0.65 &&
                            rect.width > 180 && isScrollable(ancestor)) {
                            resultsPane = ancestor;
                            break;
                        }
                    }
                    if (resultsPane) break;
                }

                if (!resultsPane) {
                    const paneCandidates = Array.from(document.querySelectorAll(
                        '.jobs-search-results-list, .scaffold-layout__list, ' +
                        '[data-testid="jobs-search-results-list"], [role="list"]'
                    ));
                    resultsPane = paneCandidates.find(element => {
                        const rect = element.getBoundingClientRect();
                        return rect.left >= 0 && rect.right < viewportWidth * 0.65 &&
                            rect.width > 180 && isScrollable(element);
                    }) || null;
                }

                if (resultsPane) {
                    const before = resultsPane.scrollTop;
                    const distance = Math.max(250, Math.floor(resultsPane.clientHeight * 0.8));
                    resultsPane.scrollTop = Math.min(
                        resultsPane.scrollHeight,
                        before + distance
                    );
                    resultsPane.dispatchEvent(new Event('scroll', { bubbles: true }));
                    if (resultsPane.scrollTop > before + 1) return true;
                }

                const pointX = Math.floor(viewportWidth * 0.25);
                const pointY = Math.floor(window.innerHeight * 0.62);
                let target = document.elementFromPoint(pointX, pointY);
                for (; target && target !== document.body; target = target.parentElement) {
                    if (target.scrollHeight > target.clientHeight + 20) {
                        const before = target.scrollTop;
                        target.scrollTop = Math.min(
                            target.scrollHeight,
                            before + Math.max(250, Math.floor(target.clientHeight * 0.8))
                        );
                        target.dispatchEvent(new Event('scroll', { bubbles: true }));
                        if (target.scrollTop > before + 1) return true;
                    }
                }
                return false;
            }
            """);
        if (moved)
        {
            return true;
        }

        var viewport = await page.EvaluateAsync<double[]>("() => [window.innerWidth, window.innerHeight]");
        var beforeOffset = await ReadLeftPaneScrollOffsetAsync(page);
        await page.Mouse.MoveAsync((float)(viewport[0] * 0.25), (float)(viewport[1] * 0.62));
        await page.Mouse.WheelAsync(0, (float)Math.Max(500, viewport[1] * 0.75));
        await page.WaitForTimeoutAsync(250);
        var afterOffset = await ReadLeftPaneScrollOffsetAsync(page);
        return afterOffset > beforeOffset + 1;
    }

    private static async Task<double> ReadLeftPaneScrollOffsetAsync(IPage page) =>
        await page.EvaluateAsync<double>(
            """
            () => Array.from(document.querySelectorAll('*'))
                .filter(element => {
                    const rect = element.getBoundingClientRect();
                    return element !== document.scrollingElement
                        && rect.left >= 0
                        && rect.right < window.innerWidth * 0.65
                        && rect.width > 180
                        && element.scrollHeight > element.clientHeight + 20;
                })
                .reduce((total, element) => total + element.scrollTop, 0)
            """);

    private static async Task<bool> AdvanceResultsPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var nextButtons = await GetVisibleButtonsAsync(
            page.Locator("button[data-testid='pagination-controls-next-button-visible']"));
        if (nextButtons.Count != 1 || !await nextButtons[0].IsEnabledAsync())
        {
            return false;
        }

        var previousIdentities = (await ReadVisibleJobListingsAsync(page))
            .Select(listing => listing.Identity)
            .ToHashSet(StringComparer.Ordinal);
        var previousDetailsJobId = JobListingDiscovery.ExtractCurrentJobId(page.Url);
        Console.WriteLine("  Left pane is at its end; clicking LinkedIn's Next page button.");
        await nextButtons[0].ClickAsync();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(300);
            if (page.IsClosed)
            {
                return false;
            }

            var currentJobId = JobListingDiscovery.ExtractCurrentJobId(page.Url);
            var currentListings = await ReadVisibleJobListingsAsync(page);
            if ((currentJobId is not null && currentJobId != previousDetailsJobId) ||
                currentListings.Any(listing => !previousIdentities.Contains(listing.Identity)))
            {
                await page.WaitForTimeoutAsync(500);
                return true;
            }
        }

        Console.WriteLine("  Next page was clicked, but LinkedIn did not show different results.");
        return false;
    }

    private static async Task<ApplyResult> CompleteEasyApplyAsync(
        IPage page,
        string jobTitle,
        ConfirmedAnswerStore confirmedAnswers,
        string confirmedAnswersPath,
        QuestionAnswerStore questionAnswers,
        string questionAnswersPath,
        ResumeAnswerStore resumeAnswers,
        string resumeText,
        string? residenceCityStateZip,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        CancellationToken cancellationToken)
    {
        var modal = page.Locator("[role='dialog'], .jobs-easy-apply-modal").Last;
        if (await modal.CountAsync() == 0 || !await modal.IsVisibleAsync())
        {
            return ApplyResult.UnsupportedDialog;
        }

        var reviewReached = false;
        for (var step = 0; step < 8; step++)
        {
            var modalText = await modal.InnerTextAsync();
            if (ContainsConfirmation(modalText))
            {
                await CloseCompletedModalAsync(modal, page);
                return ApplyResult.Submitted;
            }

            if (Regex.IsMatch(modalText, @"\breview (your )?application\b", RegexOptions.IgnoreCase))
            {
                reviewReached = true;
            }

            var inputs = modal.Locator("input:not([type='hidden']):not([type='submit']):not([type='button']), textarea, select");
            var inputCount = await inputs.CountAsync();
            var resumeFlow = Regex.IsMatch(
                modalText,
                @"\b(?:select|choose|upload|attach)\b.{0,50}\bresume\b|\bresume\b.{0,50}\b(?:select|choose|upload|attach)\b",
                RegexOptions.IgnoreCase);
            var unsupportedInput = false;
            var applicationQuestions = new List<PendingAnswerField>();
            var seenRadioGroups = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < inputCount; i++)
            {
                var input = inputs.Nth(i);
                var type = (await input.GetAttributeAsync("type") ?? "").ToLowerInvariant();
                var tagName = await input.EvaluateAsync<string>("element => element.tagName.toLowerCase()");
                if (tagName == "select")
                {
                    type = "select";
                }
                else if (tagName == "textarea")
                {
                    type = "textarea";
                }

                if (!await input.IsVisibleAsync())
                {
                    if (type is not ("radio" or "checkbox"))
                    {
                        continue;
                    }

                    var optionLabel = input.Locator("xpath=ancestor::label[1]");
                    if (await optionLabel.CountAsync() == 0 || !await optionLabel.IsVisibleAsync())
                    {
                        continue;
                    }
                }

                var label = string.Join(" ", new[]
                {
                    await input.GetAttributeAsync("aria-label"),
                    await input.GetAttributeAsync("placeholder"),
                    await input.GetAttributeAsync("name"),
                    await input.GetAttributeAsync("autocomplete")
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

                if (type == "radio" && resumeFlow)
                {
                    continue;
                }
                if (type == "radio")
                {
                    var name = await input.GetAttributeAsync("name");
                    var groupKey = string.IsNullOrWhiteSpace(name) ? $"radio-{i}" : $"radio-{name}";
                    if (seenRadioGroups.Add(groupKey))
                    {
                        var group = await GetRadioGroupAsync(modal, name, input);
                        var question = await GetQuestionTextAsync(input);
                        applicationQuestions.Add(new PendingAnswerField(question, type, group));
                    }
                    continue;
                }

                if (type is "file" or "password")
                {
                    unsupportedInput = true;
                    continue;
                }

                if (await input.IsDisabledAsync() || await input.GetAttributeAsync("readonly") is not null)
                {
                    continue;
                }

                if (Regex.IsMatch(label, @"\b(location|city)\b", RegexOptions.IgnoreCase) ||
                    (string.IsNullOrWhiteSpace(label) &&
                     Regex.IsMatch(modalText, @"\b(location|city|where are you located)\b", RegexOptions.IgnoreCase) &&
                     inputCount == 1 &&
                     (type == "text" || type.Length == 0)))
                {
                    var question = await GetQuestionTextAsync(input);
                    applicationQuestions.Add(new PendingAnswerField(question, type, [input]));
                    continue;
                }

                if (type == "select")
                {
                    var question = await GetQuestionTextAsync(input);
                    applicationQuestions.Add(new PendingAnswerField(question, type, [input]));
                    continue;
                }

                if (type == "checkbox")
                {
                    var labelText = await ReadControlLabelAsync(input);
                    if (Regex.IsMatch(labelText, @"follow.*company|company.*follow", RegexOptions.IgnoreCase))
                    {
                        continue;
                    }

                    var question = await GetQuestionTextAsync(input);
                    applicationQuestions.Add(new PendingAnswerField(question, type, [input]));
                }
                else
                {
                    var question = await GetQuestionTextAsync(input);
                    applicationQuestions.Add(new PendingAnswerField(question, type, [input]));
                }
            }

            var hasQuestionSection = Regex.IsMatch(
                modalText,
                @"\b(application questions?|screening questions?|additional questions?|pre-?screening questions?)\b",
                RegexOptions.IgnoreCase);
            var isQuestionPage = ManualAnswerWindow.ShouldWaitForQuestionPage(
                applicationQuestions.Count > 0,
                hasQuestionSection,
                resumeFlow,
                isReviewPage: Regex.IsMatch(modalText, @"\breview (your )?application\b", RegexOptions.IgnoreCase));

            if (!isQuestionPage)
            {
                applicationQuestions.Clear();
            }

            if (isQuestionPage)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"Application question(s) detected. The app will wait until the mouse has been still inside this Playwright " +
                    $"browser page for {ManualAnswerWindow.IdleDuration.TotalSeconds:0} seconds. Any page mouse movement resets " +
                    "the timer. The app will not edit fields, advance, close this dialog, or move to another listing while waiting:");
                foreach (var question in applicationQuestions)
                {
                    Console.WriteLine($"  - {question.Question}");
                }

                if (!await WaitForMouseIdleAsync(page, ManualAnswerWindow.IdleDuration, cancellationToken))
                {
                    return ApplyResult.WaitInterrupted;
                }
            }

            if (unsupportedInput)
            {
                Console.WriteLine(
                    "This application has a field the app cannot safely handle. "
                    + "Complete or skip it manually in the browser, then press Enter "
                    + "to continue (Ctrl+C stops the run).");
                var manualInput = await Console.In.ReadLineAsync(cancellationToken);
                if (manualInput is null)
                {
                    return ApplyResult.ManualReviewPending;
                }
                continue;
            }

            if (resumeFlow)
            {
                if (!await SelectNewestResumeAsync(modal))
                {
                    await CloseModalAsync(modal, page);
                    return ApplyResult.UnsupportedDialog;
                }
            }

            if (isQuestionPage)
            {
                if (!await modal.IsVisibleAsync())
                {
                    return ApplyResult.UnsupportedDialog;
                }

                var stillVisibleQuestions = new List<PendingAnswerField>();
                foreach (var field in applicationQuestions)
                {
                    if (await IsPendingFieldVisibleAsync(field))
                    {
                        stillVisibleQuestions.Add(field);
                    }
                }
                if (!ManualAnswerWindow.ShouldReviewQuestionPage(
                        isQuestionPage,
                        stillVisibleQuestions.Count,
                        modalIsVisible: true))
                {
                    continue;
                }
                applicationQuestions = stillVisibleQuestions;
            }

            foreach (var pendingQuestion in applicationQuestions)
            {
                var browserAnswer = await GetCurrentAnswerAsync(pendingQuestion);
                if (string.IsNullOrWhiteSpace(pendingQuestion.Question) ||
                    !await ReviewAndApplyAnswerAsync(
                        pendingQuestion,
                        browserAnswer,
                        confirmedAnswers,
                        confirmedAnswersPath,
                        questionAnswers,
                        questionAnswersPath,
                        resumeAnswers,
                        resumeText))
                {
                    await CloseModalAsync(modal, page);
                    return ApplyResult.UnsupportedQuestion;
                }
            }

            var submitButton = modal.GetByRole(AriaRole.Button, new() { Name = "Submit application" });
            if (await submitButton.CountAsync() > 0 && await submitButton.IsVisibleAsync())
            {
                if (!reviewReached)
                {
                    await CloseModalAsync(modal, page);
                    return ApplyResult.UnsupportedDialog;
                }
            }

            static async Task<IReadOnlyList<ILocator>> GetRadioGroupAsync(
                    ILocator modal,
                    string? groupName,
                    ILocator current)
                {
                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        return [current];
                    }

                    var radios = modal.Locator("input[type='radio']");
                    var group = new List<ILocator>();
                    for (var index = 0; index < await radios.CountAsync(); index++)
                    {
                        var radio = radios.Nth(index);
                        if (await radio.GetAttributeAsync("name") == groupName)
                        {
                            group.Add(radio);
                        }
                    }

                    return group.Count > 0 ? group : [current];
                }

            static async Task<string> GetQuestionTextAsync(ILocator control) =>
                    await control.EvaluateAsync<string>(
                        """
                        element => {
                            const text = node => (node?.innerText || node?.textContent || '').trim();
                            if (element.type === 'radio') {
                                const group = element.closest('fieldset, [role="group"]');
                                const legend = group?.querySelector('legend');
                                if (text(legend)) return text(legend);
                                if (text(group)) return text(group);
                            }

                            const ids = (element.getAttribute('aria-labelledby') || '')
                                .split(/\s+/).filter(Boolean);
                            const labelledBy = ids.map(id => document.getElementById(id))
                                .map(text).filter(Boolean).join(' ');
                            if (labelledBy) return labelledBy;

                            const labels = Array.from(element.labels || [])
                                .map(text).filter(Boolean).join(' ');
                            if (labels) return labels;
                            return element.getAttribute('aria-label')
                                || element.getAttribute('placeholder')
                                || element.getAttribute('name')
                                || element.getAttribute('autocomplete')
                                || '';
                        }
                        """);

            async Task<bool> ReviewAndApplyAnswerAsync(
                    PendingAnswerField field,
                    string? browserAnswer,
                    ConfirmedAnswerStore confirmedAnswers,
                    string confirmedAnswersPath,
                    QuestionAnswerStore questionAnswers,
                    string questionAnswersPath,
                    ResumeAnswerStore resumeAnswers,
                    string resumeText)
                {
                    if (browserAnswer is null && personalProfile?.AnswerForQuestion(field.Question) is { } personalAnswer)
                    {
                        if (await ApplyAnswerAsync(field, personalAnswer))
                        {
                            Console.WriteLine("Applied the matching answer from local personal.md.");
                            return true;
                        }

                        Console.WriteLine($"The personal.md answer for this question did not match exactly one available choice; leaving it for manual review: {field.Question}");
                    }

                    if (ResidenceQuestion.IsMatch(field.Question) &&
                        !string.IsNullOrWhiteSpace(residenceCityStateZip) &&
                        await ApplyAnswerAsync(field, residenceCityStateZip))
                    {
                        Console.WriteLine("Applied the configured residence answer for this application only.");
                        return true;
                    }

                    var sensitive = AnswerMatcher.IsSensitive(field.Question);
                    var previousAnswer = AnswerMatcher.FindPreviousAnswer(field.Question, confirmedAnswers.Entries);
                    var savedKeyValueSuggestion = browserAnswer is null
                        ? questionAnswers.FindAnswer(field.Question)
                        : new AnswerSuggestion(AnswerSuggestionKind.None, null, 0);
                    var suggestion = browserAnswer is not null
                        ? new AnswerSuggestion(AnswerSuggestionKind.None, null, 0)
                        : savedKeyValueSuggestion.Kind != AnswerSuggestionKind.None
                            ? savedKeyValueSuggestion
                            : previousAnswer.Kind == AnswerSuggestionKind.None
                                ? resumeAnswers.FindAnswer(field.Question) is { Kind: not AnswerSuggestionKind.None } resumeAnswer
                                    ? resumeAnswer
                                    : AnswerMatcher.FindResumeExcerpt(field.Question, resumeText)
                                : previousAnswer;
                    LocalAnswerDraft? localDraft = null;
                    if (browserAnswer is null && suggestion.Answer is null && localModel is not null &&
                        (field.Type is "text" or "email" or "tel" or "textarea" or "select" or "radio"))
                    {
                        var options = field.Type is "select" or "radio"
                            ? await GetAvailableOptionsAsync(field)
                            : [];
                        localDraft = await localModel.CreateDraftAsync(
                            field.Question,
                            field.Type == "radio" ? "select" : field.Type,
                            jobTitle,
                            await page.TitleAsync(),
                            resumeText,
                            options,
                            cancellationToken);
                        if (localDraft is not null)
                        {
                            suggestion = new AnswerSuggestion(AnswerSuggestionKind.ResumeAnswer, localDraft.Answer, 1);
                        }
                    }
                    if (suggestion.Answer is not null &&
                        !ManualAnswerWindow.ShouldSuggestAnswer(browserAnswer, suggestion.Answer))
                    {
                        suggestion = new AnswerSuggestion(AnswerSuggestionKind.None, null, 0);
                    }

                    Console.WriteLine();
                    Console.WriteLine($"Application question: {field.Question}");
                    if (browserAnswer is not null)
                    {
                        Console.WriteLine($"Current answer in LinkedIn: {browserAnswer}");
                        Console.WriteLine("Choose: [Y] keep the browser answer, [E] edit/replace it, [S] skip.");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.PreviousAnswer)
                    {
                        Console.WriteLine(
                            $"Prior confirmed answer ({suggestion.Similarity:P0} Jaccard similarity to: " +
                            $"\"{suggestion.MatchedQuestion}\"): {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.SavedKeyValueAnswer)
                    {
                        Console.WriteLine(
                            $"Saved key/value answer ({suggestion.Similarity:P0} match to: " +
                            $"\"{suggestion.MatchedQuestion}\"): {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeExcerpt)
                    {
                        Console.WriteLine(
                            $"Resume excerpt suggestion ({suggestion.Similarity:P0} Jaccard similarity; " +
                            "quoted verbatim):");
                        Console.WriteLine($"  {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeAnswer)
                    {
                        Console.WriteLine(localDraft is null
                            ? "Resume-supported answer:"
                            : "Local model draft supported by this resume evidence:");
                        if (localDraft is not null)
                        {
                            Console.WriteLine($"  Evidence: {localDraft.Evidence}");
                        }
                        Console.WriteLine($"  {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeAnswerUnavailable)
                    {
                        Console.WriteLine("The resume does not provide a supported answer to this question; enter one manually or skip.");
                    }
                    else if (suggestion.Kind is AnswerSuggestionKind.AmbiguousQuestionAnswers or AnswerSuggestionKind.AmbiguousPreviousAnswers)
                    {
                        Console.WriteLine("Several similar saved questions have different answers:");
                        foreach (var answer in suggestion.ConflictingAnswers ?? [])
                        {
                            Console.WriteLine($"  Q: {answer.Question}");
                            Console.WriteLine($"  A: {answer.Answer}");
                        }
                        Console.WriteLine("No prior answer will be reused; enter this answer manually.");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.AmbiguousResumeExcerpts)
                    {
                        Console.WriteLine("Resume excerpts were too similar to choose safely; enter an answer manually.");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.SensitiveQuestion)
                    {
                        Console.WriteLine(
                            "Sensitive or high-impact question: prior answers and resume excerpts are not suggested or reused.");
                    }
                    else
                    {
                        Console.WriteLine("No matching confirmed answer or resume excerpt was found.");
                    }

                    if (field.Type == "radio" || field.Type == "select")
                    {
                        Console.WriteLine("Available choices:");
                        foreach (var option in await GetAvailableOptionsAsync(field))
                        {
                            Console.WriteLine($"  - {option}");
                        }
                    }

                    var forceManualAnswer = suggestion.Answer is null;
                    var preserveBrowserAnswer = browserAnswer is not null;
                    while (true)
                    {
                        string answer;
                        if (preserveBrowserAnswer)
                        {
                            Console.WriteLine("Choose: [Y] keep the browser answer, [S] skip. The existing answer will not be overwritten.");
                            var choice = Console.ReadLine()?.Trim();
                            if (choice is null || choice.Equals("S", StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                            if (!choice.Equals("Y", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("Enter Y or S.");
                                continue;
                            }

                            answer = browserAnswer!;
                        }
                        else if (!forceManualAnswer)
                        {
                            Console.WriteLine("Choose: [Y] use suggestion, [E] edit/enter another answer, [S] skip.");
                            var choice = Console.ReadLine()?.Trim();
                            if (choice is null || choice.Equals("S", StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                            if (choice.Equals("E", StringComparison.OrdinalIgnoreCase))
                            {
                                forceManualAnswer = true;
                                continue;
                            }
                            if (!choice.Equals("Y", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("Enter Y, E, or S.");
                                continue;
                            }

                            answer = suggestion.Answer!;
                        }
                        else
                        {
                            Console.Write("Enter your answer (or leave blank to skip): ");
                            var entered = Console.ReadLine();
                            if (string.IsNullOrWhiteSpace(entered))
                            {
                                return false;
                            }
                            answer = entered.Trim();
                        }

                        Console.WriteLine($"Answer to apply: {answer}");
                        Console.WriteLine(preserveBrowserAnswer
                            ? "Confirm keeping this existing answer? [Y] confirm, [S] skip."
                            : "Confirm and save this exact question/answer pair locally? [Y] confirm, [E] edit, [S] skip.");
                        var confirmation = Console.ReadLine()?.Trim();
                        if (confirmation is null || confirmation.Equals("S", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                        if (!preserveBrowserAnswer &&
                            confirmation.Equals("E", StringComparison.OrdinalIgnoreCase))
                        {
                            forceManualAnswer = true;
                            continue;
                        }
                        if (!confirmation.Equals("Y", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine(preserveBrowserAnswer
                                ? "Enter Y or S."
                                : "Enter Y, E, or S.");
                            continue;
                        }

                        if (preserveBrowserAnswer)
                        {
                            if (!await IsPendingFieldVisibleAsync(field))
                            {
                                return false;
                            }
                            if (!ManualAnswerWindow.IsUnchanged(
                                    browserAnswer,
                                    await GetCurrentAnswerAsync(field)))
                            {
                                Console.WriteLine("The browser answer changed while reviewing it. Please review again.");
                                browserAnswer = await GetCurrentAnswerAsync(field);
                                if (browserAnswer is null)
                                {
                                    preserveBrowserAnswer = false;
                                    forceManualAnswer = true;
                                }
                                continue;
                            }
                        }
                        else if (!await ApplyAnswerAsync(field, answer))
                        {
                            Console.WriteLine("That answer did not exactly match a supported option. Try the displayed option text or skip.");
                            forceManualAnswer = true;
                            continue;
                        }

                        await questionAnswers.AddAndSaveAsync(questionAnswersPath, field.Question, answer);
                        Console.WriteLine($"Saved exact question/answer key-value pair to {questionAnswersPath}.");
                        if (!sensitive)
                        {
                            await confirmedAnswers.AddAndSaveAsync(confirmedAnswersPath, field.Question, answer);
                        }

                        return true;
                    }
                }

            static async Task<string?> GetCurrentAnswerAsync(PendingAnswerField field)
                {
                    if (!await IsPendingFieldVisibleAsync(field))
                    {
                        return null;
                    }

                    if (field.Type == "radio")
                    {
                        foreach (var radioControl in field.Controls)
                        {
                            if (await radioControl.IsCheckedAsync())
                            {
                                var label = await ReadControlLabelAsync(radioControl);
                                return string.IsNullOrWhiteSpace(label)
                                    ? await radioControl.GetAttributeAsync("value")
                                    : label;
                            }
                        }

                        return null;
                    }

                    var control = field.Controls[0];
                    if (field.Type == "checkbox")
                    {
                        var isChecked = await control.IsCheckedAsync();
                        return isChecked ? "Yes" : null;
                    }
                    if (field.Type == "select")
                    {
                        var selected = control.Locator("option:checked").First;
                        if (await selected.CountAsync() == 0)
                        {
                            return null;
                        }
                        var selectedValue = await selected.GetAttributeAsync("value");
                        if (string.IsNullOrWhiteSpace(selectedValue))
                        {
                            return null;
                        }
                        var selectedLabel = (await selected.InnerTextAsync()).Trim();
                        return string.IsNullOrWhiteSpace(selectedLabel) ? selectedValue : selectedLabel;
                    }

                    var value = await control.InputValueAsync();
                    return ManualAnswerWindow.HasAnswer(field.Type, value) ? value : null;
                }

            static async Task<bool> IsPendingFieldVisibleAsync(PendingAnswerField field)
                {
                    foreach (var control in field.Controls)
                    {
                        var visible = await control.IsVisibleAsync();
                        if (!visible)
                        {
                            var label = control.Locator("xpath=ancestor::label[1]");
                            visible = await label.CountAsync() > 0 && await label.IsVisibleAsync();
                        }

                        if (visible &&
                            string.Equals(
                                await GetQuestionTextAsync(control),
                                field.Question,
                                StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                    return false;
                }

            static async Task<IReadOnlyList<string>> GetAvailableOptionsAsync(PendingAnswerField field)
                {
                    var options = new List<string>();
                    if (field.Type == "radio")
                    {
                        foreach (var control in field.Controls)
                        {
                            var label = await ReadControlLabelAsync(control);
                            options.Add(string.IsNullOrWhiteSpace(label)
                                ? await control.GetAttributeAsync("value") ?? "(unlabeled option)"
                                : label);
                        }
                    }
                    else if (field.Type == "select")
                    {
                        var selectOptions = field.Controls[0].Locator("option");
                        for (var index = 0; index < await selectOptions.CountAsync(); index++)
                        {
                            var option = selectOptions.Nth(index);
                            var label = (await option.InnerTextAsync()).Trim();
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                options.Add(label);
                            }
                        }
                    }

                    return options;
                }

            static bool IsAnswerMatch(string answer, string option) =>
                    answer.Length > 0 &&
                    (answer == option || option.StartsWith(answer + " ", StringComparison.Ordinal) ||
                     option.EndsWith(" " + answer, StringComparison.Ordinal));

            static async Task<bool> ApplyAnswerAsync(PendingAnswerField field, string answer)
                {
                    if (field.Type == "radio")
                    {
                        var normalizedAnswer = AnswerMatcher.Normalize(answer);
                        var matches = new List<ILocator>();
                        foreach (var control in field.Controls)
                        {
                            var label = await ReadControlLabelAsync(control);
                            var value = await control.GetAttributeAsync("value") ?? "";
                            if (IsAnswerMatch(normalizedAnswer, AnswerMatcher.Normalize(label)) ||
                                IsAnswerMatch(normalizedAnswer, AnswerMatcher.Normalize(value)))
                            {
                                matches.Add(control);
                            }
                        }

                        if (matches.Count != 1)
                        {
                            return false;
                        }

                        var radio = matches[0];
                        if (await radio.IsVisibleAsync())
                        {
                            await radio.CheckAsync();
                        }
                        else
                        {
                            var label = radio.Locator("xpath=ancestor::label[1]");
                            if (await label.CountAsync() > 0 && await label.IsVisibleAsync())
                            {
                                await label.ClickAsync();
                            }
                            else
                            {
                                return false;
                            }
                        }

                        return await radio.IsCheckedAsync();
                    }

                    if (field.Type == "select")
                    {
                        var select = field.Controls[0];
                        var normalizedAnswer = AnswerMatcher.Normalize(answer);
                        var options = select.Locator("option");
                        var matches = new List<(string? Value, string Label)>();
                        for (var index = 0; index < await options.CountAsync(); index++)
                        {
                            var option = options.Nth(index);
                            var label = (await option.InnerTextAsync()).Trim();
                            var value = await option.GetAttributeAsync("value");
                            if (IsAnswerMatch(normalizedAnswer, AnswerMatcher.Normalize(label)) ||
                                IsAnswerMatch(normalizedAnswer, AnswerMatcher.Normalize(value ?? "")))
                            {
                                matches.Add((value, label));
                            }
                        }

                        if (matches.Count != 1)
                        {
                            return false;
                        }
                        if (matches[0].Value is not null)
                        {
                            await select.SelectOptionAsync(new SelectOptionValue { Value = matches[0].Value });
                        }
                        else
                        {
                            await select.SelectOptionAsync(new SelectOptionValue { Label = matches[0].Label });
                        }
                        return true;
                    }

                    if (field.Type == "checkbox")
                    {
                        var normalizedAnswer = AnswerMatcher.Normalize(answer);
                        var shouldCheck = normalizedAnswer is "yes" or "true" or "agree";
                        if (!shouldCheck && normalizedAnswer is not ("no" or "false"))
                        {
                            return false;
                        }

                        var checkbox = field.Controls[0];
                        if (shouldCheck != await checkbox.IsCheckedAsync())
                        {
                            if (await checkbox.IsVisibleAsync())
                            {
                                if (shouldCheck)
                                {
                                    await checkbox.CheckAsync();
                                }
                                else
                                {
                                    await checkbox.UncheckAsync();
                                }
                            }
                            else
                            {
                                var label = checkbox.Locator("xpath=ancestor::label[1]");
                                if (await label.CountAsync() == 0 || !await label.IsVisibleAsync())
                                {
                                    return false;
                                }
                                await label.ClickAsync();
                            }
                        }
                        return await checkbox.IsCheckedAsync() == shouldCheck;
                    }

                    if (field.Controls.Count != 1)
                    {
                        return false;
                    }

                    var textField = field.Controls[0];
                    if (string.Equals(await textField.GetAttributeAsync("type"), "number", StringComparison.OrdinalIgnoreCase))
                    {
                        var numericAnswer = Regex.Replace(answer, @"[^\d.\-]", "");
                        if (numericAnswer.Length == 0)
                        {
                            return false;
                        }
                        await textField.FillAsync(numericAnswer);
                    }
                    else
                    {
                        await textField.FillAsync(answer);
                    }
                    return true;
                }

            if (await submitButton.CountAsync() > 0 && await submitButton.IsVisibleAsync())
            {
                return await WaitForManualSubmissionAsync(modal, page, cancellationToken);
            }

            var reviewButton = modal.GetByRole(AriaRole.Button, new() { Name = "Review", Exact = true });
            if (await reviewButton.CountAsync() > 0 && await reviewButton.IsVisibleAsync())
            {
                await reviewButton.ClickAsync();
                reviewReached = true;
                await page.WaitForTimeoutAsync(350);
                continue;
            }

            var nextButton = modal.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true });
            if (await nextButton.CountAsync() > 0 && await nextButton.IsVisibleAsync())
            {
                await nextButton.ClickAsync();
                await page.WaitForTimeoutAsync(350);
                continue;
            }

            await CloseModalAsync(modal, page);
            return ApplyResult.UnsupportedDialog;
        }

        await CloseModalAsync(modal, page);
        return ApplyResult.UnsupportedDialog;
    }

    private static async Task<bool> SelectNewestResumeAsync(ILocator modal)
    {
        var radios = modal.Locator("input[type='radio']");
        var count = await radios.CountAsync();
        if (count == 0)
        {
            return true;
        }

        var chosenIndex = 0;
        DateTime newestDate = DateTime.MinValue;
        var allResumesHaveDates = true;
        for (var i = 0; i < count; i++)
        {
            var radio = radios.Nth(i);
            var text = await ReadControlLabelAsync(radio);

            if (!TryGetResumeDate(text, out var date))
            {
                allResumesHaveDates = false;
            }
            else if (date > newestDate)
            {
                newestDate = date;
                chosenIndex = i;
            }
        }

        if (!allResumesHaveDates)
        {
            chosenIndex = 0;
        }

        var chosen = radios.Nth(chosenIndex);
        if (await chosen.IsVisibleAsync())
        {
            await chosen.CheckAsync();
            return true;
        }

        var label = chosen.Locator("xpath=ancestor::label[1]");
        if (await label.CountAsync() > 0 && await label.IsVisibleAsync())
        {
            await label.ClickAsync();
            return true;
        }

        var parent = chosen.Locator("xpath=..");
        if (await parent.CountAsync() > 0 && await parent.IsVisibleAsync())
        {
            await parent.ClickAsync();
            return true;
        }

        return false;
    }

    private static async Task<ApplyResult> WaitForManualSubmissionAsync(
        ILocator modal,
        IPage page,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Review the application in the browser. The app will not submit it.");
        Console.WriteLine("Submit manually if you choose to proceed, then press Enter here so the app can verify success.");
        var manualInput = await Console.In.ReadLineAsync(cancellationToken);
        if (manualInput is null)
        {
            return ApplyResult.ManualReviewPending;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (page.IsClosed)
            {
                return ApplyResult.ManualReviewPending;
            }

            var bodyText = await page.Locator("body").InnerTextAsync();
            var modalConfirms = await modal.IsVisibleAsync() &&
                ContainsConfirmation(await modal.InnerTextAsync());
            if (modalConfirms || ContainsConfirmation(bodyText))
            {
                await CloseCompletedModalAsync(modal, page);
                return ApplyResult.Submitted;
            }

            await page.WaitForTimeoutAsync(500);
        }

        Console.WriteLine("No submission confirmation was detected. Leaving the application open for manual review.");
        return ApplyResult.ManualReviewPending;
    }

    private static async Task CloseModalAsync(ILocator modal, IPage page)
    {
        var dismiss = modal.GetByRole(AriaRole.Button, new() { Name = "Dismiss" });
        if (await dismiss.CountAsync() > 0 && await dismiss.First.IsVisibleAsync())
        {
            await dismiss.First.ClickAsync();
        }
        else
        {
            var close = modal.GetByRole(AriaRole.Button, new() { Name = "Close" });
            if (await close.CountAsync() > 0 && await close.First.IsVisibleAsync())
            {
                await close.First.ClickAsync();
            }
            else
            {
                await page.Keyboard.PressAsync("Escape");
            }
        }

        await page.WaitForTimeoutAsync(250);
        var remainingDialog = page.GetByRole(AriaRole.Dialog).Last;
        if (await remainingDialog.CountAsync() > 0 && await remainingDialog.IsVisibleAsync())
        {
            var discard = remainingDialog.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true });
            if (await discard.CountAsync() > 0 && await discard.IsVisibleAsync())
            {
                await discard.ClickAsync();
            }
        }
    }

    private static async Task CloseCompletedModalAsync(ILocator modal, IPage page)
    {
        var done = modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true });
        if (await done.CountAsync() > 0 && await done.IsVisibleAsync())
        {
            await done.ClickAsync();
        }
        else
        {
            await CloseModalAsync(modal, page);
        }
    }

    private static bool TryGetResumeDate(string text, out DateTime date)
    {
        var dateMatch = DatePattern.Match(text);
        if (dateMatch.Success && DateTime.TryParse(dateMatch.Value, out date))
        {
            return true;
        }

        var relativeMatch = Regex.Match(
            text,
            @"\b(?:(today)|(yesterday)|(\d+)\s+(day|week|month|year)s?\s+ago)\b",
            RegexOptions.IgnoreCase);
        if (relativeMatch.Success)
        {
            date = DateTime.Now;
            if (relativeMatch.Groups[2].Success)
            {
                date = date.AddDays(-1);
                return true;
            }

            if (!relativeMatch.Groups[1].Success)
            {
                var amount = int.Parse(relativeMatch.Groups[3].Value);
                date = relativeMatch.Groups[4].Value.ToLowerInvariant() switch
                {
                    "day" => date.AddDays(-amount),
                    "week" => date.AddDays(-7 * amount),
                    "month" => date.AddMonths(-amount),
                    "year" => date.AddYears(-amount),
                    _ => date
                };
            }

            return true;
        }

        date = DateTime.MinValue;
        return false;
    }

    private static async Task<string> ReadControlLabelAsync(ILocator control)
    {
        var associatedLabels = await control.EvaluateAsync<string>(
            "element => Array.from(element.labels || []).map(label => label.innerText.trim()).filter(Boolean).join(' ')");
        if (!string.IsNullOrWhiteSpace(associatedLabels))
        {
            return associatedLabels;
        }

        var label = control.Locator("xpath=ancestor::label[1]");
        if (await label.CountAsync() > 0)
        {
            return await label.InnerTextAsync();
        }

        var parent = control.Locator("xpath=..");
        return await parent.CountAsync() > 0 ? await parent.InnerTextAsync() : "";
    }

    private static bool ContainsAppliedMarker(string text) =>
        Regex.IsMatch(text, @"\b(applied|application submitted)\b", RegexOptions.IgnoreCase);

    private static bool ContainsConfirmation(string text) =>
        Regex.IsMatch(text, @"application (was )?(submitted|sent)|your application has been sent|application submitted", RegexOptions.IgnoreCase);

    private static string FirstNonEmptyLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Untitled listing";

    private static async Task<HashSet<string>> LoadListingIdsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(path);
        var values = await JsonSerializer.DeserializeAsync<List<string>>(stream);
        return new HashSet<string>(values ?? [], StringComparer.Ordinal);
    }

    private static async Task<HashSet<string>> LoadLegacyAppliedJobIdsAsync(string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return ids;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ids;
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Name.All(char.IsDigit) &&
                    entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.TryGetProperty("submitted_at", out var submittedAt) &&
                    submittedAt.ValueKind == JsonValueKind.String)
                {
                    ids.Add(entry.Name);
                }
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Could not import the previous application history from {path}: {ex.Message}");
        }

        return ids;
    }

    private static bool ShouldSkipApplicationUrl(string url) => SkippedApplicationUrl.IsMatch(url);

    private static async Task SaveListingIdsAsync(string path, HashSet<string> appliedJobs)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, appliedJobs.OrderBy(id => id).ToList(), JsonOptions);
    }

    private enum ApplyResult
    {
        Submitted,
        UnsupportedQuestion,
        UnsupportedDialog,
        UnconfirmedSubmission,
        ManualReviewPending,
        WaitInterrupted
    }

    private sealed record PendingAnswerField(
        string Question,
        string Type,
        IReadOnlyList<ILocator> Controls);

    private sealed record ExternalApplyLink(Uri Destination);
}
