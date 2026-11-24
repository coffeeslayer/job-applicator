# Workspace Rules — Job Applicator

## Browser Lifecycle & Timeout Policy (All Applications)
- **Applies to every application in this repository**: `Dice`, `Greenhouse`, `LinkedIn`, `Glassdoor`, and any shared Playwright helpers under `Shared/`.
- **Never timeout and close the browser**:
  - Always disable Playwright default action and navigation timeouts (`SetDefaultTimeout(0)` and `SetDefaultNavigationTimeout(0)`) on every `IBrowserContext` and every `IPage` (including popups/newly opened tabs via `context.Page`).
  - Do not add hard wall-clock timeouts or deadlines that abort sign-in, page loads, wizard navigation, form filling, or submission confirmation and exit or close the browser.
  - Never automatically call `context.CloseAsync()` or tear down the browser on completion, error, cancellation, or closed/redirected stdin (`Console.ReadLine() == null`). Always keep the browser window open until the user explicitly closes it.
  - Keep failed or stalled job/application tabs open for live inspection instead of closing them in `finally` blocks when an error or stall occurs.

## Stall Evaluation & Refactor Loop
Whenever any runner stalls or hits a blocker:
1. **Evaluate why the runner stalled** — inspect the runner's log (`logs/<runner>.log`), stall diagnostics (`logs/<runner>-stall.json`), browser state/history, and form/DOM structure.
2. **Refactor to resolve the blockers** — fix the underlying automation, selector, field-matching, or profile-lookup bug in code (and update `personal.md` or shared helpers when appropriate) rather than working around it manually.
3. **Restart the runner and repeat** — rebuild, restart the runner, verify it progresses past the previous blocker, and repeat until all eligible listings complete cleanly.
