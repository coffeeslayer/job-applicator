using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private const string SearchUrl =
        "https://my.greenhouse.io/jobs/search?date_posted=past_five_days&salary=more_than_160k&work_type%5B%5D=remote&employment_type%5B%5D=full_time&employment_type%5B%5D=part_time&employment_type%5B%5D=contract&employment_type%5B%5D=temporary";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex JobPath = new(@"/jobs/(?!search(?:[/?#]|$))[^/?#]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SkippedApplicationHost = new(@"talenthop|bestjobtool", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            "GreenhouseJobSearch");
        var profileDirectory = Path.Combine(dataDirectory, "PlaywrightProfile");
        var appliedJobsPath = Path.Combine(dataDirectory, "applied-jobs.json");
        var personalProfilePath = PersonalProfile.FindPath(Environment.CurrentDirectory, AppContext.BaseDirectory);
        var projectRoot = Path.GetDirectoryName(personalProfilePath)!;
        var teeWriter = new ConsoleTeeWriter(Console.Out, Path.Combine(projectRoot, "console.log"));
        Console.SetOut(teeWriter);
        var personalProfile = await PersonalProfile.LoadAsync(personalProfilePath);
        var resumePath = Path.Combine(projectRoot, "resume.md");
        var resumeText = File.Exists(resumePath) ? await File.ReadAllTextAsync(resumePath) : "";
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
        Console.WriteLine("The browser profile is local to your Windows account. Sign in there if Greenhouse asks.");

        using var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;
        var keepJobPagesForTroubleshooting = false;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions { Headless = false });
            var searchPage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            await searchPage.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            var waitingForVerificationCode = await PrepareGreenhouseSignInAsync(searchPage, personalProfile);

            Console.WriteLine();
            Console.WriteLine("Greenhouse search opened with the requested filters:");
            Console.WriteLine("  Posted in the past five days; salary above $160,000; remote;");
            Console.WriteLine("  full-time, part-time, contract, or temporary.");
            Console.WriteLine(waitingForVerificationCode
                ? "Enter the security code in the browser and finish signing in. Then press Enter here to load the filtered search."
                : "Press Enter to load the filtered search.");
            if (Console.ReadLine() is null)
            {
                return 0;
            }

            await searchPage.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Console.WriteLine("Loaded the Greenhouse search URL with your requested filters.");

            var seenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!cancellation.IsCancellationRequested && !searchPage.IsClosed)
            {
                var listings = await ReadListingsAsync(searchPage);
                if (listings.Count == 0)
                {
                    Console.WriteLine("No job links recognized yet; waiting up to 15 seconds for Greenhouse results to finish loading...");
                    listings = await WaitForListingsAsync(searchPage, TimeSpan.FromSeconds(15), cancellation.Token);
                    if (listings.Count == 0)
                    {
                        await ReportListingDiagnosticsAsync(searchPage);
                    }
                }

                var newListings = listings.Where(listing => seenThisRun.Add(listing.Url)).ToList();
                Console.WriteLine($"Found {listings.Count} job link(s); {newListings.Count} not yet scanned this run.");

                foreach (var listing in newListings)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (ShouldSkipApplicationUrl(listing.Url))
                    {
                        Console.WriteLine($"Skipping excluded application URL: {listing.Url}");
                        continue;
                    }
                    if (appliedJobs.Contains(listing.Url))
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
                        await jobPage.GotoAsync(listing.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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
                            await applyControls[0].ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                            await jobPage.WaitForTimeoutAsync(800);
                            applicationPopup = context.Pages.FirstOrDefault(page => !pagesBeforeApply.Contains(page));
                            if (applicationPopup is not null)
                            {
                                Console.WriteLine("The application opened in a separate browser tab.");
                            }
                            var applicationPage = applicationPopup ?? jobPage;
                            if (ShouldSkipApplicationUrl(applicationPage.Url))
                            {
                                Console.WriteLine($"Skipping excluded application URL: {applicationPage.Url}");
                                continue;
                            }
                            await SelectLinkedInReferralAsync(applicationPage);
                            await CheckGdprConsentIfPresentAsync(applicationPage);
                            var fillResult = await FillRequestedApplicationFieldsAsync(
                                applicationPage,
                                personalProfile,
                                string.IsNullOrWhiteSpace(resumeText) ? null : localModel,
                                resumeText,
                                listing.Title,
                                cancellation.Token);
                            if (fillResult.FilledCount > 0)
                            {
                                Console.WriteLine($"Filled {fillResult.FilledCount} recognized application field(s).");
                            }
                            var submitted = fillResult.AllQuestionsAnswered &&
                                await OfferApplicationSubmissionAsync(applicationPage, listing.Title);
                            if (!fillResult.AllQuestionsAnswered)
                            {
                                Console.WriteLine();
                                Console.WriteLine("===============================================================");
                                Console.WriteLine($"[ACTION REQUIRED] UNANSWERED QUESTIONS FOR: {listing.Title}");
                                foreach (var q in fillResult.UnansweredQuestions)
                                {
                                    Console.WriteLine($"  * {q}");
                                }
                                Console.WriteLine("Tell the assistant in chat: 'check console' to answer these in personal.md!");
                                Console.WriteLine("===============================================================");
                                Console.WriteLine();

                                var pendingPath = Path.Combine(projectRoot, "pending-questions.json");
                                await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(new
                                {
                                    jobTitle = listing.Title,
                                    jobUrl = listing.Url,
                                    timestamp = DateTimeOffset.UtcNow,
                                    unansweredQuestions = fillResult.UnansweredQuestions
                                }, JsonOptions));
                            }
                            if (submitted && await HasSubmissionConfirmationAsync(applicationPage))
                            {
                                appliedJobs.Add(listing.Url);
                                await SaveAppliedJobsAsync(appliedJobsPath, appliedJobs);
                                Console.WriteLine("Greenhouse confirmed the application. Recorded as applied.");
                                var pendingPath = Path.Combine(projectRoot, "pending-questions.json");
                                if (File.Exists(pendingPath))
                                {
                                    File.Delete(pendingPath);
                                }
                                await DismissApplicationSuccessAndRestoreSearchAsync(applicationPage, searchPage);
                                continue;
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
                            appliedJobs.Add(listing.Url);
                            await SaveAppliedJobsAsync(appliedJobsPath, appliedJobs);
                            Console.WriteLine("Recorded as applied.");
                            var completedPage = applicationPopup ?? jobPage;
                            if (await HasSubmissionConfirmationAsync(completedPage))
                            {
                                await DismissApplicationSuccessAndRestoreSearchAsync(completedPage, searchPage);
                            }
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
                Console.WriteLine("Scan finished. Scroll or adjust the search in the Greenhouse tab, then press Enter to scan new results.");
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
            Console.Error.WriteLine("Greenhouse runner failed with an exception:");
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

    private static async Task<bool> PrepareGreenhouseSignInAsync(IPage page, PersonalProfile? personalProfile)
    {
        var cookieButtons = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^ok$", RegexOptions.IgnoreCase) });
        if (await cookieButtons.CountAsync() == 1 && await cookieButtons.First.IsVisibleAsync())
        {
            await cookieButtons.First.ClickAsync();
        }

        if (!page.Url.Contains("/users/sign_in", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var emailFields = page.Locator("input[type='email'], input[autocomplete='email'], input[placeholder='Email']");
        var visibleEmailFields = new List<ILocator>();
        for (var index = 0; index < await emailFields.CountAsync(); index++)
        {
            var field = emailFields.Nth(index);
            if (await field.IsVisibleAsync())
            {
                visibleEmailFields.Add(field);
            }
        }

        if (visibleEmailFields.Count != 1)
        {
            Console.WriteLine("Greenhouse sign-in is open. Enter your email and request a security code in the browser.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(personalProfile?.Email))
        {
            Console.WriteLine("Personal settings do not contain an email. Enter it and request the Greenhouse security code in the browser.");
            return true;
        }

        await visibleEmailFields[0].FillAsync(personalProfile.Email);
        var sendCodeButtons = page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex("^send security code$", RegexOptions.IgnoreCase) });
        if (await sendCodeButtons.CountAsync() == 1 && await sendCodeButtons.First.IsVisibleAsync())
        {
            await sendCodeButtons.First.ClickAsync();
            Console.WriteLine("Requested a Greenhouse sign-in security code. Enter it in the browser to continue.");
            return true;
        }

        Console.WriteLine("Your email is filled in. Request the Greenhouse security code in the browser.");
        return true;
    }

    private static async Task<ApplicationFieldFillResult> FillRequestedApplicationFieldsAsync(
        IPage page,
        PersonalProfile? personalProfile,
        LocalModelAnswerClient? localModel,
        string resumeText,
        string jobTitle,
        CancellationToken cancellationToken)
    {
        var filledCount = 0;
        filledCount += await FillResidenceQuestionAsync(
            page,
            personalProfile?.Residence);
        var fullLegalName = string.Join(' ', new[] { personalProfile?.LegalFirstName, personalProfile?.LegalLastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        filledCount += await FillFullLegalNameQuestionsAsync(page, fullLegalName);

        var authorizationAnswer = personalProfile?.AnswerForQuestion(
            "Are you authorized to reside and work in the country where this role is based?");
        if (authorizationAnswer is "Yes" or "No")
        {
            filledCount += await FillLabeledBooleanComboboxAsync(
                page,
                IsWorkAuthorizationQuestion,
                authorizationAnswer,
                "work authorization");
        }

        var sponsorshipAnswer = personalProfile?.AnswerForQuestion(
            "Will you now or in the future require visa sponsorship for employment in the United States?");
        if (sponsorshipAnswer is "Yes" or "No")
        {
            filledCount += await FillLabeledBooleanComboboxAsync(
                page,
                IsVisaSponsorshipQuestion,
                sponsorshipAnswer,
                "visa sponsorship");
        }

        var veteranAnswer = personalProfile?.VeteranStatus;
        if (veteranAnswer is "Yes" or "No")
        {
            filledCount += await FillLabeledBooleanComboboxAsync(
                page,
                label => label.Contains("veteran", StringComparison.OrdinalIgnoreCase),
                veteranAnswer,
                "veteran status");
        }

        filledCount += await FillUniqueVisibleFieldAsync(page, "Legal First Name", personalProfile?.LegalFirstName);
        filledCount += await FillUniqueVisibleFieldAsync(page, "Legal Last Name", personalProfile?.LegalLastName);

        try
        {
            var result = await ExternalApplicationFormAssistant.DraftBlankFieldsAsync(
                page,
                localModel,
                jobTitle,
                resumeText,
                personalProfile?.Residence,
                cancellationToken,
                personalProfile);
            filledCount += result.FilledCount;
            return new ApplicationFieldFillResult(filledCount, result.UnansweredQuestions.Count == 0, result.UnansweredQuestions);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Could not contact the local model; leaving the application open for review. ({ex.Message})");
            return new ApplicationFieldFillResult(filledCount, false, []);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"The local model returned an unusable answer; leaving the application open for review. ({ex.Message})");
            return new ApplicationFieldFillResult(filledCount, false, []);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"The local model request timed out; leaving the application open for review. ({ex.Message})");
            return new ApplicationFieldFillResult(filledCount, false, []);
        }
    }

    private sealed record ApplicationFieldFillResult(int FilledCount, bool AllQuestionsAnswered, IReadOnlyList<string> UnansweredQuestions);

    private static async Task<int> FillResidenceQuestionAsync(IPage page, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return 0;
        }

        var controls = page.Locator("input:not([type='hidden']):not([type='checkbox']):not([type='radio']), textarea");
        var matches = new List<ILocator>();
        for (var index = 0; index < await controls.CountAsync(); index++)
        {
            var control = controls.Nth(index);
            if (!await control.IsVisibleAsync() || await control.IsDisabledAsync() || await control.GetAttributeAsync("readonly") is not null)
            {
                continue;
            }

            if (ResidenceQuestion.IsMatch(await GetAccessibleQuestionLabelAsync(control)))
            {
                matches.Add(control);
            }
        }

        if (matches.Count != 1)
        {
            return 0;
        }

        await matches[0].ScrollIntoViewIfNeededAsync();
        await matches[0].FillAsync(answer);
        Console.WriteLine("Filled the residence question from personal.md.");
        return 1;
    }

    private static bool ShouldSkipApplicationUrl(string url) => SkippedApplicationHost.IsMatch(url);

    private static async Task<int> FillFullLegalNameQuestionsAsync(IPage page, string fullLegalName)
    {
        if (string.IsNullOrWhiteSpace(fullLegalName))
        {
            return 0;
        }

        var fields = page.Locator("input[type='text'], textarea");
        var filledCount = 0;
        for (var index = 0; index < await fields.CountAsync(); index++)
        {
            var field = fields.Nth(index);
            if (!await field.IsVisibleAsync() || !await field.IsEditableAsync() ||
                string.Equals(await field.GetAttributeAsync("role"), "combobox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var labels = new List<string>();
            var ariaLabel = await field.GetAttributeAsync("aria-label");
            if (!string.IsNullOrWhiteSpace(ariaLabel))
            {
                labels.Add(ariaLabel);
            }

            var id = await field.GetAttributeAsync("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                var associatedLabels = page.Locator($"label[for={JsonSerializer.Serialize(id)}]");
                for (var labelIndex = 0; labelIndex < await associatedLabels.CountAsync(); labelIndex++)
                {
                    labels.Add(await associatedLabels.Nth(labelIndex).InnerTextAsync());
                }
            }

            if (labels.Any(ContainsFullLegalNameTerms))
            {
                await field.ScrollIntoViewIfNeededAsync();
                await field.FillAsync(fullLegalName);
                filledCount++;
            }
        }

        return filledCount;
    }

    private static bool ContainsFullLegalNameTerms(string label)
    {
        var words = Regex.Matches(label, @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.Contains("full") && words.Contains("legal") && words.Contains("name");
    }

    private static bool IsVisaSponsorshipQuestion(string label)
    {
        var normalized = Regex.Replace(label, @"[^\p{L}\p{N}]+", " ").Trim();
        return normalized.Contains("sponsor", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("visa", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("work", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("employment", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWorkAuthorizationQuestion(string label)
    {
        var normalized = Regex.Replace(label, @"[^\p{L}\p{N}]+", " ").Trim();
        return normalized.Contains("authoriz", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("work", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("reside", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> FillLabeledBooleanComboboxAsync(
        IPage page,
        Func<string, bool> matchesQuestion,
        string answer,
        string description)
    {
        var controls = page.Locator("select, input[role='combobox']");
        var matches = new List<ILocator>();
        for (var index = 0; index < await controls.CountAsync(); index++)
        {
            var control = controls.Nth(index);
            if (!await control.IsVisibleAsync())
            {
                continue;
            }

            var label = await control.EvaluateAsync<string>(
                "element => { const ids = (element.getAttribute('aria-labelledby') || '').split(/\\s+/).filter(Boolean); " +
                "const referenced = ids.map(id => document.getElementById(id)?.innerText || ''); " +
                "const native = element.labels ? Array.from(element.labels).map(label => label.innerText || '') : []; " +
                "return [element.getAttribute('aria-label') || '', ...referenced, ...native].join(' ').trim(); }");
            if (matchesQuestion(label))
            {
                matches.Add(control);
            }
        }

        if (matches.Count == 0)
        {
            return 0;
        }
        if (matches.Count > 1 && !description.Equals("veteran status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Found multiple {description} questions; leaving them for manual review.");
            return 0;
        }

        var filledCount = 0;
        foreach (var match in matches)
        {
            try
            {
                await match.ScrollIntoViewIfNeededAsync();
                var tagName = await match.EvaluateAsync<string>("element => element.tagName.toLowerCase()");
                if (tagName == "select")
                {
                    var options = match.Locator("option");
                    string? matchingValue = null;
                    for (var index = 0; index < await options.CountAsync(); index++)
                    {
                        var option = options.Nth(index);
                        var label = Regex.Replace(await option.InnerTextAsync(), @"\s+", " ").Trim();
                        if (MatchesRequestedAnswer(answer, label))
                        {
                            matchingValue = await option.GetAttributeAsync("value") ?? label;
                            break;
                        }
                    }
                    if (matchingValue is null)
                    {
                        Console.WriteLine($"Could not find the {answer} option for {description}; available choices: {string.Join(" | ", await options.AllInnerTextsAsync())}");
                        continue;
                    }
                    await match.SelectOptionAsync(matchingValue);
                }
                else
                {
                    await match.ClickAsync(new LocatorClickOptions { Force = true });
                    var options = page.Locator("[role='option'], .select__option");
                    var visibleOptions = new List<ILocator>();
                    for (var index = 0; index < await options.CountAsync(); index++)
                    {
                        var option = options.Nth(index);
                        if (await option.IsVisibleAsync() && MatchesRequestedAnswer(answer, Regex.Replace(await option.InnerTextAsync(), @"\s+", " ").Trim()))
                        {
                            visibleOptions.Add(option);
                        }
                    }

                    if (visibleOptions.Count != 1)
                    {
                        // Toggle closed safely without pressing Escape which closes the application modal
                        await match.ClickAsync(new LocatorClickOptions { Force = true });
                        Console.WriteLine($"Could not identify a unique {answer} option for {description}; please answer it manually.");
                        continue;
                    }
                    await visibleOptions[0].ClickAsync();
                }

                await match.EvaluateAsync<bool>(
                    "(element, value) => { element.setAttribute('data-codex-answered', value); return true; }",
                    answer);

                Console.WriteLine($"Answered the {description} question with {answer}.");
                filledCount++;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                Console.WriteLine($"Could not answer the {description} question; please answer it manually. ({ex.Message})");
            }
        }
        return filledCount;
    }

    private static bool MatchesRequestedAnswer(string answer, string optionLabel)
    {
        var normalizedAnswer = Regex.Replace(answer, @"[^\p{L}\p{N}]+", " ").Trim();
        var normalizedOption = Regex.Replace(optionLabel, @"[^\p{L}\p{N}]+", " ").Trim();
        if (normalizedAnswer.Equals(normalizedOption, StringComparison.OrdinalIgnoreCase) ||
            normalizedOption.StartsWith(normalizedAnswer + " ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedAnswer.Equals("No", StringComparison.OrdinalIgnoreCase) &&
            (normalizedOption.Contains("not a veteran", StringComparison.OrdinalIgnoreCase) ||
             normalizedOption.Contains("not a protected veteran", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> FillUniqueVisibleFieldAsync(IPage page, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var fields = page.Locator($"input[aria-label={JsonSerializer.Serialize(fieldName)}]");
        var visibleFields = new List<ILocator>();
        for (var index = 0; index < await fields.CountAsync(); index++)
        {
            var field = fields.Nth(index);
            if (await field.IsVisibleAsync())
            {
                visibleFields.Add(field);
            }
        }

        if (visibleFields.Count == 0)
        {
            return 0;
        }
        if (visibleFields.Count != 1)
        {
            Console.WriteLine($"Found multiple visible {fieldName} fields; leaving them unchanged.");
            return 0;
        }

        await visibleFields[0].ScrollIntoViewIfNeededAsync();
        await visibleFields[0].FillAsync(value);
        return 1;
    }

    private static async Task SelectLinkedInReferralAsync(IPage page)
    {
        var textFields = page.Locator("input[type='text']:not([role='combobox']), textarea");
        var questionTextFields = new List<ILocator>();
        for (var index = 0; index < await textFields.CountAsync(); index++)
        {
            var textField = textFields.Nth(index);
            if (await textField.IsVisibleAsync() && IsHowDidYouHearQuestion(await GetAccessibleQuestionLabelAsync(textField)))
            {
                questionTextFields.Add(textField);
            }
        }

        if (questionTextFields.Count == 1)
        {
            await questionTextFields[0].ScrollIntoViewIfNeededAsync();
            await questionTextFields[0].FillAsync("LinkedIn");
            Console.WriteLine("Answered ‘How did you hear about this job?’ with LinkedIn.");
            return;
        }
        if (questionTextFields.Count > 1)
        {
            Console.WriteLine("Found multiple text fields for ‘How did you hear about this job?’; leaving them for manual review.");
            return;
        }

        var comboBoxes = page.Locator("input.select__input[role='combobox']");
        var questionComboBoxes = new List<ILocator>();
        for (var index = 0; index < await comboBoxes.CountAsync(); index++)
        {
            var comboBox = comboBoxes.Nth(index);
            if (!await comboBox.IsVisibleAsync())
            {
                continue;
            }

            var labelText = await GetAccessibleQuestionLabelAsync(comboBox);
            if (IsHowDidYouHearQuestion(labelText))
            {
                questionComboBoxes.Add(comboBox);
            }
        }

        if (questionComboBoxes.Count == 1)
        {
            try
            {
                await questionComboBoxes[0].ScrollIntoViewIfNeededAsync();
                await questionComboBoxes[0].ClickAsync();
                var linkedinOption = page.GetByRole(AriaRole.Option, new() { Name = "LinkedIn", Exact = true });
                var visibleOptions = new List<ILocator>();
                for (var index = 0; index < await linkedinOption.CountAsync(); index++)
                {
                    if (await linkedinOption.Nth(index).IsVisibleAsync())
                    {
                        visibleOptions.Add(linkedinOption.Nth(index));
                    }
                }

                if (visibleOptions.Count == 1)
                {
                    await visibleOptions[0].ClickAsync();
                    Console.WriteLine("Answered ‘How did you hear about this job?’ with LinkedIn.");
                }
                else
                {
                    await questionComboBoxes[0].ClickAsync(new LocatorClickOptions { Force = true });
                    Console.WriteLine("The matching question did not offer a unique LinkedIn option; please answer it manually.");
                }
            }
            catch (PlaywrightException ex)
            {
                Console.WriteLine($"Could not select LinkedIn for the job-source question; please answer it manually. ({ex.Message})");
            }
            return;
        }
        if (questionComboBoxes.Count > 1)
        {
            Console.WriteLine("Found multiple ‘How did you hear about this job?’ dropdowns; leaving them for manual review.");
            return;
        }

        // Do not probe every combobox by clicking it. Application forms commonly
        // contain country/location controls, and their overlays can be blocked by
        // the sticky modal header. Only select a referral field when its label is
        // actually recognizable above; otherwise leave the optional field alone.
    }

    private static bool IsHowDidYouHearQuestion(string label)
    {
        var normalized = Regex.Replace(label, @"[^\p{L}\p{N}]+", " ").Trim();
        return normalized.Contains("how did you hear", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("referral source", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<string> GetAccessibleQuestionLabelAsync(ILocator element) =>
        element.EvaluateAsync<string>(
            "element => { const ids = (element.getAttribute('aria-labelledby') || '').split(/\\s+/).filter(Boolean); " +
            "const referenced = ids.map(id => document.getElementById(id)?.innerText || ''); " +
            "const native = element.labels ? Array.from(element.labels).map(label => label.innerText || '') : []; " +
            "return [element.getAttribute('aria-label') || '', ...referenced, ...native].join(' ').trim(); }");

    private static async Task CheckGdprConsentIfPresentAsync(IPage page)
    {
        const string consentId = "gdpr_demographic_data_consent_given_1";
        var consent = page.Locator($"input#{consentId}[type='checkbox']");
        if (await consent.CountAsync() == 1 && await consent.First.IsVisibleAsync() && !await consent.First.IsCheckedAsync())
        {
            try
            {
                await consent.First.CheckAsync();
            }
            catch (PlaywrightException)
            {
                // Fall back to the rendered label for styled/custom checkboxes.
            }

            if (!await consent.First.IsCheckedAsync())
            {
                var label = page.Locator($"label[for='{consentId}']");
                if (await label.CountAsync() == 1 && await label.First.IsVisibleAsync())
                {
                    try { await label.First.ClickAsync(); }
                    catch (PlaywrightException) { }
                }
            }

            if (await consent.First.IsCheckedAsync())
            {
                Console.WriteLine("Checked the specified GDPR demographic-data consent box.");
            }
            else
            {
                Console.WriteLine("The GDPR demographic-data consent box still needs your attention in the form.");
            }
        }
    }

    private static async Task<bool> OfferApplicationSubmissionAsync(IPage page, string jobTitle)
    {
        var submitButtons = page.GetByRole(
            AriaRole.Button,
            new() { NameRegex = new Regex("^submit application$", RegexOptions.IgnoreCase) });
        var visibleButtons = new List<ILocator>();
        for (var index = 0; index < await submitButtons.CountAsync(); index++)
        {
            var button = submitButtons.Nth(index);
            if (await button.IsVisibleAsync())
            {
                visibleButtons.Add(button);
            }
        }

        if (visibleButtons.Count != 1)
        {
            return false;
        }

        var invalidRequiredFields = page.Locator(
            "input:required:invalid, textarea:required:invalid, select:required:invalid, " +
            "[aria-required='true'][aria-invalid='true']");
        if (await invalidRequiredFields.CountAsync() > 0)
        {
            Console.WriteLine($"Required application fields remain incomplete for {jobTitle}; leaving the form open for you.");
            return false;
        }

        Console.WriteLine($"Submitting the completed Greenhouse application for {jobTitle}.");
        await visibleButtons[0].ClickAsync();
        await page.WaitForTimeoutAsync(1500);
        return true;
    }

    private static async Task<bool> HasSubmissionConfirmationAsync(IPage page)
    {
        if (page.IsClosed)
        {
            return false;
        }

        var confirmation = page.Locator("[role='dialog'], .modal, .confirmation, [class*='success'], [class*='submitted']");
        var target = await confirmation.CountAsync() > 0 ? confirmation.First : page.Locator("body");
        var text = await target.InnerTextAsync();
        return Regex.IsMatch(
            text,
            @"(?:your\s+)?application (?:has been|was) submitted|thank you for applying|successfully submitted",
            RegexOptions.IgnoreCase);
    }

    private static async Task DismissApplicationSuccessAndRestoreSearchAsync(IPage successPage, IPage searchPage)
    {
        if (!successPage.IsClosed)
        {
            var closeButtons = successPage.Locator("[role='dialog'] button[aria-label*='close' i], [role='dialog'] button:has-text('Done'), [role='dialog'] button:has-text('Close'), [role='dialog'] button:has-text('Dismiss')");
            if (await closeButtons.CountAsync() > 0 && await closeButtons.First.IsVisibleAsync())
            {
                await closeButtons.First.ClickAsync();
                await successPage.WaitForTimeoutAsync(300);
            }
        }

        if (!searchPage.IsClosed)
        {
            await searchPage.GotoAsync(
                SearchUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Console.WriteLine("Restored the Greenhouse search with your filters after application confirmation.");
        }
    }

    private static async Task<List<JobListing>> ReadListingsAsync(IPage page)
    {
        var links = page.Locator("a[href]");
        var count = await links.CountAsync();
        var listings = new List<JobListing>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < count; index++)
        {
            var link = links.Nth(index);
            if (!await link.IsVisibleAsync())
            {
                continue;
            }

            var href = await link.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(new Uri(page.Url), href, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.EndsWith("greenhouse.io", StringComparison.OrdinalIgnoreCase) ||
                !JobPath.IsMatch(uri.AbsolutePath))
            {
                continue;
            }

            var url = uri.GetLeftPart(UriPartial.Path);
            if (!seenUrls.Add(url))
            {
                continue;
            }

            var title = Regex.Replace(await link.InnerTextAsync(), @"\s+", " ").Trim();
            if (title.Length == 0)
            {
                title = uri.Segments.LastOrDefault()?.Trim('/') ?? "Greenhouse job";
            }
            listings.Add(new JobListing(title, url));
        }

        return listings;
    }

    private static async Task<List<JobListing>> WaitForListingsAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            Console.WriteLine("The Greenhouse search tab closed before listings could be read.");
            return;
        }

        var linkCount = await page.Locator("a[href]").CountAsync();
        var cardCount = await page.Locator(
            "article, [role='article'], [role='listitem'], [data-testid*='job' i], [class*='job-card' i], [class*='jobcard' i]").CountAsync();
        var candidates = await page.Locator("a[href]").EvaluateAllAsync<string[]>(
            "links => links.slice(0, 12).map(link => `${(link.innerText || '').trim().replace(/\\s+/g, ' ').slice(0, 70)} | ${link.href}`)");

        Console.WriteLine($"After waiting, the scanner saw {linkCount} link(s) and {cardCount} likely card element(s), but no job links matching the current rule.");
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
            var candidates = page.GetByRole(role, new() { NameRegex = new Regex(@"^apply(?: now)?$", RegexOptions.IgnoreCase) });
            for (var index = 0; index < await candidates.CountAsync(); index++)
            {
                var candidate = candidates.Nth(index);
                if (await candidate.IsVisibleAsync())
                {
                    controls.Add(candidate);
                }
            }
        }
        return controls;
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

    private static async Task SaveAppliedJobsAsync(string path, HashSet<string> urls)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, urls.OrderBy(url => url, StringComparer.OrdinalIgnoreCase), JsonOptions);
    }

    private sealed record JobListing(string Title, string Url);

    private sealed class ConsoleTeeWriter : TextWriter
    {
        private readonly TextWriter consoleOut;
        private readonly StreamWriter fileOut;

        public ConsoleTeeWriter(TextWriter consoleOut, string filePath)
        {
            this.consoleOut = consoleOut;
            this.fileOut = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }

        public override System.Text.Encoding Encoding => consoleOut.Encoding;

        public override void Write(char value)
        {
            consoleOut.Write(value);
            fileOut.Write(value);
        }

        public override void Write(string? value)
        {
            consoleOut.Write(value);
            fileOut.Write(value);
        }

        public override void WriteLine(string? value)
        {
            consoleOut.WriteLine(value);
            fileOut.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fileOut.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
