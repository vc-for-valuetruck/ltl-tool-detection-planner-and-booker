# LTL Stability Verification

This pull request exists to force the LTL application through the normal PR review and CI path after the immediate stabilization fixes were applied.

## Scope being verified

- Angular LTL Billing Worklist badge filtering uses signal-safe binding.
- Backend LTL sorting keeps missing/null values last in both ascending and descending directions.
- The LTL application remains aligned to the Search -> Match -> Assign -> Bill workflow.

## Files to review on main

- `web/src/app/features/ltl/ltl-search.html`
- `src/LtlTool.Api/Features/Ltl/LtlLoadService.cs`

## Expected checks

PR checks should validate the current LTL application state through the repository workflow configuration. Do not merge this verification PR until the checks complete successfully or any failures are reviewed and fixed in a follow-up PR.
