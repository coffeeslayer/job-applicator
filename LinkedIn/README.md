# LinkedIn Job Search

A .NET 8 console application that opens the configured LinkedIn job search in a visible Chromium window and processes listings with an **Easy Apply** or **Apply** button. It uses Playwright's persistent browser context so you can sign in interactively; the app never asks for or reads your password.

## Requirements

- .NET 8 SDK
- PowerShell (for Playwright's browser installation script)

## Run

From this directory:

```powershell
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

The first run opens LinkedIn in Chromium. Sign in in that window if needed, wait for the results to appear, then press Enter in the console. The app scans visible listings, automatically scrolls the left results pane when no unprocessed visible listings remain, and clicks LinkedIn’s visible next-page control when the pane stops moving. It continues until no more result pages are available, then reports completion and keeps Chromium open with your login session. Adjust the search in the browser, then press Enter in the console to scan again; type **Q** and press Enter to close the app and browser. The browser profile and listing state are saved under `%LOCALAPPDATA%\LinkedInJobSearch`; the profile contains your browser session data, so keep that folder private. At startup the app imports prior submitted IDs from the project’s `.linkedin-application-state.json` when present, then prints the path and count for `%LOCALAPPDATA%\LinkedInJobSearch\applied-jobs.json`. This JSON file stores job IDs and is checked before applying, so reruns skip them. External applications you finish manually are recorded when you press **Space** to continue. LinkedIn submissions without a detected confirmation are recorded in `uncertain-jobs.json` and also skipped on reruns. No password is stored by this application.

For each visible listing, the app searches listing attributes, common job-card layouts, and visible left-pane list items, then extracts a job ID from the card or a `/jobs/view/<id>` link. When a left-pane card has no link or ID, it selects the card and reads the selected job ID from LinkedIn's updated `currentJobId` URL parameter before checking application controls. Candidate cards are restricted to the visible left side of the window so the currently selected job in the details pane is not mistaken for another result. Diagnostics separately report the current details job ID and recognized left-pane cards. It checks for an existing applied marker and prefers a unique visible **Easy Apply** button; if none exists, it can use a unique visible **Apply** button when it opens the supported on-site application flow. External Apply links are identified by a visible span whose exact text is **Apply**, and LinkedIn safety redirects are resolved to the company destination. URLs containing `talenthop` or `bestjobtool` are skipped. For an external application, the app opens the company site in a separate tab. If the URL contains `myworkdayjobs`, it closes that tab and skips to the next LinkedIn listing. If the URL contains `theladders`, it clicks **Save & Go to Next Section** four times, scrolling down before clicks three and four, checks both final-page boxes, and clicks **Apply to job**. Other external sites get a unique visible cookie consent button clicked when present, such as **Accept** or **OK**. With a configured local model and the repository-root `resume.md`, it fills blank supported fields only when an answer is directly supported by the resume. It uses matching answers from the repository-root `personal.md` for explicit facts and choices. It advances through uniquely identified form steps and submits external applications when all detected questions are answered. If a question is unsupported, the model is unavailable, or controls are ambiguous, it leaves the tab open for you to finish. Press **Space** in the console when done to close the company tab and continue to LinkedIn's next listing; Ctrl+C stops the run. Unsupported on-site Apply flows and ambiguous controls are left for manual handling or skipped safely rather than guessed. In the supported LinkedIn Easy Apply flow, the app selects the newest resume by its displayed date (or the first option if dates are unavailable), preserves current field values, and applies a matching `personal.md` answer when available. For other supported questions, it can suggest an evidence-backed local model draft or a resume excerpt for you to review. It waits for you to review answers and never clicks **Submit application** or changes other choices such as the follow-company checkbox. Review and submit those LinkedIn applications manually; after you press Enter, the app records the listing only if it detects LinkedIn's success confirmation. Confirmed applications are recorded in `applied-jobs.json`.

The Apply-link check runs on the selected job details before the app skips a listing for lacking Easy Apply. If an older run prints a message that it found no Easy Apply button and immediately advances without offering external manual handoff, rebuild and start the current source version.

## Local AI drafts for external applications

The app can optionally fill blank text, email, phone, textarea, and select fields on an external company application using resume-grounded drafts from an already-running local model. It never uses a hosted AI service. Configure the local model in the same PowerShell window before starting the app:

```powershell
$env:LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL = "http://127.0.0.1:1234/v1"
$env:LINKEDIN_JOB_SEARCH_LOCAL_MODEL = "your-local-model-id"
dotnet run
```

The endpoint must use a literal loopback IP address and plain HTTP; the app disables proxy use and redirects and refuses non-loopback hosts. It checks `/models` locally before enabling drafts. The app does not install or launch a model service. Without a configured model, external forms are left for you to complete.

For each blank supported field, the app sends only its label, type, available select choices, job title, form title, and local `resume.md` text to the loopback endpoint. It fills a field only when the model provides an exact quote from the resume as evidence; unsupported claims, invalid choices, and answers over 1,000 characters are discarded. Contact details are accepted only when the exact value appears in the resume. Work authorization, immigration/sponsorship, EEO/demographic, disability, veteran, salary, and criminal-history questions are never sent to the model. Existing nonblank fields are preserved. If a question has no resume-supported answer or uses an unsupported field type, the app leaves it blank and pauses so you can finish the application. When every detected question is answered, it advances through uniquely identified **Next**, **Continue**, or **Review** buttons and clicks a unique **Submit** or **Apply** button. It records the application only after detecting a success message. On a manual handoff, finish and submit in the external tab, then press **Space** in the console to close it and continue to LinkedIn's next listing. Visible cookie banners with a unique **Accept** or **OK** button are accepted automatically.

## Application answers

Whenever an Easy Apply page contains application-question fields, whether blank or already filled, the app starts a fresh **30-second** idle window when it detects the question page. Any mouse movement over the Playwright page resets the full timer; movement outside the page is not tracked. Starting a fresh window at question-page detection prevents time spent on earlier job pages from satisfying the wait. During the wait the app does not edit fields, advance the form, close the dialog, or move to another listing. If the page navigates or closes, the run stops without further form or listing actions; pressing Ctrl+C cancels the wait and stops the run. After an uninterrupted idle period, the app preserves each existing browser answer and lets you confirm keeping it or skip; it does not offer to replace prefilled values. For blank fields, it checks saved key/value answers, confirmed answers, and resume-based suggestions. If none match, it asks you to enter an answer. Choose **Y** to use a suggestion, **E** to enter a different answer manually, or **S** to skip. It asks for confirmation before applying the answer and saves confirmed answers as exact question-to-answer key/value pairs for later. Similar question wording can match a saved key/value answer at **50%** similarity; close matches that have different answers are treated as ambiguous and require your input.

The app reads explicit application answers from the repository-root `personal.md`, including residence, salary, work authorization, sponsorship, demographic choices, pronouns, privacy, integrity, referral source, and LinkedIn profile URL. It compares normalized question text using Jaccard similarity: lowercase alphanumeric word sets, common question words removed, and intersection size divided by union size. A previously confirmed answer is suggested at **50% or greater** similarity. If different answers are within 10 percentage points of the top match, the question is treated as ambiguous and requires a manual answer.

For resume-based suggestions, keep your resume in the repository-root `resume.md`. This file is ignored by Git because it contains private information. Existing excerpt suggestions quote resume text verbatim; optional local-model drafts are restricted to a loopback endpoint and must include an exact resume quote as evidence. Unsupported or ambiguous answers remain for manual review.

For common questions with a concise, resume-supported answer, you can optionally create `%LOCALAPPDATA%\LinkedInJobSearch\resume-answers.json`. It accepts only these keys: `professional_software_engineering_years`, `microsoft_azure_years`, `csharp_dotnet_experience`, `client_facing_technical_consulting_or_presales`, and `production_software_systems_experience`. The app suggests a configured value only for a matching blank question, and still requires your confirmation before filling it. Do not enter an Azure years value unless you can verify the duration; missing or unsupported answers remain for you to answer manually. This answer file is local to your Windows account and is never stored in the repository. Keep only facts you have verified and remove any value you do not want suggested.

Confirmed non-sensitive question/answer pairs and their UTC confirmation timestamps are stored in `%LOCALAPPDATA%\LinkedInJobSearch\confirmed-answers.json` for fuzzy matching. Exact question-to-answer key/value pairs for all confirmed answers, including sensitive answers that you explicitly enter and confirm, are stored as a plaintext JSON object in `%LOCALAPPDATA%\LinkedInJobSearch\question-answers.json`. Key/value matches require your confirmation before use; sensitive answers are only matched against similar question wording and are not sent to the local model or used for resume suggestions. Inspect or remove entries you no longer want saved, and protect your Windows account because these files may contain personal information. They remain local to this Windows account and are not stored in the repository.

Run the matching and storage tests without opening LinkedIn:

```powershell
dotnet run --project .\tests\LinkedInJobSearch.Tests.csproj
```

This app does not run until you start it. Review each application and LinkedIn's terms before use; it does not bypass login, bot checks, or other access controls.
