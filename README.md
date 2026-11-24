# Job Applicator

This repository contains local job-search tools for LinkedIn, Dice, Greenhouse, and Glassdoor. The tools find job listings, open application pages in Chromium, and use your local profile and resume notes to help complete supported fields. Each platform has its own runner and setup instructions.

## Project layout

- `LinkedIn/` - LinkedIn job search and application runner.
- `Dice/` - Dice search dashboard and Easy Apply runner.
- `Greenhouse/` - Greenhouse search and application runner.
- `Glassdoor/` - Glassdoor Recruiter matches and application runner.
- `Shared/` - profile parsing code shared by the runners.
- `personal.md` - your private, reusable application answers.
- `resume.md` - your private resume text used for resume-grounded suggestions.
- `experience-bank.md` - your private verified master experience bank used for per-job resume tailoring.
- `Resume.pdf` - your private fallback resume attachment.
- `tailored-resumes/` - private per-job archived tailored resumes (`*.pdf`, `resume.md`, `job-description.txt`, `tailor-report.json`).
- `templates/` - blank starter files; these do not contain personal data.
- `.env.example` - starter environment settings for the Dice and Glassdoor runners.

## Getting started

### 1. Install requirements

Install the .NET 8 SDK and PowerShell. The runners use Playwright with Chromium; the commands below install the browser runtime locally for each runner.

### 2. Set up your personal files

The repository already contains personal files if you have configured your profile. Do not overwrite them with the blank templates. If you are setting up a fresh copy, copy the templates from `templates/` to the repository root and replace every blank with an answer you want reused:

```powershell
Copy-Item .\templates\personal.md .\personal.md
Copy-Item .\templates\resume.md .\resume.md
```

The `templates/Resume.pdf` file is a blank layout guide. Replace it with your completed resume PDF at the repository root only when you are ready. Keep private files out of commits; `.gitignore` excludes the root `.env`, `personal.md`, `resume.md`, and `Resume.pdf` files.

### 3. Configure `.env.example` when using Dice or Glassdoor

`.env.example` shows the optional Dice and Glassdoor sign-in settings. Copy it to the location read by the runner you want, then edit the local copy:

```powershell
Copy-Item .\.env.example .\Dice\.env
Copy-Item .\.env.example .\Glassdoor\.env
```

Set `DICE_EMAIL` and `DICE_PASSWORD` in `Dice/.env` if you want the Dice runner to sign in for you. Set `GLASSDOOR_EMAIL` and `GLASSDOOR_PASSWORD` in `Glassdoor/.env` for the Glassdoor runner. Leave a platform's pair empty to sign in manually in that runner's Chromium window. Each `.env` file is plain text and must remain private; it is ignored by Git.

The LinkedIn and Greenhouse runners use interactive sign-in in their Chromium windows. The `LINKEDIN_*` variables in `.env.example` are legacy placeholders and are not read by the current LinkedIn runner.

### 4. Run a platform

Run the commands from the platform folder you want to use. For example, for LinkedIn:

```powershell
Set-Location .\LinkedIn
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

Use the same restore, build, Playwright install, and `dotnet run` sequence from `Dice/`, `Greenhouse/`, or `Glassdoor/` for those runners. The Dice dashboard can also be opened by opening `Dice/index.html` in a browser.

## Review before running

The runners have different application behavior. LinkedIn Easy Apply answers are left for your review and manual submission; its external-site flow may submit when all supported answers are complete. Dice, Greenhouse, and Glassdoor can submit completed applications automatically. Review the platform README and your profile answers before each run, and inspect the confirmation state before rerunning an application.

The runners keep browser profiles and application history under your Windows local app data folder. Treat those directories as private because the browser profiles contain signed-in sessions. Do not commit credentials, application history, resumes, or profile details.

## Per-job resume tailoring

All four runners (`LinkedIn`, `Dice`, `Greenhouse`, and `Glassdoor`) automatically tailor a 2-page ATS-friendly PDF resume for each job using `Shared/ResumeTailor.cs` and your private `experience-bank.md` (falling back to `resume.md` and `Resume.pdf` if unconfigured or unavailable).

Configure the provider in the git-ignored repository-root `.env` file (see `.env.example`):

- `RESUME_TAILOR_PROVIDER=auto` (default: uses Gemini when `GEMINI_API_KEY` is set, else local model; set `gemini`, `local`, or `off`)
- `GEMINI_API_KEY` and `GEMINI_MODEL` (defaults to `gemini-3.8-flash`)
- Or reuse `LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL` and `LINKEDIN_JOB_SEARCH_LOCAL_MODEL` for a local OpenAI-compatible model

Each generated resume is archived under `tailored-resumes/` (ignored by Git) alongside the scraped job description and a `tailor-report.json` listing matched keywords and any factual guardrail adjustments.

## Optional local resume drafts

The LinkedIn, Greenhouse, and Glassdoor runners can use a locally running model for resume-grounded draft answers. Dice uses the same local-model settings. See the platform README for the supported environment variables and setup. The model endpoint must use loopback HTTP; resume text is sent only to that local endpoint. Without a configured local model, unsupported questions remain for manual review.

## LinkedIn tests

Run the LinkedIn matching and storage tests without opening the browser:

```powershell
dotnet run --project .\LinkedIn\tests\LinkedInJobSearch.Tests.csproj
```
