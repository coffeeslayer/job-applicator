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

The first run opens LinkedIn in Chromium. Sign in in that window if needed, wait for the results to appear, then press Enter in the console. The app scans visible listings and automatically scrolls the left results pane when no unprocessed visible listings remain, continuing as more cards load until the pane stops advancing. It then reports completion and keeps Chromium open with your login session. Adjust the search in the browser, then press Enter in the console to scan again; type **Q** and press Enter to close the app and browser. The browser profile and listing state are saved under `%LOCALAPPDATA%\LinkedInJobSearch`; the profile contains your browser session data, so keep that folder private. At startup the app imports prior submitted IDs from the project’s `.linkedin-application-state.json` when present, then prints the path and count for `%LOCALAPPDATA%\LinkedInJobSearch\applied-jobs.json`. This JSON file stores job IDs and is checked before applying, so reruns skip them. External applications you finish manually are recorded when you press **Space** to continue. LinkedIn submissions without a detected confirmation are recorded in `uncertain-jobs.json` and also skipped on reruns. No password is stored by this application.

For each visible listing, the app searches listing attributes, common job-card layouts, and visible left-pane list items, then extracts a job ID from the card or a `/jobs/view/<id>` link. When a left-pane card has no link or ID, it selects the card and reads the selected job ID from LinkedIn's updated `currentJobId` URL parameter before checking application controls. Candidate cards are restricted to the visible left side of the window so the currently selected job in the details pane is not mistaken for another result. Diagnostics separately report the current details job ID and recognized left-pane cards. It checks for an existing applied marker and prefers a unique visible **Easy Apply** button; if none exists, it can use a unique visible **Apply** button when it opens the supported on-site application flow. External Apply links are identified by a visible span whose exact text is **Apply**, and LinkedIn safety redirects are resolved to the company destination. For an external application, the app opens the company site in a separate tab. If the URL contains `myworkdayjobs`, it closes that tab and skips to the next LinkedIn listing. If the URL contains `theladders`, it clicks **Save & Go to Next Section** four times, scrolling down before clicks three and four, checks both final-page boxes, and clicks **Apply to job**. Other external sites get a unique visible cookie consent button clicked when present, such as **Accept** or **OK**. With a configured local model and project-root `resume.md`, it fills blank supported fields only when an answer is directly supported by the resume. It advances through uniquely identified form steps and submits when all detected questions are answered. If a question is unsupported, the model is unavailable, or the form controls are ambiguous, it leaves the tab open for you to finish. Press **Space** in the console when done to close the company tab and continue to LinkedIn's next listing; Ctrl+C stops the run. Unsupported on-site Apply flows and ambiguous controls are left for manual handling or skipped safely rather than guessed. In the supported LinkedIn Easy Apply flow, the app selects the newest resume by its displayed date (or the first option if dates are unavailable), preserves current field values, and suggests answers only for blank fields. It waits for you to review answers and never clicks **Submit application** or changes other choices such as the follow-company checkbox. Review and submit those LinkedIn applications manually; after you press Enter, the app records the listing only if it detects LinkedIn's success confirmation. Confirmed applications are recorded in `applied-jobs.json`.

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

Whenever an Easy Apply page contains application-question fields, whether blank or already filled, the app starts a fresh **30-second** idle window when it detects the question page. Any mouse movement over the Playwright page resets the full timer; movement outside the page is not tracked. Starting a fresh window at question-page detection prevents time spent on earlier job pages from satisfying the wait. During the wait the app does not edit fields, advance the form, close the dialog, or move to another listing. If the page navigates or closes, the run stops without further form or listing actions; pressing Ctrl+C cancels the wait and stops the run. After an uninterrupted idle period, the app preserves each existing browser answer and lets you confirm keeping it or skip; it does not offer to replace prefilled values. For blank fields, it shows a matching previously confirmed answer or resume-based suggestion, if available. Choose **Y** to use a suggestion, **E** to enter a different answer manually, or **S** to skip that application question. The app asks for a separate confirmation before applying the answer. Confirmed non-sensitive answers can be saved for future matching.

The app compares normalized question text using Jaccard similarity: lowercase alphanumeric word sets, common question words removed, and intersection size divided by union size. A previously confirmed answer is suggested at **50% or greater** similarity. If different answers are within 10 percentage points of the top match, the question is treated as ambiguous and requires a manual answer. This is deterministic text matching, not a generative AI service.

For resume-based suggestions, put your resume in `LinkedInJobSearch\resume.md`, at the project root. This file is ignored by Git because it contains private information; the app reads it locally and does not send it to an AI service. The app can suggest a matching sentence or bullet verbatim (minimum 20% Jaccard similarity); it never writes new claims based on the resume. Equally similar excerpts are treated as ambiguous. Review every excerpt before confirming it.

For common questions with a concise, resume-supported answer, you can optionally create `%LOCALAPPDATA%\LinkedInJobSearch\resume-answers.json`. It accepts only these keys: `professional_software_engineering_years`, `microsoft_azure_years`, `csharp_dotnet_experience`, `client_facing_technical_consulting_or_presales`, and `production_software_systems_experience`. The app suggests a configured value only for a matching blank question, and still requires your confirmation before filling it. Do not enter an Azure years value unless you can verify the duration; missing or unsupported answers remain for you to answer manually. This answer file is local to your Windows account and is never stored in the repository. Keep only facts you have verified and remove any value you do not want suggested.

Confirmed non-sensitive question/answer pairs and their UTC confirmation timestamps are stored as plaintext JSON in `%LOCALAPPDATA%\LinkedInJobSearch\confirmed-answers.json`. You can inspect or remove entries in that file; protect your Windows account because answers may contain personal information. Sensitive/high-impact topics—including work authorization, visas/sponsorship, disability, veteran status, criminal history, compensation, demographic/EEO questions, and personal contact information—never get a prior-answer or resume suggestion and are not saved, even when you manually answer them. All these files are outside the repository and remain local to this Windows account.

Run the matching and storage tests without opening LinkedIn:

```powershell
dotnet run --project .\tests\LinkedInJobSearch.Tests.csproj
```

This app does not run until you start it. Review each application and LinkedIn's terms before use; it does not bypass login, bot checks, or other access controls.
