using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private const string SearchUrl =
        "https://my.greenhouse.io/jobs/search?date_posted=past_five_days&salary=more_than_160k&work_type%5B%5D=remote&employment_type%5B%5D=full_time&employment_type%5B%5D=part_time&employment_type%5B%5D=contract&employment_type%5B%5D=temporary";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex JobPath = new(@"/jobs/(?!search(?:[/?#]|$))[^/?#]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var personalDataPath = FindPersonalDataPath();
        var personalData = await LoadPersonalDataAsync(personalDataPath);
        Directory.CreateDirectory(profileDirectory);
        var appliedJobs = await LoadAppliedJobsAsync(appliedJobsPath);

        if (personalData is null)
        {
            Console.WriteLine($"Personal settings were not found at {personalDataPath}. Copy personal.example.json to personal.json and fill it in.");
        }

        Console.WriteLine($"Applied jobs are tracked in {appliedJobsPath} ({appliedJobs.Count} recorded).");
        Console.WriteLine("The browser profile is local to your Windows account. Sign in there if Greenhouse asks.");

        using var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profileDirectory,
                new BrowserTypeLaunchPersistentContextOptions { Headless = false });
            var searchPage = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            await searchPage.GotoAsync(SearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            var waitingForVerificationCode = await PrepareGreenhouseSignInAsync(searchPage, personalData);

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
                if (await HasSubmissionConfirmationAsync(searchPage))
                {
                    await DismissApplicationSuccessAndRestoreSearchAsync(searchPage, searchPage);
                }

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
                        var applyControls = await FindVisibleApplyControlsAsync(jobPage);
                        if (applyControls.Count == 1)
                        {
                            Console.WriteLine("Opening the unique Apply control. Review the application and submit it yourself.");
                            var pagesBeforeApply = context.Pages.ToHashSet();
                            await applyControls[0].ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                            await jobPage.WaitForTimeoutAsync(800);
                            applicationPopup = context.Pages.FirstOrDefault(page => !pagesBeforeApply.Contains(page));
                            if (applicationPopup is not null)
                            {
                                Console.WriteLine("The application opened in a separate browser tab.");
                            }
                            var applicationPage = applicationPopup ?? jobPage;
                            var filledFields = await FillRequestedApplicationFieldsAsync(applicationPage, personalData);
                            if (filledFields > 0)
                            {
                                Console.WriteLine($"Filled {filledFields} requested application field(s). Review them before continuing.");
                            }
                            await SelectLinkedInReferralAsync(applicationPage);
                            await CheckGdprConsentIfPresentAsync(applicationPage);
                            var submitted = await OfferApplicationSubmissionAsync(applicationPage, listing.Title);
                            if (submitted && await HasSubmissionConfirmationAsync(applicationPage))
                            {
                                appliedJobs.Add(listing.Url);
                                await SaveAppliedJobsAsync(appliedJobsPath, appliedJobs);
                                Console.WriteLine("Greenhouse confirmed the application. Recorded as applied.");
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
                    finally
                    {
                        if (applicationPopup is not null && !applicationPopup.IsClosed)
                        {
                            await applicationPopup.CloseAsync();
                        }
                        if (!jobPage.IsClosed)
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

    private static async Task<bool> PrepareGreenhouseSignInAsync(IPage page, PersonalData? personalData)
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

        if (string.IsNullOrWhiteSpace(personalData?.Email))
        {
            Console.WriteLine("Personal settings do not contain an email. Enter it and request the Greenhouse security code in the browser.");
            return true;
        }

        await visibleEmailFields[0].FillAsync(personalData.Email);
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

    private static async Task<int> FillRequestedApplicationFieldsAsync(IPage page, PersonalData? personalData)
    {
        if (personalData is null)
        {
            return 0;
        }

        var filledCount = 0;
        var fullLegalName = string.Join(' ', new[] { personalData.LegalFirstName, personalData.LegalLastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        filledCount += await FillFullLegalNameQuestionsAsync(page, fullLegalName);

        if (personalData.AuthorizedToWorkInUs is bool authorized)
        {
            var authorizationQuestion = page.GetByLabel(
                "Are you legally authorized to work for any employer in the United States?",
                new() { Exact = true });
            if (await authorizationQuestion.CountAsync() == 1 && await authorizationQuestion.First.IsVisibleAsync())
            {
                var answer = authorized ? "Yes" : "No";
                try
                {
                    var tagName = await authorizationQuestion.First.EvaluateAsync<string>("element => element.tagName.toLowerCase()");
                    if (tagName == "select")
                    {
                        await authorizationQuestion.First.SelectOptionAsync(new SelectOptionValue { Label = answer });
                        filledCount++;
                    }
                    else
                    {
                        await authorizationQuestion.First.ClickAsync();
                        var answerOptions = page.GetByRole(AriaRole.Option, new() { Name = answer, Exact = true });
                        var visibleAnswerOptions = new List<ILocator>();
                        for (var index = 0; index < await answerOptions.CountAsync(); index++)
                        {
                            var option = answerOptions.Nth(index);
                            if (await option.IsVisibleAsync())
                            {
                                visibleAnswerOptions.Add(option);
                            }
                        }

                        if (visibleAnswerOptions.Count == 1)
                        {
                            await visibleAnswerOptions[0].ClickAsync();
                            filledCount++;
                        }
                        else
                        {
                            Console.WriteLine($"Could not identify a unique {answer} option for the work-authorization question; please answer it manually.");
                        }
                    }
                }
                catch (PlaywrightException ex)
                {
                    Console.WriteLine($"Could not fill the work-authorization answer; please answer it manually. ({ex.Message})");
                }
            }
        }

        if (personalData.RequiresVisaSponsorshipForUsEmployment is bool requiresSponsorship)
        {
            filledCount += await FillLabeledBooleanComboboxAsync(
                page,
                IsVisaSponsorshipQuestion,
                requiresSponsorship ? "Yes" : "No",
                "visa sponsorship");
        }

        filledCount += await FillUniqueVisibleFieldAsync(page, "Legal First Name", personalData.LegalFirstName);
        filledCount += await FillUniqueVisibleFieldAsync(page, "Legal Last Name", personalData.LegalLastName);
        return filledCount;
    }

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
            && normalized.Contains("visa", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("employment", StringComparison.OrdinalIgnoreCase);
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

        if (matches.Count != 1)
        {
            if (matches.Count > 1)
            {
                Console.WriteLine($"Found multiple {description} questions; leaving them for manual review.");
            }
            return 0;
        }

        try
        {
            var tagName = await matches[0].EvaluateAsync<string>("element => element.tagName.toLowerCase()");
            if (tagName == "select")
            {
                await matches[0].SelectOptionAsync(new SelectOptionValue { Label = answer });
            }
            else
            {
                await matches[0].ClickAsync();
                var options = page.GetByRole(AriaRole.Option, new() { Name = answer, Exact = true });
                var visibleOptions = new List<ILocator>();
                for (var index = 0; index < await options.CountAsync(); index++)
                {
                    if (await options.Nth(index).IsVisibleAsync())
                    {
                        visibleOptions.Add(options.Nth(index));
                    }
                }

                if (visibleOptions.Count != 1)
                {
                    await matches[0].PressAsync("Escape");
                    Console.WriteLine($"Could not identify a unique {answer} option for {description}; please answer it manually.");
                    return 0;
                }
                await visibleOptions[0].ClickAsync();
            }

            Console.WriteLine($"Answered the {description} question with {answer}.");
            return 1;
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"Could not answer the {description} question; please answer it manually. ({ex.Message})");
            return 0;
        }
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
                    await questionComboBoxes[0].PressAsync("Escape");
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

        var matchingComboBoxes = new List<ILocator>();
        for (var index = 0; index < await comboBoxes.CountAsync(); index++)
        {
            var comboBox = comboBoxes.Nth(index);
            if (!await comboBox.IsVisibleAsync())
            {
                continue;
            }

            try
            {
                await comboBox.ClickAsync();
                var linkedinOptions = page.GetByRole(AriaRole.Option, new() { Name = "LinkedIn", Exact = true });
                var visibleLinkedInOption = false;
                for (var optionIndex = 0; optionIndex < await linkedinOptions.CountAsync(); optionIndex++)
                {
                    if (await linkedinOptions.Nth(optionIndex).IsVisibleAsync())
                    {
                        visibleLinkedInOption = true;
                        break;
                    }
                }

                if (visibleLinkedInOption)
                {
                    matchingComboBoxes.Add(comboBox);
                }
                await comboBox.PressAsync("Escape");
            }
            catch (PlaywrightException)
            {
                // A field may disappear as the form updates; leave it for manual review.
            }
        }

        if (matchingComboBoxes.Count != 1)
        {
            if (matchingComboBoxes.Count > 1)
            {
                Console.WriteLine("More than one dropdown offers LinkedIn; leaving the dropdowns unchanged for manual review.");
            }
            return;
        }

        try
        {
            await matchingComboBoxes[0].ClickAsync();
            var option = page.GetByRole(AriaRole.Option, new() { Name = "LinkedIn", Exact = true });
            var visibleOptions = new List<ILocator>();
            for (var index = 0; index < await option.CountAsync(); index++)
            {
                if (await option.Nth(index).IsVisibleAsync())
                {
                    visibleOptions.Add(option.Nth(index));
                }
            }

            if (visibleOptions.Count == 1)
            {
                await visibleOptions[0].ClickAsync();
                Console.WriteLine("Selected LinkedIn in the matching dropdown.");
            }
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"Could not select LinkedIn in the dropdown; please review it manually. ({ex.Message})");
        }
    }

    private static bool IsHowDidYouHearQuestion(string label)
    {
        var normalized = Regex.Replace(label, @"[^\p{L}\p{N}]+", " ").Trim();
        return normalized.Equals("How did you hear about this job", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<string> GetAccessibleQuestionLabelAsync(ILocator element) =>
        element.EvaluateAsync<string>(
            "element => { const ids = (element.getAttribute('aria-labelledby') || '').split(/\\s+/).filter(Boolean); " +
            "const referenced = ids.map(id => document.getElementById(id)?.innerText || ''); " +
            "const native = element.labels ? Array.from(element.labels).map(label => label.innerText || '') : []; " +
            "return [element.getAttribute('aria-label') || '', ...referenced, ...native].join(' ').trim(); }");

    private static async Task CheckGdprConsentIfPresentAsync(IPage page)
    {
        var consent = page.Locator("input#gdpr_demographic_data_consent_given_1[type='checkbox']");
        if (await consent.CountAsync() == 1 && await consent.First.IsVisibleAsync() && !await consent.First.IsCheckedAsync())
        {
            await consent.First.CheckAsync();
            Console.WriteLine("Checked the specified GDPR demographic-data consent box.");
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

        Console.WriteLine($"Review the complete application for {jobTitle}.");
        Console.WriteLine("Type SUBMIT and press Enter to submit this application now, or press Enter to keep it open without submitting.");
        var approval = await Console.In.ReadLineAsync();
        if (!string.Equals(approval?.Trim(), "SUBMIT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

        var bodyText = await page.Locator("body").InnerTextAsync();
        return Regex.IsMatch(
            bodyText,
            @"application (?:has been|was) submitted|thank you for applying|application received|successfully submitted",
            RegexOptions.IgnoreCase);
    }

    private static async Task DismissApplicationSuccessAndRestoreSearchAsync(IPage successPage, IPage searchPage)
    {
        if (!successPage.IsClosed)
        {
            // Greenhouse presents the confirmation in a modal. Clicking outside it
            // dismisses the modal; reloading the explicit URL restores the filters
            // even if that dismissal navigates back to the default jobs page.
            await successPage.Mouse.ClickAsync(5, 5);
            await successPage.WaitForTimeoutAsync(300);
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

    private static string FindPersonalDataPath()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "personal.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "personal.json");
    }

    private static async Task<PersonalData?> LoadPersonalDataAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PersonalData>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Could not parse personal settings at {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task SaveAppliedJobsAsync(string path, HashSet<string> urls)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, urls.OrderBy(url => url, StringComparer.OrdinalIgnoreCase), JsonOptions);
    }

    private sealed record JobListing(string Title, string Url);

    private sealed record PersonalData(
        string? Email,
        string? LegalFirstName,
        string? LegalLastName,
        bool? AuthorizedToWorkInUs,
        bool? RequiresVisaSponsorshipForUsEmployment);
}
