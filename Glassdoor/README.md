# Glassdoor Job Search

A .NET 8 console workflow that follows the Greenhouse applicator's Playwright and persistent Chromium pattern. It opens your Glassdoor Recruiter matches at `https://www.glassdoor.com/recruiter/matches`. The matches themselves come from the preferences saved in that Glassdoor account.

## Run

```powershell
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

Before running, keep your private application answers in the repository-root `personal.md`; all runners read this same file. It is listed in `.gitignore`. The app also reads the repository-root `resume.md` for resume-grounded answers and uploads the repository-root `Resume.pdf` when a form has a single resume file field.

For automatic email/password sign-in, copy the example environment file and set both values:

```powershell
Copy-Item ..\.env.example .\.env
```

Set `GLASSDOOR_EMAIL` and `GLASSDOOR_PASSWORD` in `Glassdoor/.env`. The runner submits these values to Glassdoor's sign-in form and does not print them. Leave both empty to sign in manually in the Chromium window. If Glassdoor requests a one-time code, captcha, or other verification, complete it in that window. The `.env` file is plain text, so protect it and do not share or commit it. Google sign-in is not automated.

The app waits up to 15 seconds for job links before reporting zero results and prints link diagnostics if none are recognized. It treats a link as a match when it is on `glassdoor.com` and points at a job listing (`/job-listing/`, `/partner/jobListing`, or a `jl=` job id). It skips application URLs containing `talenthop` or `bestjobtool`, and it skips cards already marked Applied. It opens each other match in a separate tab and clicks a single clearly labeled **Apply** or **Easy Apply** control when one is available.

On forms, it fills matching answers from `personal.md`, uploads `Resume.pdf` when the field is a resume upload, then uses an optional local model to draft other supported answers from `resume.md`. Set `LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL` to an HTTP loopback endpoint and `LINKEDIN_JOB_SEARCH_LOCAL_MODEL` to its model ID to enable drafts; the resume is sent only to that local endpoint. The app leaves unsupported or ambiguous questions for manual review and does not submit while any are unanswered. When every recognized question is answered, required fields appear valid, and one **Submit application**, **Submit**, **Send application**, or **Apply to job** button is visible inside the form, the app clicks it. It records the match only after a success message is detected. If confirmation is not detected, or a question needs you, the tab stays open: type **A** to record the application, **S** to skip it, or press Enter to leave it unrecorded. Scroll the matches page and press Enter to scan again; type **Q** to exit.

The persistent browser profile and application history are saved under `%LOCALAPPDATA%\GlassdoorJobSearch`. Keep that folder private because the browser profile contains your Glassdoor session. Application history is keyed by the Glassdoor job id or listing URL. The app does not store your password except in the local `.env` file you create.
