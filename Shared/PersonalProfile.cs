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
    string? ResidesInUnitedStates = null,
    IReadOnlyDictionary<string, string>? AdditionalEntries = null,
    string? LegalName = null,
    string? Address = null,
    string? Clearance = null,
    string? AuthorizedToWork = null,
    string? Phone = null,
    string? ResumeFile = null,
    string? ClearanceLevel = null)
{
    private static readonly Regex EntryPattern = new(
        @"(?im)^\s*(?:[-*]\s+(?<key>[^:\r\n]+?)|(?<key>[\p{L}][\p{L}\p{N} /_-]*?))\s*:\s*(?<value>.+?)\s*$",
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

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in EntryPattern.Matches(await File.ReadAllTextAsync(path)))
        {
            var normalizedKey = Normalize(match.Groups["key"].Value);
            var val = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedKey) && !string.IsNullOrWhiteSpace(val))
            {
                values[normalizedKey] = val;
            }
        }

        string? Read(params string[] keys) => keys
            .Select(Normalize)
            .Where(values.ContainsKey)
            .Select(key => values[key])
            .FirstOrDefault();

        var legalFirst = Read("Legal first name", "First name");
        var legalLast = Read("Legal last name", "Last name");
        var legalName = Read("Legal name", "Full legal name") ??
            string.Join(' ', new[] { legalFirst, legalLast }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var addressLine1 = Read("Address Line 1", "Street address");
        var residence = Read("Residence", "Location");
        var fullAddress = Read("Address", "Full address") ??
            (string.IsNullOrWhiteSpace(addressLine1) ? residence : $"{addressLine1}, {residence}");

        return new PersonalProfile(
            residence,
            Read("Email", "Email address"),
            legalFirst,
            legalLast,
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
            addressLine1,
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
            Read("Hispanic/Latino", "Hispanic Latino", "Hispanic/Latino status", "Hispanic or Latino"),
            Read("Sexual orientation"),
            Read("Transgender identity", "Transgender"),
            Read("Disability or chronic condition", "Disability"),
            Read("Currently active member of the U.S. Armed Forces", "Active duty"),
            Read("Previously employed at Box", "Worked at Box"),
            Read("Willing to relocate or commute to a designated office three days per week", "Willing to relocate"),
            Read("Currently resides in the United States", "Resides in the United States", "Currently located in the United States", "Located in the United States"),
            values,
            legalName,
            fullAddress,
            Read("Eligible for U.S. Government Clearance", "Government clearance", "Security clearance", "Clearance") ?? "Yes",
            Read("Legally authorized to work in the United States", "Authorized to work in the United States", "Work authorization") ?? "Yes",
            Read("Phone", "Phone number", "Mobile", "Telephone"),
            Read("Resume file", "Resume path"),
            Read("Security clearance level", "Clearance level"));
    }

    public string? AnswerForQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        var normalized = Regex.Replace(question, @"[^\p{L}\p{N}]+", " ").Trim();

        if (AdditionalEntries is not null)
        {
            if (AdditionalEntries.TryGetValue(normalized, out var exact))
            {
                return exact;
            }

            foreach (var (key, value) in AdditionalEntries)
            {
                if (key.Length >= 14 &&
                    normalized.Length >= 14 &&
                    normalized.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
                if (normalized.Length >= 16 &&
                    key.Length >= normalized.Length &&
                    normalized.Length * 10 >= key.Length * 6 &&
                    key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        if (normalized.Equals("email", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("email address", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("what is your email", StringComparison.OrdinalIgnoreCase))
        {
            return Email;
        }
        if (normalized.Equals("phone", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("phone number", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("mobile number", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("telephone", StringComparison.OrdinalIgnoreCase))
        {
            return Phone;
        }
        if (normalized.Equals("first name", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("legal first name", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("what is your first name", StringComparison.OrdinalIgnoreCase))
        {
            return LegalFirstName;
        }
        if (normalized.Equals("last name", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("legal last name", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("what is your last name", StringComparison.OrdinalIgnoreCase))
        {
            return LegalLastName;
        }
        if (normalized.Contains("legal name", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("full legal name", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("full name", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("name", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("what is your legal name", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("what is your name", StringComparison.OrdinalIgnoreCase))
        {
            return LegalName ?? $"{LegalFirstName} {LegalLastName}".Trim();
        }
        if (normalized.Contains("address line 1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("street address", StringComparison.OrdinalIgnoreCase))
        {
            return AddressLine1;
        }
        if (normalized.Contains("what is your address", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("address", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("current address", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("mailing address", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("home address", StringComparison.OrdinalIgnoreCase))
        {
            return Address ?? AddressLine1 ?? Residence;
        }
        if (normalized.Contains("postal code", StringComparison.OrdinalIgnoreCase) ||
            ContainsWord(normalized, "zip"))
        {
            return PostalCode;
        }
        if (normalized.Contains("country", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("authorized to work", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("allowed to work", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("eligible to work", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("right to work", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("work authorization", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("citizen", StringComparison.OrdinalIgnoreCase))
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
        if (normalized.Contains("github", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("portfolio", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("github");
        }
        if (normalized.Equals("website", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("personal website", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("personal website");
        }
        if (normalized.Contains("notice period", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("start date", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("when can you start", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("earliest start", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("availability", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("notice period") ?? "2 weeks";
        }
        if (normalized.Contains("background check", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("background investigation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("drug screen", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("drug test", StringComparison.OrdinalIgnoreCase))
        {
            return "Yes";
        }
        if (normalized.Contains("secondary employment", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("competes with", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("competitive in nature", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("conflict of interest", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("1 ownership", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("disqualified or recused", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("recused yourself", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("secondary or competing employment or conflict of interest") ?? "No";
        }
        if (normalized.Contains("healthcare provider", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("government official", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("government military or civilian", StringComparison.OrdinalIgnoreCase) ||
            (normalized.Contains("currently", StringComparison.OrdinalIgnoreCase) &&
             normalized.Contains("government", StringComparison.OrdinalIgnoreCase) &&
             normalized.Contains("employee", StringComparison.OrdinalIgnoreCase)))
        {
            return AdditionalEntries?.GetValueOrDefault("healthcare provider or government official") ?? "No";
        }
        if (normalized.Contains("essential functions", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("can perform essential job functions with or without reasonable accommodation") ?? "Yes";
        }
        if ((normalized.Contains("text you", StringComparison.OrdinalIgnoreCase) ||
             ContainsWord(normalized, "sms") ||
             normalized.Contains("text message", StringComparison.OrdinalIgnoreCase)) &&
            (normalized.Contains("updates", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("application", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("notifications", StringComparison.OrdinalIgnoreCase)))
        {
            return AdditionalEntries?.GetValueOrDefault("sms text application updates consent") ?? "Yes";
        }
        if (normalized.Contains("resume", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("included", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("most recent", StringComparison.OrdinalIgnoreCase)))
        {
            return AdditionalEntries?.GetValueOrDefault("resume updated and included") ?? "Yes";
        }
        if (normalized.Contains("california applicant", StringComparison.OrdinalIgnoreCase))
        {
            var residentState = ExtractStateName(Residence);
            if (!string.Equals(residentState, "California", StringComparison.OrdinalIgnoreCase))
            {
                return AdditionalEntries?.GetValueOrDefault("california applicant notice") ?? "Not Applicable";
            }
        }
        if (normalized.Contains("state specific notices", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ccpa notification", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("please review and acknowledge", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("i acknowledge that i have read", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacyConsent ?? ApplicationCertification ?? "Yes";
        }
        if ((!normalized.Contains("referred by", StringComparison.OrdinalIgnoreCase) &&
             !normalized.Contains("related to", StringComparison.OrdinalIgnoreCase) &&
             !normalized.Contains("relative", StringComparison.OrdinalIgnoreCase) &&
             Regex.IsMatch(normalized, @"\b(ex|former|prior|previous|current)\b(?:\s+\w+){0,3}\s+\bemployee\b", RegexOptions.IgnoreCase)) ||
            ((normalized.Contains("employed at", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("employed by", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("worked at", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("work for", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("worked for", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("previously employed", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("previously been employed", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("currently performing work", StringComparison.OrdinalIgnoreCase)) &&
             (normalized.Contains("ever", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("previously", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("prior", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("past", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("currently", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("full time employee", StringComparison.OrdinalIgnoreCase))))
        {
            if (normalized.Contains("box", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(PreviouslyEmployedAtBox))
            {
                return PreviouslyEmployedAtBox;
            }
            return AdditionalEntries?.GetValueOrDefault("default answer for prior employer questions") ?? "No";
        }
        if (normalized.Contains("if the role is fully remote", StringComparison.OrdinalIgnoreCase) ||
            (normalized.Contains("workplace type", StringComparison.OrdinalIgnoreCase) &&
             normalized.Contains("remote", StringComparison.OrdinalIgnoreCase)))
        {
            return AdditionalEntries?.GetValueOrDefault("remote workplace availability") ?? "Yes, I can work this way now.";
        }
        if (normalized.Contains("relocate", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("commute", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("in office", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("days in office", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("onsite", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("on site", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("headquarters", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return WillingToRelocate ?? "No";
        }
        if (normalized.Contains("local to", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("bay area", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("san francisco", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("local to the san francisco bay area") ?? "No";
        }
        if (normalized.Contains("currently reside in the united states", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("located in the united states", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("living in the united states", StringComparison.OrdinalIgnoreCase) ||
            (normalized.Contains("in the united states", StringComparison.OrdinalIgnoreCase) &&
             (normalized.Contains("located", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("currently", StringComparison.OrdinalIgnoreCase))))
        {
            return ResidesInUnitedStates ?? "Yes";
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
        if (normalized.Contains("relative", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("familial", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("family member", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("family relationship", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("related to anyone", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("related to a current", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("related to an employee", StringComparison.OrdinalIgnoreCase))
        {
            return RelatedToEmployee ?? AdditionalEntries?.GetValueOrDefault("default answer for employee relatives") ?? "No";
        }
        if (normalized.Contains("if yes", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("name", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("relationship", StringComparison.OrdinalIgnoreCase)))
        {
            return AdditionalEntries?.GetValueOrDefault("name and relationship of relatives at company") ?? "None";
        }
        if (normalized.Contains("referred by a current", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("employee referral", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(normalized, @"\breferred by (?:an?\s+)?(?:\w+\s+){0,3}employee\b", RegexOptions.IgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("default answer for employee referral questions") ?? ReferredByEmployee ?? "No";
        }
        if (normalized.Contains("veteran", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("military service", StringComparison.OrdinalIgnoreCase))
        {
            return VeteranStatus ?? "I don't wish to answer";
        }
        if (normalized.Contains("served in the u s armed forces", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("employed by any u s federal", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("government employer in the past five years", StringComparison.OrdinalIgnoreCase) ||
            (normalized.Contains("armed forces", StringComparison.OrdinalIgnoreCase) &&
             (normalized.Contains("past five", StringComparison.OrdinalIgnoreCase) ||
              normalized.Contains("past 5", StringComparison.OrdinalIgnoreCase))))
        {
            return RecentGovernmentEmployment;
        }
        if (normalized.Contains("currently use ai tools", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("use ai tools in work", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("how you currently use ai tools in work life") ??
                   "I am an advanced AI user and have directly used AI to completely systemize actions and workflows.";
        }
        if (normalized.Contains("years of", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("backend", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("years of professional backend development experience") ?? "15";
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
            return HispanicOrLatino ?? "I don't wish to answer";
        }
        if (normalized.Contains("certify", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains("answers", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("application", StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationCertification;
        }
        if (ContainsAll(normalized, "linkedin", "profile") || ContainsAll(normalized, "linkedin", "url"))
        {
            if (normalized.StartsWith("did you", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("have you", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(LinkedInUrl) ? "No" : "Yes";
            }
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
            return VeteranStatus ?? "I don't wish to answer";
        }
        if (normalized.Contains("active member", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("active duty", StringComparison.OrdinalIgnoreCase))
        {
            return ActiveDuty ?? "I don't wish to answer";
        }
        if (ContainsWord(normalized, "race") ||
            normalized.Contains("ethnicity", StringComparison.OrdinalIgnoreCase))
        {
            return Race ?? "I don't wish to answer";
        }
        if (normalized.Contains("gender", StringComparison.OrdinalIgnoreCase) ||
            ContainsWord(normalized, "sex"))
        {
            return Gender ?? "Male";
        }
        if (normalized.Contains("salary", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("compensation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pay expectation", StringComparison.OrdinalIgnoreCase))
        {
            return SalaryExpectations;
        }
        if (normalized.Contains("sponsor", StringComparison.OrdinalIgnoreCase) ||
            ContainsWord(normalized, "visa"))
        {
            return RequiresSponsorship ?? "No";
        }
        if (normalized.Contains("clearance", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("security clearance", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("government clearance", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(ClearanceLevel) &&
                (normalized.Contains("level", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("type", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("which clearance", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("what clearance", StringComparison.OrdinalIgnoreCase)))
            {
                return ClearanceLevel;
            }
            return Clearance ?? "Yes";
        }
        if (normalized.Contains("citizen", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("u s person", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("export control", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("work authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorized to work", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("allowed to work", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("eligible to work", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("employment eligibility", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("proof of your identity", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("right to work", StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizedToWork ?? Citizenship ?? "Yes";
        }
        if (normalized.Contains("about us", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("how did you hear", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("referral source", StringComparison.OrdinalIgnoreCase))
        {
            return ReferralSource ?? "LinkedIn";
        }
        if ((normalized.Equals("state", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("current state", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("state province", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("state of residence", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("what state", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("which state", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("state territory", StringComparison.OrdinalIgnoreCase)) &&
            !ContainsWord(normalized, "city") &&
            !ContainsWord(normalized, "zip") &&
            !normalized.Contains("united states", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractStateName(Residence) ?? Residence;
        }
        if (ContainsWord(normalized, "city") &&
            !ContainsWord(normalized, "state") &&
            !ContainsWord(normalized, "zip"))
        {
            return ExtractCityName(Residence) ?? Residence;
        }
        if (ContainsWord(normalized, "city") ||
            ContainsWord(normalized, "zip") ||
            ContainsWord(normalized, "location") ||
            normalized.Contains("residing", StringComparison.OrdinalIgnoreCase))
        {
            return Residence;
        }
        if (normalized.Contains("voluntary", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("self identify", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("demographic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("equal employment", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("eeo", StringComparison.OrdinalIgnoreCase))
        {
            return AdditionalEntries?.GetValueOrDefault("default answer for unanswered voluntary eeo questions") ?? "I don't wish to answer";
        }

        return null;
    }

    public static async Task<PersonalProfile?> SaveAnswerAsync(string path, string question, string answer)
    {
        var cleanQuestion = Regex.Replace(question ?? "", @"[\r\n]+", " ");
        cleanQuestion = Regex.Replace(cleanQuestion, @"\s+", " ").Trim().TrimEnd('*', ':').Trim();
        var cleanAnswer = Regex.Replace(answer ?? "", @"[\r\n]+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleanQuestion) || string.IsNullOrWhiteSpace(cleanAnswer))
        {
            return await LoadAsync(path);
        }

        // Replace colons inside the key so EntryPattern splits unambiguously on the key/value separator.
        var safeKey = cleanQuestion.Replace(":", " -", StringComparison.Ordinal);
        var targetNormalized = Normalize(safeKey);
        var formattedLine = $"- {safeKey}: {cleanAnswer}";

        var lines = File.Exists(path)
            ? (await File.ReadAllLinesAsync(path)).ToList()
            : ["# Personal application answers", ""];

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var match = EntryPattern.Match(lines[i]);
            if (match.Success &&
                string.Equals(Normalize(match.Groups["key"].Value), targetNormalized, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = formattedLine;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(formattedLine);
            }
            else
            {
                lines.Add(formattedLine);
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(tempPath, lines);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return await LoadAsync(path);
    }

    private static string? ExtractCityName(string? residence)
    {
        if (string.IsNullOrWhiteSpace(residence)) return null;
        var comma = residence.IndexOf(',');
        return comma > 0 ? residence[..comma].Trim() : null;
    }

    private static string? ExtractStateName(string? residence)
    {
        if (string.IsNullOrWhiteSpace(residence)) return null;
        var match = Regex.Match(residence, @",\s*([A-Za-z]{2})\b");
        if (!match.Success) return null;
        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "AL" => "Alabama", "AK" => "Alaska", "AZ" => "Arizona", "AR" => "Arkansas",
            "CA" => "California", "CO" => "Colorado", "CT" => "Connecticut", "DE" => "Delaware",
            "DC" => "District of Columbia", "FL" => "Florida", "GA" => "Georgia", "HI" => "Hawaii",
            "ID" => "Idaho", "IL" => "Illinois", "IN" => "Indiana", "IA" => "Iowa",
            "KS" => "Kansas", "KY" => "Kentucky", "LA" => "Louisiana", "ME" => "Maine",
            "MD" => "Maryland", "MA" => "Massachusetts", "MI" => "Michigan", "MN" => "Minnesota",
            "MS" => "Mississippi", "MO" => "Missouri", "MT" => "Montana", "NE" => "Nebraska",
            "NV" => "Nevada", "NH" => "New Hampshire", "NJ" => "New Jersey", "NM" => "New Mexico",
            "NY" => "New York", "NC" => "North Carolina", "ND" => "North Dakota", "OH" => "Ohio",
            "OK" => "Oklahoma", "OR" => "Oregon", "PA" => "Pennsylvania", "RI" => "Rhode Island",
            "SC" => "South Carolina", "SD" => "South Dakota", "TN" => "Tennessee", "TX" => "Texas",
            "UT" => "Utah", "VT" => "Vermont", "VA" => "Virginia", "WA" => "Washington",
            "WV" => "West Virginia", "WI" => "Wisconsin", "WY" => "Wyoming",
            _ => match.Groups[1].Value
        };
    }

    private static bool ContainsAll(string text, params string[] words) =>
        words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"[^\p{L}\p{N}]+", " ").Trim();
}
