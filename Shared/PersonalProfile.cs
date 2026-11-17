using System.Text.RegularExpressions;

public sealed record PersonalProfile(
    string? Residence,
    string? Email,
    string? LegalFirstName,
    string? LegalLastName,
    string? SalaryExpectations,
    string? Citizenship,
    string? RequiresSponsorship,
    string? VeteranStatus,
    string? Race,
    string? Gender,
    string? Pronouns,
    string? PrivacyConsent,
    string? AiIntegrityAgreement,
    string? ReferralSource,
    string? LinkedInUrl,
    string? AddressLine1 = null,
    string? PostalCode = null,
    string? Country = null,
    string? PreferredFirstName = null,
    string? CurrentEmployer = null,
    string? CurrentJobTitle = null,
    string? School = null,
    string? Degree = null,
    string? Over18 = null,
    string? EmploymentRestrictions = null,
    string? RelatedToEmployee = null,
    string? ReferredByEmployee = null,
    string? RecentGovernmentEmployment = null,
    string? AiAgentsExperience = null,
    string? ApplicationCertification = null,
    string? ProfessionalCertifications = null,
    string? HispanicOrLatino = null,
    string? SexualOrientation = null,
    string? TransgenderIdentity = null,
    string? Disability = null,
    string? ActiveDuty = null,
    string? PreviouslyEmployedAtBox = null,
    string? WillingToRelocate = null,
    string? ResidesInUnitedStates = null)
{
    private static readonly Regex EntryPattern = new(
        @"(?im)^\s*[-*]?\s*(?<key>[\p{L}][\p{L}\p{N} /_-]*?)\s*:\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled);

    public static string FindPath(params string[] startingPaths)
    {
        foreach (var startingPath in startingPaths)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "personal.md");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
        }

        return Path.Combine(Path.GetFullPath(startingPaths[0]), "personal.md");
    }

    public static async Task<PersonalProfile?> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var values = EntryPattern.Matches(await File.ReadAllTextAsync(path))
            .Cast<Match>()
            .ToDictionary(
                match => Normalize(match.Groups["key"].Value),
                match => match.Groups["value"].Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

        string? Read(params string[] keys) => keys
            .Select(Normalize)
            .Where(values.ContainsKey)
            .Select(key => values[key])
            .FirstOrDefault();

        return new PersonalProfile(
            Read("Residence", "Location"),
            Read("Email", "Email address"),
            Read("Legal first name", "First name"),
            Read("Legal last name", "Last name"),
            Read("Salary expectations", "Salary"),
            Read("US citizenship", "Citizenship"),
            Read("Requires sponsorship", "Visa sponsorship"),
            Read("Veteran status", "Veteran"),
            Read("Race/ethnicity", "Race"),
            Read("Gender"),
            Read("Pronouns"),
            Read("Privacy consent", "Privacy"),
            Read("AI integrity agreement", "AI integrity"),
            Read("Referral source", "How did you hear about this job"),
            Read("LinkedIn profile URL", "LinkedIn URL"),
            Read("Address Line 1", "Street address"),
            Read("Postal Code", "ZIP Code", "ZIP"),
            Read("Country"),
            Read("Preferred first name", "First name"),
            Read("Current employer", "Current company"),
            Read("Current job title", "Current title"),
            Read("School", "University"),
            Read("Degree"),
            Read("Over 18", "18 or older"),
            Read("Non-compete or other employment restriction", "Non-compete", "Employment restriction"),
            Read("Related to a current OneStream employee", "Related to an employee"),
            Read("Referred by a current OneStream employee", "Employee referral"),
            Read("Served in the U.S. armed forces or worked for a government employer in the past five years"),
            Read("Experience launching AI agents into production", "AI agent experience"),
            Read("Application answers certification", "Application certification"),
            Read("Professional certifications", "Certifications"),
            Read("Hispanic/Latino status", "Hispanic or Latino"),
            Read("Sexual orientation"),
            Read("Transgender identity", "Transgender"),
            Read("Disability or chronic condition", "Disability"),
            Read("Currently active member of the U.S. Armed Forces", "Active duty"),
            Read("Previously employed at Box", "Worked at Box"),
            Read("Willing to relocate or commute to a designated office three days per week", "Willing to relocate"),
            Read("Currently resides in the United States", "Resides in the United States"));
    }

    public string? AnswerForQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        var normalized = Regex.Replace(question, @"[^\p{L}\p{N}]+", " ").Trim();
        if (normalized.Contains("address line 1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("street address", StringComparison.OrdinalIgnoreCase))
        {
            return AddressLine1;
        }
        if (normalized.Contains("postal code", StringComparison.OrdinalIgnoreCase) ||
            ContainsWord(normalized, "zip"))
        {
            return PostalCode;
        }
        if (normalized.Contains("country", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("authorized to work", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("work authorization", StringComparison.OrdinalIgnoreCase))
        {
            return Country;
        }
        if (normalized.Contains("preferred first name", StringComparison.OrdinalIgnoreCase))
        {
            return PreferredFirstName;
        }
        if (normalized.Contains("school", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("university", StringComparison.OrdinalIgnoreCase))
        {
            return School;
        }
        if (ContainsWord(normalized, "degree"))
        {
            return Degree;
        }
        if (normalized.Contains("current company", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("current employer", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentEmployer;
        }
        if (normalized.StartsWith("current job title", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("current title", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentJobTitle;
        }
        if (normalized.Contains("professional certification", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("certifications do you currently hold", StringComparison.OrdinalIgnoreCase))
        {
            return ProfessionalCertifications;
        }
        if (normalized.Equals("website", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("personal website", StringComparison.OrdinalIgnoreCase))
        {
            return LinkedInUrl;
        }
        if ((normalized.Contains("employed at box", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("worked at box", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("work for box", StringComparison.OrdinalIgnoreCase)) &&
            normalized.Contains("ever", StringComparison.OrdinalIgnoreCase))
        {
            return PreviouslyEmployedAtBox;
        }
        if (normalized.Contains("relocate", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("commute to this position", StringComparison.OrdinalIgnoreCase))
        {
            return WillingToRelocate;
        }
        if (normalized.Contains("currently reside in the united states", StringComparison.OrdinalIgnoreCase))
        {
            return ResidesInUnitedStates;
        }
        if (normalized.Contains("consent to process", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacyConsent;
        }
        if (normalized.Contains("over 18", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("18 years old", StringComparison.OrdinalIgnoreCase))
        {
            return Over18;
        }
        if (normalized.Contains("non compete", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("other agreement", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("restrict your employment", StringComparison.OrdinalIgnoreCase))
        {
            return EmploymentRestrictions;
        }
        if (normalized.Contains("related to anyone", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("related to a current", StringComparison.OrdinalIgnoreCase))
        {
            return RelatedToEmployee;
        }
        if (normalized.Contains("referred by a current", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("employee referral", StringComparison.OrdinalIgnoreCase))
        {
            return ReferredByEmployee;
        }
        if (normalized.Contains("armed forces", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("employed by any u s federal", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("government employer in the past five years", StringComparison.OrdinalIgnoreCase))
        {
            return RecentGovernmentEmployment;
        }
        if (normalized.Contains("ai agents", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ai agent", StringComparison.OrdinalIgnoreCase))
        {
            return AiAgentsExperience;
        }
        if (normalized.Contains("sexual orientation", StringComparison.OrdinalIgnoreCase))
        {
            return SexualOrientation;
        }
        if (normalized.Contains("transgender", StringComparison.OrdinalIgnoreCase))
        {
            return TransgenderIdentity;
        }
        if (normalized.Contains("disability", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("chronic condition", StringComparison.OrdinalIgnoreCase))
        {
            return Disability;
        }
        if ((normalized.Contains("hispanic", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("latino", StringComparison.OrdinalIgnoreCase)) &&
            !normalized.Contains("race", StringComparison.OrdinalIgnoreCase))
        {
            return HispanicOrLatino;
        }
        if (normalized.Contains("certify", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("answers", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("application", StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationCertification;
        }
        if (ContainsAll(normalized, "linkedin", "profile") || ContainsAll(normalized, "linkedin", "url"))
        {
            return LinkedInUrl;
        }
        if (ContainsAll(normalized, "ai", "integrity"))
        {
            return AiIntegrityAgreement;
        }
        if (normalized.Contains("privacy", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacyConsent;
        }
        if (normalized.Contains("pronoun", StringComparison.OrdinalIgnoreCase))
        {
            return Pronouns;
        }
        if (normalized.Contains("veteran", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("military service", StringComparison.OrdinalIgnoreCase))
        {
            return VeteranStatus;
        }
        if (normalized.Contains("active member", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("active duty", StringComparison.OrdinalIgnoreCase))
        {
            return ActiveDuty;
        }
        if ((normalized.Contains("hispanic", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("latino", StringComparison.OrdinalIgnoreCase)) &&
            !normalized.Contains("race", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (normalized.Contains("race", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ethnicity", StringComparison.OrdinalIgnoreCase))
        {
            return Race;
        }
        if (normalized.Contains("gender", StringComparison.OrdinalIgnoreCase) ||
            ContainsWord(normalized, "sex"))
        {
            return Gender;
        }
        if (normalized.Contains("salary", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("compensation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pay expectation", StringComparison.OrdinalIgnoreCase))
        {
            return SalaryExpectations;
        }
        if (normalized.Contains("sponsor", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("visa", StringComparison.OrdinalIgnoreCase))
        {
            return RequiresSponsorship;
        }
        if (normalized.Contains("citizen", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("work authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorized to work", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("eligible to work", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("right to work", StringComparison.OrdinalIgnoreCase))
        {
            return Citizenship;
        }
        if (normalized.Contains("about us", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("how did you hear", StringComparison.OrdinalIgnoreCase))
        {
            return ReferralSource;
        }
        if (normalized.Contains("city", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("state", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("location", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("residing", StringComparison.OrdinalIgnoreCase))
        {
            return Residence;
        }

        return null;
    }

    private static bool ContainsAll(string text, params string[] words) =>
        words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"[^\p{L}\p{N}]+", " ").Trim();
}
