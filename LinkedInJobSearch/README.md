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

The first run opens LinkedIn in Chromium. Sign in in that window if needed, wait for the results to appear, then press Enter in the console. The browser profile and listing state are saved under `%LOCALAPPDATA%\LinkedInJobSearch`; the profile contains your browser session data, so keep that folder private. No password is stored by this application.

For each listing, the app checks for an existing applied marker and prefers a unique visible **Easy Apply** button; if none exists, it can use a unique visible **Apply** button when it opens the supported on-site application flow. External Apply links are identified by a visible span whose exact text is **Apply**, and LinkedIn safety redirects are resolved to the company destination. For an external application, the app opens the destination in a separate browser tab and pauses so you can complete it manually. It does not fill fields, answer questions, or submit on the company site. Press Enter in the console when finished to close that tab and continue to the next listing; Ctrl+C stops the run. Unsupported on-site Apply flows and ambiguous controls are left for manual handling or skipped safely rather than guessed. In the supported on-site flow, the app selects the newest resume by its displayed date (or the first option if dates are unavailable), preserves current field values, and suggests answers only for blank fields. It waits for you to review answers and never clicks **Submit application** or changes other choices such as the follow-company checkbox. Review and submit manually in LinkedIn; after you press Enter, the app records the listing only if it detects LinkedIn's success confirmation. Confirmed applications are recorded in `applied-jobs.json`.

The Apply-link check runs on the selected job details before the app skips a listing for lacking Easy Apply. If an older run prints a message that it found no Easy Apply button and immediately advances without offering external manual handoff, rebuild and start the current source version.

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
