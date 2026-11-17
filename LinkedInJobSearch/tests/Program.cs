static class AnswerMatcherTests
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("normalizes case and punctuation", NormalizesQuestions),
            ("reuses an answer at exactly 50% Jaccard similarity", AcceptsThresholdBoundary),
            ("does not reuse answers below 50% similarity", RejectsBelowThreshold),
            ("flags conflicting equally similar prior answers", FlagsAmbiguousAnswers),
            ("flags equally similar resume excerpts", FlagsAmbiguousResumeExcerpts),
            ("never suggests reuse for sensitive questions", BlocksSensitiveQuestions),
            ("does not reuse sensitive stored answers for other questions", BlocksSensitiveSourceAnswers),
            ("does not excerpt sensitive resume details", BlocksSensitiveResumeExcerpts),
            ("suggests only configured, resume-supported answers", SuggestsConfiguredResumeAnswers),
            ("does not suggest answers for unsupported or sensitive questions", LeavesUnsupportedResumeAnswersBlank),
            ("suggests answers only when the field is blank", SuggestsAnswersOnlyForBlankFields),
            ("resolves resume.md at the project root", ResolvesProjectRootResumePath),
            ("starts a fresh mouse-idle window and preserves browser answers", DetectsMouseIdleWaitAndManualAnswers),
            ("selects an external Apply link before the no-Easy-Apply skip path", SelectsSupportedApplyButton),
            ("recognizes accessible external Apply links and unwraps LinkedIn redirects", DetectsExternalApplyLinks),
            ("resumes only after manual handoff and a live source page", ValidatesExternalApplyHandoff),
            ("suggests only a verbatim resume excerpt", SuggestsResumeExcerpt)
        };

        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }

        await PersistsOnlyExplicitlyAddedAnswers();
        Console.WriteLine("PASS persists confirmed answer records");
        await LoadsConfiguredResumeAnswers();
        Console.WriteLine("PASS loads only supported local resume-answer keys");
        return 0;
    }

    private static void NormalizesQuestions()
    {
        var first = AnswerMatcher.Normalize("Are you experienced with React?");
        var second = AnswerMatcher.Normalize("React, experienced!");
        Equal(first, second);
    }

    private static void AcceptsThresholdBoundary()
    {
        var result = AnswerMatcher.FindPreviousAnswer(
            "React dotnet",
            [Entry("React dotnet cloud cloudops", "Yes")]);
        Equal(AnswerSuggestionKind.PreviousAnswer, result.Kind);
        Equal(0.5, result.Similarity, 0.000001);
        Equal("Yes", result.Answer);
    }

    private static void RejectsBelowThreshold()
    {
        var result = AnswerMatcher.FindPreviousAnswer(
            "React dotnet",
            [Entry("React dotnet cloud cloudops azure", "Yes")]);
        Equal(AnswerSuggestionKind.None, result.Kind);
    }

    private static void FlagsAmbiguousAnswers()
    {
        var result = AnswerMatcher.FindPreviousAnswer(
            "Are you experienced with React?",
            [
                Entry("Are you experienced with React?", "Yes"),
                Entry("Are you experienced with React?", "No")
            ]);
        Equal(AnswerSuggestionKind.AmbiguousPreviousAnswers, result.Kind);
        Equal<string?>(null, result.Answer);
        Equal(2, result.ConflictingAnswers?.Count ?? 0);
    }

    private static void BlocksSensitiveQuestions()
    {
        var questions = new[]
        {
            "Are you authorized to work in the United States?",
            "Will you now or in the future require visa sponsorship?",
            "Do you have a disability?",
            "What is your veteran status?",
            "Have you ever been convicted of a crime?",
            "What are your salary expectations?",
            "Please self-identify your race and ethnicity.",
            "What is your personal email address?",
            "What is your phone number?"
        };

        foreach (var question in questions)
        {
            var result = AnswerMatcher.FindPreviousAnswer(question, [Entry(question, "Yes")]);
            Equal(AnswerSuggestionKind.SensitiveQuestion, result.Kind);
            Equal(AnswerSuggestionKind.SensitiveQuestion, AnswerMatcher.FindResumeExcerpt(question, "Authorized to work in the US.").Kind);
        }
    }

    private static void FlagsAmbiguousResumeExcerpts()
    {
        var result = AnswerMatcher.FindResumeExcerpt(
            "React production applications",
            "Built React applications for production teams.\nManaged React production applications for finance.");
        Equal(AnswerSuggestionKind.AmbiguousResumeExcerpts, result.Kind);
        Equal<string?>(null, result.Answer);
    }

    private static void BlocksSensitiveSourceAnswers()
    {
        var result = AnswerMatcher.FindPreviousAnswer(
            "What was your experience with React?",
            [Entry("What was your experience with React salary?", "100000")]);
        Equal(AnswerSuggestionKind.None, result.Kind);
    }

    private static void BlocksSensitiveResumeExcerpts()
    {
        var result = AnswerMatcher.FindResumeExcerpt(
            "What was your React experience?",
            "React experience salary expectations are $100,000.");
        Equal(AnswerSuggestionKind.None, result.Kind);
    }

    private static void SuggestsConfiguredResumeAnswers()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["professional_software_engineering_years"] = "15",
            ["csharp_dotnet_experience"] = "Yes",
            ["client_facing_technical_consulting_or_presales"] = "Yes",
            ["production_software_systems_experience"] = "Yes"
        };

        var years = AnswerMatcher.FindResumeAnswer(
            "How many years of professional software engineering experience do you have?",
            answers);
        Equal(AnswerSuggestionKind.ResumeAnswer, years.Kind);
        Equal("15", years.Answer);

        var dotnet = AnswerMatcher.FindResumeAnswer(
            "Do you have professional experience developing applications with C#/.NET?",
            answers);
        Equal(AnswerSuggestionKind.ResumeAnswer, dotnet.Kind);
        Equal("Yes", dotnet.Answer);

        var consulting = AnswerMatcher.FindResumeAnswer(
            "Do you have client-facing technical consulting or pre-sales experience?",
            answers);
        Equal(AnswerSuggestionKind.ResumeAnswer, consulting.Kind);
        Equal("Yes", consulting.Answer);

        var production = AnswerMatcher.FindResumeAnswer(
            "Have you personally designed and deployed production software systems?",
            answers);
        Equal(AnswerSuggestionKind.ResumeAnswer, production.Kind);
        Equal("Yes", production.Answer);
    }

    private static void LeavesUnsupportedResumeAnswersBlank()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["professional_software_engineering_years"] = "15"
        };

        var azureYears = AnswerMatcher.FindResumeAnswer(
            "How many years of hands-on Microsoft Azure experience do you have?",
            answers);
        Equal(AnswerSuggestionKind.ResumeAnswerUnavailable, azureYears.Kind);
        Equal<string?>(null, azureYears.Answer);

        var workAuthorization = AnswerMatcher.FindResumeAnswer(
            "Are you legally authorized to work in the United States?",
            new Dictionary<string, string> { ["work_authorization"] = "Yes" });
        Equal(AnswerSuggestionKind.SensitiveQuestion, workAuthorization.Kind);
        Equal<string?>(null, workAuthorization.Answer);
    }

    private static void SuggestsAnswersOnlyForBlankFields()
    {
        Equal(true, ManualAnswerWindow.ShouldSuggestAnswer(null, "15"));
        Equal(true, ManualAnswerWindow.ShouldSuggestAnswer("", "Yes"));
        Equal(false, ManualAnswerWindow.ShouldSuggestAnswer("Already answered", "15"));
        Equal(false, ManualAnswerWindow.ShouldSuggestAnswer("No", "Yes"));
        Equal(false, ManualAnswerWindow.ShouldSuggestAnswer(null, null));
    }

    private static void ResolvesProjectRootResumePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkedInJobSearchTests-{Guid.NewGuid():N}");
        var executableDirectory = Path.Combine(root, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(executableDirectory);
        try
        {
            File.WriteAllText(Path.Combine(root, "LinkedInJobSearch.csproj"), "");
            Equal(
                root,
                ResumePaths.FindProjectRoot(executableDirectory, Path.GetTempPath()));
            Equal(
                Path.Combine(root, "resume.md"),
                ResumePaths.GetResumePath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LoadsConfiguredResumeAnswers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LinkedInJobSearchTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "resume-answers.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{"professional_software_engineering_years":"15"}""");
            var store = await ResumeAnswerStore.LoadAsync(path);
            Equal(
                "15",
                store.FindAnswer("How many years of professional software engineering experience?")
                    .Answer);

            await File.WriteAllTextAsync(path, """{"unsupported_answer_key":"value"}""");
            try
            {
                await ResumeAnswerStore.LoadAsync(path);
                throw new InvalidOperationException("Expected unsupported resume answer keys to be rejected.");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SuggestsResumeExcerpt()
    {
        const string excerpt = "Built React applications for production teams.";
        var result = AnswerMatcher.FindResumeExcerpt(
            "Tell us about your React production applications",
            $"Unrelated opening sentence. {excerpt}");
        Equal(AnswerSuggestionKind.ResumeExcerpt, result.Kind);
        Equal(excerpt, result.Answer);
    }

    private static void SelectsSupportedApplyButton()
    {
        Equal(ApplyButtonKind.EasyApply, ApplyButtonHandling.Select(1, 1, 1));
        Equal(ApplyButtonKind.ExternalApplyLink, ApplyButtonHandling.Select(0, 1, 0));
        Equal(ApplyButtonKind.Apply, ApplyButtonHandling.Select(0, 0, 1));
        Equal(ApplyButtonKind.None, ApplyButtonHandling.Select(0, 0, 0));
        Equal(ApplyButtonKind.None, ApplyButtonHandling.Select(2, 1, 1));
        Equal(ApplyButtonKind.None, ApplyButtonHandling.Select(0, 2, 1));
        Equal(ApplyButtonKind.None, ApplyButtonHandling.Select(0, 0, 2));
    }

    private static void DetectsExternalApplyLinks()
    {
        var linkedinPage = new Uri("https://www.linkedin.com/jobs/view/123");
        const string bambooHrRedirect =
            "https://www.linkedin.com/safety/go/?url=https%3A%2F%2Fmhc%2Ebamboohr%2Ecom%2Fcareers%2F88&urlhash=HYji&mt=7TC4X2pxSQo3sxJcbKkAT-6eEEPL6kEKXIZaAMiAWM19lyEJb9cCAJ85oBoIiVpHM-iD6MVfph-ooMTwBlaT0LI_Z4cLZblkvDGA6G-4uvnynncImV6HuA3_OyLBsbz2_YohvIwYKioRH_3NtM5Lqp7jjbj8X5_VpZ5HikP2JjI&isSdui=true";
        var bambooHrDestination = ApplyButtonHandling.ResolveDestination(linkedinPage, bambooHrRedirect);
        Equal(new Uri("https://mhc.bamboohr.com/careers/88"), bambooHrDestination);
        Equal(true, ApplyButtonHandling.IsApplySpanText("Apply"));
        Equal(true, ApplyButtonHandling.IsApplySpanText("  Apply "));
        Equal(false, ApplyButtonHandling.IsApplySpanText("Easy Apply"));
        Equal(false, ApplyButtonHandling.IsApplySpanText("Apply now"));
        Equal(
            true,
            ApplyButtonHandling.IsExternalApplyLink(
                linkedinPage,
                "Apply",
                bambooHrRedirect));
        Equal(true, ApplyButtonHandling.IsSafeExternalDestination(bambooHrDestination!));

        const string redirect =
            "https://www.linkedin.com/safety/go/?url=https%3A%2F%2Fcareers.example.com%2Fjobs%2F123%3Fsrc%3DLinkedIn&urlhash=JBk5";
        var destination = ApplyButtonHandling.ResolveDestination(linkedinPage, redirect);
        Equal(new Uri("https://careers.example.com/jobs/123?src=LinkedIn"), destination);
        Equal(
            true,
            ApplyButtonHandling.IsExternalApplyLink(
                linkedinPage,
                "Apply",
                redirect));
        Equal(
            true,
            ApplyButtonHandling.IsExternalApplyLink(
                linkedinPage,
                "Apply",
                "https://careers.example.com/jobs/123"));
        Equal(
            false,
            ApplyButtonHandling.IsExternalApplyLink(
                linkedinPage,
                "Apply",
                "/jobs/apply/123"));
        Equal(
            false,
            ApplyButtonHandling.IsExternalApplyLink(
                linkedinPage,
                "Company website",
                "https://careers.example.com/jobs/123"));
        Equal(true, ApplyButtonHandling.IsSafeExternalDestination(destination!));
        Equal(false, ApplyButtonHandling.IsSafeExternalDestination(new Uri("http://careers.example.com/jobs/123")));
    }

    private static void ValidatesExternalApplyHandoff()
    {
        Equal(true, ApplyButtonHandling.ShouldContinueAfterManualHandoff(true, false));
        Equal(false, ApplyButtonHandling.ShouldContinueAfterManualHandoff(false, false));
        Equal(false, ApplyButtonHandling.ShouldContinueAfterManualHandoff(true, true));
    }

    private static void DetectsMouseIdleWaitAndManualAnswers()
    {
        Equal(TimeSpan.FromSeconds(30), ManualAnswerWindow.IdleDuration);
        Equal(
            false,
            ManualAnswerWindow.HasBeenIdleFor(TimeSpan.Zero, ManualAnswerWindow.IdleDuration));
        Equal(true, ManualAnswerWindow.HasBeenIdleFor(TimeSpan.FromSeconds(30), ManualAnswerWindow.IdleDuration));
        Equal(false, ManualAnswerWindow.HasBeenIdleFor(TimeSpan.FromSeconds(29.999), ManualAnswerWindow.IdleDuration));
        Equal(TimeSpan.FromMilliseconds(250), ManualAnswerWindow.PollInterval);
        Equal(true, ManualAnswerWindow.ShouldWaitForQuestionPage(true, false, false));
        Equal(true, ManualAnswerWindow.ShouldWaitForQuestionPage(false, true, false));
        Equal(true, ManualAnswerWindow.ShouldWaitForQuestionPage(false, true, true));
        Equal(false, ManualAnswerWindow.ShouldWaitForQuestionPage(true, false, true));
        Equal(false, ManualAnswerWindow.ShouldWaitForQuestionPage(true, false, false, isReviewPage: true));
        Equal(
            true,
            ManualAnswerWindow.ShouldWaitForQuestionPage(
                ManualAnswerWindow.HasAnswer("text", "Already filled"),
                hasQuestionSection: false,
                isResumeSelectionPage: false));
        Equal(
            true,
            ManualAnswerWindow.ShouldWaitForQuestionPage(
                hasQuestionFields: true,
                hasQuestionSection: false,
                isResumeSelectionPage: false));
        Equal(false, ManualAnswerWindow.HasAnswer("text", ""));
        Equal(true, ManualAnswerWindow.HasAnswer("text", "Typed in LinkedIn"));
        Equal(false, ManualAnswerWindow.HasAnswer("textarea", "   "));
        Equal(true, ManualAnswerWindow.HasAnswer("radio", "option-value"));
        Equal(true, ManualAnswerWindow.HasAnswer("checkbox", null, isChecked: true));
        Equal(true, ManualAnswerWindow.HasAnswer("checkbox", null, isChecked: false));
        Equal(true, ManualAnswerWindow.IsUnchanged("Manual answer", "Manual answer"));
        Equal(false, ManualAnswerWindow.IsUnchanged("Original", "Edited"));
    }

    private static async Task PersistsOnlyExplicitlyAddedAnswers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LinkedInJobSearchTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "confirmed-answers.json");
        try
        {
            var store = await ConfirmedAnswerStore.LoadAsync(path);
            Equal(0, store.Entries.Count);
            await store.AddAndSaveAsync(path, "React experience?", "5 years");
            var reloaded = await ConfirmedAnswerStore.LoadAsync(path);
            Equal(1, reloaded.Entries.Count);
            Equal("React experience?", reloaded.Entries[0].Question);
            Equal("5 years", reloaded.Entries[0].Answer);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ConfirmedAnswer Entry(string question, string answer) =>
        new(question, answer, DateTimeOffset.UtcNow);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void Equal(double expected, double actual, double delta)
    {
        if (Math.Abs(expected - actual) > delta)
        {
            throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
        }
    }
}
