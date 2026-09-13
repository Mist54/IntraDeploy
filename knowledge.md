# IntraDeploy

> Windows desktop tool that deploys published ASP.NET / ASP.NET Core folders to IIS, with optional SQL Server `.bak` restore, versioned target folders, and a confirmation dialog that shows live IIS state before anything changes.

## Tech Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| Language | C# | LangVersion implicit (Framework project) / 7.3 in helper SDK projects |
| Runtime | .NET Framework | 4.8 |
| UI | Windows Forms + Krypton.Toolkit | 105.26.7.201 |
| IIS admin | Microsoft.Web.Administration | 11.1.0 |
| SQL | Microsoft.Data.SqlClient | 7.0.2 |
| Logging | Serilog + Serilog.Sinks.File | 4.4.0 / 7.0.0 |
| JSON config edits | System.Text.Json | 10.0.12 |
| Package manager | NuGet PackageReference | MSBuild restore |
| Build | MSBuild (Visual Studio / Build Tools) | VS 2022+ |

**Key Dependencies:**
- `Krypton.Toolkit` — themed WinForms controls for the operator UI
- `Microsoft.Web.Administration` — create/update IIS sites, apps, pools (no `appcmd`)
- `Microsoft.Data.SqlClient` — connectivity checks and `RESTORE` from `.bak`
- `Serilog` / `Serilog.Sinks.File` — rolling daily logs under `LogDirectory`
- `System.Text.Json` — safe edits to deployed `appsettings.json`

## Project Structure

```
IntraDeploy/
├── Program.cs                 Entry point (mutex, elevation warning, MainForm)
├── App.config                 All operator defaults (paths, health, copy, hooks, prune) — see AppConfig
├── IntraDeploy.csproj         Main WinExe (.NET Framework 4.8, PackageReference)
├── IntraDeploy.slnx           Solution (main project only)
├── Configuration/             AppConfig — reads appSettings (clamped ints/bools)
├── Logging/                   Serilog bootstrap + SecretRedaction (test/call-site helper)
├── Models/                    Request/result DTOs and enums (IIS/DB modes, DeploymentMode, steps)
├── Services/                  Deployment pipeline and IIS/SQL/file/config/hooks work
├── UI/                        MainForm partials + confirmation/result/rollback/recent dialogs + Branding
├── Utilities/                 Validation, elevation, ports, recent paths, deploy history, folder picker
├── Images/                    Icon/logo assets (ICO/PNG embedded at build)
├── Properties/                AssemblyInfo, Resources, Settings
├── SmokeTest/                 Non-elevated logic smoke harness (IlProbe.exe)
├── LiveIisTest/               Elevated live IIS integration harness
├── Tools/MakeIcon/            Build-time PNG → multi-size ICO utility
├── knowledge.md               AI/onboarding knowledge base (this file)
├── README.md                  Human project overview
├── .gitignore                 .NET/VS + IDE + coding-agent trace ignores
└── documentation/             DEV.md (developers) + MANUAL.md (operators)
```

**Key directories:**
- `Services/` — all side effects (IIS, SQL, filesystem, config, hooks). UI must not call IIS/SQL APIs directly.
- `UI/` — operator-facing forms. `MainForm` is split: `MainForm.cs`, `MainForm.Layout.cs`, `MainForm.Events.cs`, `MainForm.Deploy.cs` + Designer.
- `Models/` — shared contracts between UI and services.
- `SmokeTest/` / `LiveIisTest/` — console harnesses referencing `bin\Debug\IntraDeploy.exe`.

**Entry points:**
- `Program.cs` — `Main()` → `Application.Run(new MainForm())` (`IntraDeploy.UI.MainForm`)
- `SmokeTest/SmokeTest.csx.cs` — validation/copy/config/secret-shape smoke tests
- `LiveIisTest/LiveIisTest.csx.cs` — live IIS create/update/cleanup tests (admin required)
- `Tools/MakeIcon/Program.cs` — regenerate `Images\IntraDeploy.ico`

## Architecture

IntraDeploy is a single WinForms executable. The UI collects a `DeploymentRequest`, shows live IIS state via `IisService.GetLiveState`, then runs `DeploymentService.DeployAsync`. The pipeline never reports success on a failed step; overwrite copies use a backup-swap so a failed copy restores the previous folder. On overwrite, `CopyCompareMode=SizeAndTime` (default) reuses files from `.deploying-bak` when **Length** and **LastWriteTimeUtc** are equal (`DateTime` equality — no content hash).

**Data flow:**
1. Operator fills `UI/MainForm` → builds `DeploymentRequest` (`Mode`, `DryRun`, …)
2. Confirmation dialog shows `IisLiveState`, mode/dry-run, and steps that will run
3. `DeploymentService` runs ordered `DeploymentStep`s for the selected mode (or dry-run exit after checks)
4. Progress events update the UI; cancel during copy shows “Cancelling after current file…”; Serilog writes `intradeploy-*.log`

**Key modules:**
- `DeploymentService` — orchestrates pipeline; respects `DeploymentMode` / `DryRun`; health retries via `HealthCheckRetries` / `HealthCheckRetryDelayMilliseconds`; failures → `DeploymentResult`
- `DeploymentPlan` — which steps a mode runs; confirm/dry-run step lists
- `IisService` — `ApplicationUnderSite` vs `SeparateSite`; never deletes sites/apps; reuses pools; `RetargetPhysicalPathAndRecycle` / `GetCurrentPhysicalPath` for rollback; optional HTTPS binding
- `FileService` — versioned copy under `DeploymentRoot\App\Version`; backup-swap on overwrite; parallel copy (`CopyMaxDegreeOfParallelism`); incremental reuse from `.deploying-bak`; cancel per file / every 50 files; `ListVersionFolders` / `PruneOldVersions` (never deletes live IIS path)
- `PostDeployHookRunner` — optional `.ps1` / HTTP hooks after health
- `PreflightService` — read-only Validate; pool CLR continue gate
- `DeployHistoryStore` — append-only `%LOCALAPPDATA%\IntraDeploy\history.jsonl` (no secrets)
- `DatabaseService` — restore only when `DatabaseMode.RestoreFromBak` + explicit overwrite confirm
- `ApplicationDetector` — `web.config` vs `*.runtimeconfig.json`
- `ConfigurationService` — optional connection-string update on **deployed** copy only
- `SecretRedaction` — detects secret-shaped strings for SmokeTest / call-site checks (not a Serilog filter)

## Commands

| Task | Command | Notes |
|------|---------|-------|
| Restore | `msbuild IntraDeploy.csproj /t:Restore` | PackageReference restore |
| Build (Debug) | `msbuild IntraDeploy.csproj /t:Build /p:Configuration=Debug` | Output: `bin\Debug\IntraDeploy.exe` |
| Build (Release) | `msbuild IntraDeploy.csproj /t:Build /p:Configuration=Release` | Output: `bin\Release\IntraDeploy.exe` |
| Rebuild | `msbuild IntraDeploy.csproj /t:Rebuild /p:Configuration=Debug` | |
| Smoke tests | Build main + `msbuild SmokeTest\SmokeTest.csproj /t:Build` then run `SmokeTest\bin\Debug\IlProbe.exe` | No elevation |
| Live IIS tests | Build main + `msbuild LiveIisTest\LiveIisTest.csproj /t:Build` then run elevated `LiveIisTest\bin\Debug\net48\LiveIisTest.exe` | Creates/removes test IIS objects |
| Make icon | `msbuild Tools\MakeIcon\MakeIcon.csproj /t:Build` then run with PNG/ICO paths | Optional asset regen |
| Lint / analyzers | *(none configured)* | Use MSBuild WarningLevel 4; treat CS warnings as errors with `/warnaserror` if desired |
| CI | `.github/workflows/build.yml`, `develop-ci.yml`, `code-quality.yml` | GitHub Actions: Debug + Release build of main project; SmokeTest build + `IlProbe.exe` run on Debug only (SmokeTest HintPath is hardcoded to `bin\Debug`). `build.yml` runs on main/master/develop; `develop-ci.yml` re-runs on develop with artifact verification; `code-quality.yml` fails on compiler/NuGet warnings (Release analysis build) |

Example (PowerShell, VS MSBuild on PATH or full path):

```powershell
& "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" IntraDeploy.csproj /t:Restore,Build /p:Configuration=Debug
```

## Coding Conventions

**Naming:**
- Files/classes: PascalCase (`DeploymentService.cs`)
- Methods/properties: PascalCase; private fields: `_camelCase`
- Namespaces: `IntraDeploy`, `IntraDeploy.Services`, `IntraDeploy.UI`, `IntraDeploy.Models`, …

**Imports:**
- Framework + third-party usings first, then `IntraDeploy.*`
- No barrel/index files; no path aliases

**Error handling:**
- Pipeline failures: `DeploymentStepException` → `DeploymentResult` with `FailedAt` + message
- Unexpected exceptions logged; UI thread + AppDomain handlers in `Program.cs`
- Credentials never logged (`DatabaseService` / connection strings); use `SecretRedaction` in tests when adding summary surfaces
- SQL login credentials are session-only in the UI (`MainForm` / `SqlCredentialsDialog`) — never written to `RecentPathsStore` / ini
- Read-only pre-flight: `PreflightService.Validate` (no file/IIS/SQL writes); UI **Validate** button
- Pool CLR mismatch: `PreflightService.CheckPoolGate` / Validate finding with `RequiresExplicitContinue`; Deploy prompts Continue; `EnsureApplicationPool` never mutates existing pools
- Health check: `HealthCheckPath` / timeout / `HealthCheckFailureFailsDeploy`; retries `HealthCheckRetries` (default 3) with delay `HealthCheckRetryDelayMilliseconds` × attempt; optional UI path override on `DeploymentRequest`
- File overwrite: backup-swap + optional `SizeAndTime` bak reuse; parallel copy; cancel checked per file and every 50 files
- SeparateSite HTTPS: optional `IisSettings.HttpsPort` + thumbprint; `IisService.EnsureHttpsBinding`; pre-flight `CertificateExists`
- Post-deploy hooks: `PostDeployHooks` / timeout / `PostDeployHookFailureFailsDeploy`; `PostDeployHookRunner` after health; exit code on `DeploymentResult`
- Version lifecycle: after successful deploy, prune beyond `MaxVersionsToKeep`; Rollback retargets IIS path only; history via `DeployHistoryStore`
- Dry-run / partial modes: `DeploymentRequest.DryRun` + `DeploymentMode` (`Full` / `FilesAndIis` / `IisOnly` / `DatabaseOnly` / `RecycleOnly`); `DeploymentPlan` drives skips; confirm dialog lists steps; dry-run exits before writes with `DryRunSummary`

**Logging:**
- Serilog static `Log.*`; `Logger.Initialize` / `Shutdown` in `Program`
- `LogContext` property `RunId` during a deploy
- Never bind `{ConnectionString}`, `{Password}`, or `{SqlPassword}` in templates; confirm/dry-run summaries describe intent only

**Comments:**
- XML docs on public types/methods for operator-facing safety rules
- Intent comments for IIS/SQL safety constraints (no narration comments)

**Style notes (from code):**
- Explicit `{}` blocks even for single-line `if`/`for`
- C# 7.3-compatible patterns in helper projects (`out var` used in main where available)
- Prefer early returns; keep IIS/SQL logic out of the UI layer

**Repo hygiene:**
- Coding-agent / IDE traces are never committed: `.cursor/`, `.cursorrules`, `.cursorignore`, `.freebuff/`, `.vscode/`, `.idea/`, `*.code-workspace` (see `.gitignore`)
- Keep `.gitignore` agent-tooling entries up to date when introducing a new agent/IDE

## Important Patterns

<important if="changing the deployment pipeline">
### Pipeline steps
Order is defined by `Models/DeploymentStep` and executed in `Services/DeploymentService`. Add a step only if UI progress reporting and failure attribution need it. Never mark `Success = true` if a critical step failed. Health-check failure allows success with a non-healthy `Health` outcome when `HealthCheckFailureFailsDeploy` is false (default); when true, unhealthy → `Success = false` / `FailedAt = HealthCheck`. Post-deploy hooks run after health; failure warns by default (`PostDeployHookFailureFailsDeploy=false`) or fails at `PostDeployHook`. Partial modes skip write steps via `DeploymentPlan`; `DryRun` reports the plan after checks and exits before copy/IIS/SQL/config writes.
</important>

<important if="changing file copy or overwrite">
### Overwrite copy (FileService)
Backup-swap still applies: existing version folder → `.deploying-bak`, then copy into target; restore bak on failure. When `CopyCompareMode=SizeAndTime`, a file is taken from bak if `Length` and `LastWriteTimeUtc` both equal the source (`==` on `DateTime` — full tick equality as returned by the filesystem; no content hash). Any other `CopyCompareMode` always copies from source. Parallelism: `CopyMaxDegreeOfParallelism` (1–16, default 4). Cancel: `ThrowIfCancellationRequested` per file and every 50 files; UI status “Cancelling after current file…”. Edge case: same size + same mtime with different bytes → bak reused (stale). Normal saves bump mtime.
</important>

<important if="working on IIS configuration">
### IIS safety
`IisService` must not delete sites/applications. `ApplicationUnderSite` never modifies parent bindings. `SeparateSite` must call `GetPortConflict` before creating bindings (HTTP and optional HTTPS). Prefer update-in-place for existing apps/sites. Require elevation for real IIS changes. Existing application pools are never mutated; CLR mismatch is a pre-flight continue-required warning (`GetApplicationPoolClrMismatch` / `DescribeClrMismatch`). HTTPS uses LocalMachine store certificates via `EnsureHttpsBinding`.
</important>

<important if="working on database restore">
### SQL safety
`DatabaseMode.UseExisting` → no DB operations. Restore requires validated `.bak` metadata, safe MOVE paths, and `ConfirmDatabaseOverwrite` when the DB already exists. Never log passwords. Empty `SqlUser` → Windows auth; non-empty → SQL login for that session only.
</important>

<important if="editing connection string or validate UI">
### Phase 1 operator features
`DeploymentRequest.UpdateConnectionString` / `ConnectionString` are wired from the MainForm checkbox + textbox (Build... uses `DatabaseService.BuildApplicationConnectionString`). Confirm dialog shows the action without echoing the string. `PortHelper.IsPortInUse` is a soft warn on SeparateSite + in pre-flight; hard binding conflicts stay in `IisService.GetPortConflict`. Health path UI + App.config keys (`HealthCheckRetries`, backoff) drive post-deploy health checks; pool CLR gate runs via `PreflightService` before confirm.
</important>

<important if="editing UI">
### UI layering
`UI/MainForm` is the real form, split across partials: `MainForm.cs` (fields/ctor), `MainForm.Layout.cs` (`BuildLayout`), `MainForm.Events.cs`, `MainForm.Deploy.cs` (Validate/Deploy/progress/cancel), plus `MainForm.Designer.cs`. Prefer Krypton controls; keep service calls async/off-UI-thread where already done. During copy cancel, set status to “Cancelling after current file…”.
</important>

<important if="writing or modifying tests">
### Test harnesses
SmokeTest and LiveIisTest reference `..\bin\Debug\IntraDeploy.exe` — build the main project first. Keep their PackageReference versions aligned with `IntraDeploy.csproj` to avoid MSB3277. LiveIisTest must clean up IIS objects it creates.
</important>

## Common Tasks

### Add a deployment pipeline step
1. Add a value to `Models/DeploymentStep.cs` in execution order.
2. Call `Report(...)` and do the work in `Services/DeploymentService.Deploy`.
3. Surface progress text from `UI/MainForm` progress handler if needed.
4. Build Debug and run `SmokeTest\bin\Debug\IlProbe.exe` for non-IIS logic.

### Change App.config defaults
1. Edit keys in `App.config` (paths, Serilog, health path/timeout/fail/retries, `MaxVersionsToKeep`, post-deploy hooks, `CopyMaxDegreeOfParallelism`, `CopyCompareMode`).
2. Read via `Configuration/AppConfig.cs` only — do not hardcode paths elsewhere.
3. Rebuild so `IntraDeploy.exe.config` is regenerated beside the EXE.
4. Keep `documentation/MANUAL.md` / `README.md` config tables in sync when adding keys.

### Fix an IIS deploy bug
1. Reproduce with LiveIisTest or a manual elevated run.
2. Change `Services/IisService.cs` (not the UI).
3. Rebuild main + LiveIisTest; run elevated LiveIisTest.

### Regenerate the application icon
1. Update `Images\IntraDeployLogo.png` if needed.
2. Build and run `Tools\MakeIcon` to write `Images\IntraDeploy.ico`.
3. Rebuild IntraDeploy (ICO is an embedded resource).
