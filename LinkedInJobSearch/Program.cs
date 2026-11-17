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
    private static readonly Regex JobIdFromUrl = new(@"/jobs/view/(\d+)", RegexOptions.Compiled);
    private static readonly Regex DatePattern = new(
        @"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b",
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
            "LinkedInJobSearch");
        var profileDirectory = Path.Combine(dataDirectory, "PlaywrightProfile");
        var appliedJobsPath = Path.Combine(dataDirectory, "applied-jobs.json");
        var uncertainJobsPath = Path.Combine(dataDirectory, "uncertain-jobs.json");
        var confirmedAnswersPath = Path.Combine(dataDirectory, "confirmed-answers.json");
        var projectRoot = ResumePaths.FindProjectRoot(
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        var resumeTextPath = ResumePaths.GetResumePath(projectRoot);
        var resumeAnswersPath = Path.Combine(dataDirectory, "resume-answers.json");
        Directory.CreateDirectory(profileDirectory);

        var appliedJobs = await LoadListingIdsAsync(appliedJobsPath);
        var uncertainJobs = await LoadListingIdsAsync(uncertainJobsPath);
        var confirmedAnswers = await ConfirmedAnswerStore.LoadAsync(confirmedAnswersPath);
        var resumeAnswers = await ResumeAnswerStore.LoadAsync(resumeAnswersPath);
        var resumeText = File.Exists(resumeTextPath)
            ? await File.ReadAllTextAsync(resumeTextPath)
            : "";
        if (string.IsNullOrWhiteSpace(resumeText))
        {
            Console.WriteLine($"Optional project-root resume for excerpt suggestions: {resumeTextPath}");
        }
        Console.WriteLine($"Optional local resume-based answers: {resumeAnswersPath}");

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

            Console.WriteLine("A persistent Chromium window is open. Sign in to LinkedIn there if needed.");
            Console.WriteLine("When the job search results are visible, press Enter here to begin.");
            Console.ReadLine();

            await ProcessListingsAsync(
                page,
                appliedJobs,
                appliedJobsPath,
                uncertainJobs,
                uncertainJobsPath,
                confirmedAnswers,
                confirmedAnswersPath,
                resumeAnswers,
                resumeText,
                cancellation.Token);
            Console.WriteLine("Finished scanning the listings loaded by this search.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Run cancelled. No further form or listing actions will be taken.");
            return 0;
        }
        catch (PlaywrightException ex)
        {
            Console.Error.WriteLine($"Playwright could not complete the run: {ex.Message}");
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
        ResumeAnswerStore resumeAnswers,
        string resumeText,
        CancellationToken cancellationToken)
    {
        var seenThisRun = new HashSet<string>(StringComparer.Ordinal);
        var stagnantRounds = 0;

        while (stagnantRounds < 3)
        {
            var jobIds = await ReadVisibleJobIdsAsync(page);
            var newJobs = jobIds.Where(id => seenThisRun.Add(id)).ToList();

            foreach (var jobId in newJobs)
            {
                var card = page.Locator($"[data-job-id='{jobId}'], [data-occludable-job-id='{jobId}']").First;
                if (!await card.IsVisibleAsync())
                {
                    continue;
                }

                var cardText = await card.InnerTextAsync();
                var title = FirstNonEmptyLine(cardText);
                if (appliedJobs.Contains(jobId) || ContainsAppliedMarker(cardText))
                {
                    appliedJobs.Add(jobId);
                    await SaveListingIdsAsync(appliedJobsPath, appliedJobs);
                    Console.WriteLine($"Skipping already-applied listing: {title} ({jobId})");
                    continue;
                }
                if (uncertainJobs.Contains(jobId))
                {
                    Console.WriteLine($"Skipping listing with an unconfirmed prior submission: {title} ({jobId})");
                    continue;
                }

                Console.WriteLine($"Checking: {title} ({jobId})");
                await card.ClickAsync();
                await page.WaitForTimeoutAsync(700);

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
                    if (!await OpenExternalApplyAndPauseAsync(
                            page,
                            externalApplyLinks[0].Destination,
                            title,
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
                    if (destination is null ||
                        !await OpenExternalApplyAndPauseAsync(page, destination, title, cancellationToken))
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
                    Console.WriteLine("  Apply navigated to an external site; stopping without interacting with it.");
                    return;
                }

                var result = await CompleteEasyApplyAsync(
                    page,
                    confirmedAnswers,
                    confirmedAnswersPath,
                    resumeAnswers,
                    resumeText,
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

            await ScrollResultsAsync(page);
            await page.WaitForTimeoutAsync(900);
            var hasUnseenListings = (await ReadVisibleJobIdsAsync(page)).Any(id => !seenThisRun.Contains(id));
            stagnantRounds = hasUnseenListings ? 0 : stagnantRounds + 1;
        }
    }

    private static async Task<List<string>> ReadVisibleJobIdsAsync(IPage page)
    {
        var cards = page.Locator("div.job-card-container, li[data-occludable-job-id], div[data-job-id]");
        var count = await cards.CountAsync();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < count; index++)
        {
            var card = cards.Nth(index);
            var id = await card.GetAttributeAsync("data-job-id")
                ?? await card.GetAttributeAsync("data-occludable-job-id");
            if (string.IsNullOrWhiteSpace(id))
            {
                var link = card.Locator("a[href*='/jobs/view/']").First;
                if (await link.CountAsync() > 0)
                {
                    var href = await link.GetAttributeAsync("href");
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        id = JobIdFromUrl.Match(href).Groups[1].Value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        return ids.ToList();
    }

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
        CancellationToken cancellationToken)
    {
        if (!ApplyButtonHandling.IsSafeExternalDestination(destination))
        {
            Console.Error.WriteLine($"  External Apply destination is not HTTPS; refusing to open it: {destination}");
            return false;
        }

        var externalPage = await sourcePage.Context.NewPageAsync();
        try
        {
            await externalPage.GotoAsync(
                destination.AbsoluteUri,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Console.WriteLine();
            Console.WriteLine($"External company application opened for {jobTitle}.");
            Console.WriteLine("Handle this application manually in the browser. The app will not fill fields, answer questions, or submit it.");
            Console.WriteLine("When finished, press Enter here to close this tab and continue to the next job (Ctrl+C stops the run).");
            var input = await Console.In.ReadLineAsync(cancellationToken);
            if (input is null)
            {
                Console.WriteLine("  Console input ended; stopping without further job actions.");
                return false;
            }

            return ApplyButtonHandling.ShouldContinueAfterManualHandoff(
                enterReceived: true,
                sourcePageIsClosed: sourcePage.IsClosed);
        }
        catch (PlaywrightException ex)
        {
            Console.Error.WriteLine($"  Could not open or wait on the external application page: {ex.Message}");
            return false;
        }
        finally
        {
            if (!externalPage.IsClosed)
            {
                await externalPage.CloseAsync();
            }
        }
    }

    private static async Task ScrollResultsAsync(IPage page)
    {
        await page.EvaluateAsync(
            """
            () => {
                const candidates = [
                    ...document.querySelectorAll(
                        '.jobs-search-results-list, .scaffold-layout__list, [data-testid="jobs-search-results-list"]'
                    )
                ];
                const scrollable = candidates.find(
                    element => element.scrollHeight > element.clientHeight + 20
                );
                if (scrollable) {
                    scrollable.scrollTop = scrollable.scrollHeight;
                } else {
                    window.scrollTo(0, document.body.scrollHeight);
                }
            }
            """);
    }

    private static async Task<ApplyResult> CompleteEasyApplyAsync(
        IPage page,
        ConfirmedAnswerStore confirmedAnswers,
        string confirmedAnswersPath,
        ResumeAnswerStore resumeAnswers,
        string resumeText,
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

            static async Task<bool> ReviewAndApplyAnswerAsync(
                    PendingAnswerField field,
                    string? browserAnswer,
                    ConfirmedAnswerStore confirmedAnswers,
                    string confirmedAnswersPath,
                    ResumeAnswerStore resumeAnswers,
                    string resumeText)
                {
                    var sensitive = AnswerMatcher.IsSensitive(field.Question);
                    var previousAnswer = AnswerMatcher.FindPreviousAnswer(field.Question, confirmedAnswers.Entries);
                    var suggestion = browserAnswer is not null
                        ? new AnswerSuggestion(AnswerSuggestionKind.None, null, 0)
                        : previousAnswer.Kind == AnswerSuggestionKind.None
                            ? resumeAnswers.FindAnswer(field.Question) is { Kind: not AnswerSuggestionKind.None } resumeAnswer
                                ? resumeAnswer
                                : AnswerMatcher.FindResumeExcerpt(field.Question, resumeText)
                            : previousAnswer;
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
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeExcerpt)
                    {
                        Console.WriteLine(
                            $"Resume excerpt suggestion ({suggestion.Similarity:P0} Jaccard similarity; " +
                            "quoted verbatim):");
                        Console.WriteLine($"  {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeAnswer)
                    {
                        Console.WriteLine("Resume-supported answer:");
                        Console.WriteLine($"  {suggestion.Answer}");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.ResumeAnswerUnavailable)
                    {
                        Console.WriteLine("The resume does not provide a supported answer to this question; enter one manually or skip.");
                    }
                    else if (suggestion.Kind == AnswerSuggestionKind.AmbiguousPreviousAnswers)
                    {
                        Console.WriteLine("Several similar prior questions have different confirmed answers:");
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
                            : sensitive
                                ? "Confirm and use this answer for this application only? [Y] confirm, [E] edit, [S] skip."
                                : "Confirm and save this answer for future matching? [Y] confirm, [E] edit, [S] skip.");
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

                        if (!sensitive)
                        {
                            await confirmedAnswers.AddAndSaveAsync(confirmedAnswersPath, field.Question, answer);
                            Console.WriteLine("Confirmed answer saved locally.");
                        }
                        else
                        {
                            Console.WriteLine("Sensitive answer used for this application only; it was not saved.");
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
                        return isChecked ? "Yes" : "No";
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
                            if (normalizedAnswer == AnswerMatcher.Normalize(label) ||
                                normalizedAnswer == AnswerMatcher.Normalize(value))
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
                            if (normalizedAnswer == AnswerMatcher.Normalize(label) ||
                                normalizedAnswer == AnswerMatcher.Normalize(value ?? ""))
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
                        var shouldCheck = normalizedAnswer is "yes" or "true";
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

                    await field.Controls[0].FillAsync(answer);
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
