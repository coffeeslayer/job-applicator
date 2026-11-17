# Greenhouse Job Search

A .NET 8 console workflow that follows the LinkedIn applicator's Playwright and persistent Chromium pattern. It opens the Greenhouse search with these filters: posted in the past five days, salary above $160,000, remote, and full-time, part-time, contract, or temporary.

## Run

```powershell
dotnet restore
dotnet build
powershell -ExecutionPolicy Bypass -File .\bin\Debug\net8.0\playwright.ps1 install chromium
dotnet run
```

Before running, copy `personal.example.json` to `personal.json` and put your own values in it. `personal.json` is listed in `.gitignore` so your personal details are not committed. If Greenhouse asks you to sign in, the app accepts the cookie prompt, fills in the configured email, and requests Greenhouse's email security code. Enter that code in the browser yourself and finish signing in. Press Enter in the console and the app loads the filtered search URL again. Google sign-in is not automated because Google rejects the Playwright-controlled browser as insecure.

The app waits up to 15 seconds for job links before reporting zero results and prints link/card diagnostics if none are recognized. It opens each loaded job in a separate tab and clicks a single clearly labeled **Apply** control when one is available. On application forms, it fills full legal name questions with the configured first and last name together, and fills the configured U.S. work authorization, visa sponsorship, legal first name, and legal last name answers when those fields appear. For **How did you hear about this job?**, it types **LinkedIn** in a textbox or selects **LinkedIn** in a dropdown. It also checks the specified GDPR demographic-data consent box if present. If a unique **Submit application** button is available and required fields are valid, type **SUBMIT** at the console prompt to confirm that specific application. Otherwise the form stays open for you. After Greenhouse confirms an application, the app dismisses the success modal and reloads the filtered search URL before continuing. Scroll or adjust the search in the Greenhouse tab and press Enter to scan newly loaded results; type **Q** to exit.

The persistent browser profile and application history are saved under `%LOCALAPPDATA%\GreenhouseJobSearch`. Keep that folder private because the browser profile contains your Greenhouse session. Application history is keyed by the job URL. The app does not store your password or submit applications for you.
