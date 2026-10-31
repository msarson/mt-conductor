# MT Conductor

A standalone Windows app that shows one card per MultiTerminal (MT) project, open or not: PM, where I left off, task counts, PRs, pending questions, and a "needs you" dot. Background: ClarionLive/MultiTerminal issue #83.

## Hard rules
- **REST only.** Read everything from MT's API at http://localhost:5050 (source: F:\github\MultiTerminal\API\Controllers). Never read MT's SQLite database directly.
- **Never start, stop or kill MultiTerminal.exe.** If MT isn't reachable, show "MT offline" and keep retrying.
- PR status comes from `gh` called by the app, not through MT.
- Read-only first. Phases: 1) cards + "needs you" dot, 2) "Carry on without me" via /api/remote-mode, 3) an "Open" endpoint as an upstream PR to ClarionLive/MultiTerminal (until then, Open just says which window the project is in).

## Owner rules
- Ask before creating the GitHub repo (msarson/mt-conductor), pushing, merging, deleting, force-pushing or posting upstream.
- GitHub posts go out unsigned as the Owner (MULTITERMINAL_GH_SIGN=0); show a draft first. Tag @peterparker57 only when something needs him, in one consolidated comment.
- For git/gh inside MT panes, set GIT_CONFIG_COUNT=0. Never put tokens in URLs or on command lines.
- Every ticket gets a checklist straight away. Run the full build and test suite locally before any PR.

## Build
- `dotnet build MtConductor.slnx -m:1`
- `dotnet test MtConductor.slnx -m:1`
- Targets net8.0-windows with RollForward=Major (this machine has Windows Desktop runtime 9/10, not 8).
