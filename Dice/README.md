# Dice Job Search

A local job-search dashboard with an optional Playwright runner for Dice Easy Apply. Open `index.html` in a browser to adjust the filters and open the Dice search. You can sign in directly in the runner's Chrome window or configure credentials in a local `.env` file.

The default search matches:

- Keywords: `.net ai react architect`
- Posted: past 3 days
- Workplace: remote
- Easy Apply: enabled

The job tracker stores listing links, status, and notes in the browser's local storage on the current device.

## Search dashboard

Open `index.html` in a modern browser. No installation or server is required.

## Playwright Easy Apply runner

The runner is a .NET 8 console app. It opens an isolated Chrome profile at `%LOCALAPPDATA%\DiceJobSearch\ChromeProfile` and navigates to the filtered Dice search. This separate profile is required because current Chrome versions block Playwright's remote-debugging connection to your regular profile. It cannot inherit your regular Chrome login. If the dedicated profile is signed out, the runner uses the optional local `.env` credentials to sign in, or waits for you to sign in directly in that Chrome window. Chrome keeps the session for future runs. After submitting an application, it detects Dice's success page, returns directly to the original filtered search URL, reloads the listings, and continues with the next unapplied result. It records successful applications locally and skips jobs already recorded or marked Applied in the result list.

From this folder, install the runner dependencies:

```powershell
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
```

For automatic email/password sign-in, fill in `DICE_EMAIL` and `DICE_PASSWORD` in the local `.env` file. The `.env` file is excluded from Git. The runner submits these values to Dice's sign-in form and does not print them. The file is plain text, so protect it and do not share or commit it. Leave both values empty to sign in manually. If Dice requests a one-time code or another verification step, complete it in Chrome.

Start the application workflow:

```powershell
dotnet run
```

The runner prints progress while opening Chrome and loading the search. It reads explicit personal answers from the shared `PersonalProfile.cs` parser and repository-root `personal.md`, and resume facts from repository-root `resume.md`. It skips application URLs containing `talenthop` or `bestjobtool` and clicks **Accept all** on Dice's cookie prompt when available. For blank fields, it uses a matching explicit personal answer first, then an optional local model draft supported by a verbatim resume quote. To enable drafts, set `LINKEDIN_JOB_SEARCH_LOCAL_MODEL_URL` to an HTTP loopback endpoint (for example `http://127.0.0.1:1234/v1`) and `LINKEDIN_JOB_SEARCH_LOCAL_MODEL` to its model ID in the same PowerShell window. The resume is sent only to that local endpoint; the runner refuses non-loopback URLs. Questions the profile or resume cannot answer, including personal preferences such as C2C availability and recruiter representation, stay open for you to answer in Chrome. Running `dotnet run` submits eligible results without another confirmation prompt after the application steps are complete; if the dedicated Chrome profile is signed out, the runner waits for you to sign in there first. If Dice has no matching results, the runner exits without submitting anything. The runner stops if Dice's page or expected buttons change; it does not retry an uncertain submission. If it stops after clicking Submit, check Dice's Applied Jobs before running it again.

The dedicated Chrome profile is stored under your Windows local app data, outside this project. The application history is stored in `.dice-application-state.json` and excluded from Git.