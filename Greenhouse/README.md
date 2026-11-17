# Greenhouse Job Search

A .NET 8 console workflow that follows the LinkedIn applicator's Playwright and persistent Chromium pattern. It opens the Greenhouse search with these filters: posted in the past five days, salary above $160,000, remote, and full-time, part-time, contract, or temporary.

## Run

```powershell
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

Before running, keep your private application answers in the repository-root `personal.md`; all runners read this same file. It is listed in `.gitignore`. Add optional `Email`, `Legal First Name`, and `Legal Last Name` entries there if you want Greenhouse to fill those fields. The app also reads the repository-root `resume.md` for resume-grounded answers. If Greenhouse asks you to sign in and no email is configured, enter it in the browser; otherwise the app fills it and requests Greenhouse's email security code. Enter that code in the browser yourself and finish signing in. Press Enter in the console and the app loads the filtered search URL again. Google sign-in is not automated because Google rejects the Playwright-controlled browser as insecure.

The app waits up to 15 seconds for job links before reporting zero results and prints link/card diagnostics if none are recognized. It skips application URLs containing `talenthop` or `bestjobtool`. It opens each other loaded job in a separate tab and clicks a single clearly labeled **Apply** control when one is available. On forms, it fills matching answers from `personal.md`, then uses an optional local model to draft other supported answers from the repository-root `resume.md`. Set `LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL` to an HTTP loopback endpoint and `LINKEDIN_JOB_SEARCH_LOCAL_MODEL` to its model ID to enable drafts; the resume is sent only to that local endpoint. The app leaves unsupported or ambiguous questions for manual review and does not submit while any are unanswered. When one **Submit application** button is visible, all recognized questions are answered, and required fields appear valid, the app clicks it automatically and records the application only after Greenhouse confirms submission. After confirmation, it dismisses the success modal and reloads the filtered search URL before continuing. Scroll or adjust the search in the Greenhouse tab and press Enter to scan newly loaded results; type **Q** to exit.

The persistent browser profile and application history are saved under `%LOCALAPPDATA%\GreenhouseJobSearch`. Keep that folder private because the browser profile contains your Greenhouse session. Application history is keyed by the job URL. The app does not store your password. It submits a completed application automatically when it finds exactly one visible **Submit application** button and no required fields appear invalid, then records the job only after Greenhouse confirms submission.
