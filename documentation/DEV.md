# IntraDeploy — Developer Guide

**Audience:** Developers maintaining or extending IntraDeploy.  
**Goal:** Understand architecture, set up a build, run checks, and change the codebase safely.  
**Type:** Explanation + how-to (Diátaxis).

Related: [../README.md](../README.md) · [MANUAL.md](MANUAL.md) · [../knowledge.md](../knowledge.md)

---

## 1. What this project is

IntraDeploy is a **.NET Framework 4.8 WinForms** executable. Operators use it to deploy a Visual Studio “Publish as Folder” output to IIS, with optional SQL Server restore and connection-string patching on the deployed copy.

There is no web server and no background service—everything runs in-process in `IntraDeploy.exe`.

---

## 2. Architecture

### Layers

| Layer | Location | Responsibility |
|-------|----------|----------------|
| Entry | `Program.cs` | Single-instance mutex, Serilog init, elevation warning, unhandled exception logging |
| UI | `UI/` | Collect input, confirmation, progress, results. `MainForm` partials: `.cs` / `.Layout` / `.Events` / `.Deploy` + Designer |
| Orchestration | `Services/DeploymentService.cs` | Ordered pipeline; maps failures to `DeploymentResult`; health retries |
| Domain services | `Services/*.cs` | IIS, SQL, files (parallel + incremental overwrite), detection, config edits, hooks |
| Models | `Models/` | Requests, results, enums (`IisMode`, `DatabaseMode`, `DeploymentMode`, `DeploymentStep`, …) |
| Config / logging | `Configuration/`, `Logging/` | `App.config` settings; Serilog file sink; `SecretRedaction` for tests |
| Utilities | `Utilities/` | Validation, elevation, ports, recent paths, deploy history, Explorer folder picker |

### Pipeline (authoritative order)

Defined by `Models/DeploymentStep` and executed in `DeploymentService`:

1. Validate inputs  
2. Probe published folder (`web.config` / `*.runtimeconfig.json`)  
3. Check IIS (+ ASP.NET Core hosting prerequisite + port conflict for separate sites)  
4. Check SQL / validate `.bak` (restore mode only)  
5. Prepare target (stop app if overwriting)  
6. Copy files (backup-swap on overwrite; parallel copy; optional SizeAndTime bak reuse)  
7. Configure IIS (pool + application or site)  
8. Database restore (optional)  
9. Apply connection string (optional, deployed copy only)  
10. Recycle / start  
11. Health check (HTTP GET with retries / linear backoff)  
12. Post-deploy hooks (optional `.ps1` / HTTP URLs from App.config)  

Failure at any critical step stops the pipeline and returns a failed `DeploymentResult` with `FailedAt` set. Do not invent silent “best effort” success. Health-check and post-deploy-hook failures can still report success when their fail-deploy App.config flags are false (default).

### Overwrite copy (`FileService`)

1. Existing version folder is moved aside to `.deploying-bak` (backup-swap).  
2. Files are copied in parallel (`CopyMaxDegreeOfParallelism`, default 4).  
3. When `CopyCompareMode=SizeAndTime` and bak exists: if source and bak file have the same `Length` **and** the same `LastWriteTimeUtc` (`DateTime` equality — no content hash), the file is copied from bak; otherwise from source.  
4. Any other `CopyCompareMode` always copies from source.  
5. Cancellation is checked per file and every 50 files; UI shows “Cancelling after current file…”.  
6. On copy failure, bak is restored to the version folder.

SmokeTest covers SizeAndTime reuse when re-overwriting an unchanged tree.

### IIS models

- **ApplicationUnderSite** — IIS application under an existing parent site (e.g. `Default Web Site` → `/BiCore`). Parent bindings are never modified.  
- **SeparateSite** — dedicated site + HTTP binding on a chosen port; optional HTTPS binding + LocalMachine certificate. Port conflicts with other sites are checked first (HTTP and HTTPS).

Safety rules live in `Services/IisService.cs` (update in place; never delete sites/apps; reuse pools).

---

## 3. Solution layout

| Path | Role |
|------|------|
| `IntraDeploy.csproj` | Main WinExe (non-SDK style + **PackageReference**) |
| `IntraDeploy.slnx` | Opens the main project |
| `UI/MainForm*.cs` | Operator form: `MainForm.cs` + `Layout` / `Events` / `Deploy` partials + Designer |
| `SmokeTest/` | Non-elevated smoke harness → `IlProbe.exe` |
| `LiveIisTest/` | Elevated live IIS harness |
| `Tools/MakeIcon/` | PNG → multi-size ICO helper |

Helper projects reference `..\bin\Debug\IntraDeploy.exe`. **Always build the main project first.**

---

## 4. Prerequisites

- Windows  
- .NET Framework 4.8 targeting pack  
- MSBuild from Visual Studio 2022+ (or Build Tools)  
- NuGet restore (automatic via MSBuild `/t:Restore`)  
- For live IIS work: IIS + admin rights; for Core apps: ASP.NET Core Hosting Bundle  

---

## 5. Build and run

From the repo root (adjust the MSBuild path if needed):

```powershell
$msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"

& $msbuild IntraDeploy.csproj /t:Restore,Build /p:Configuration=Debug
& $msbuild IntraDeploy.csproj /t:Build /p:Configuration=Release
```

Outputs:

- Debug: `bin\Debug\IntraDeploy.exe` (+ `IntraDeploy.exe.config`)  
- Release: `bin\Release\IntraDeploy.exe`

Run elevated for IIS:

```powershell
Start-Process .\bin\Debug\IntraDeploy.exe -Verb RunAs
```

### Smoke tests

```powershell
& $msbuild SmokeTest\SmokeTest.csproj /t:Restore,Build /p:Configuration=Debug
.\SmokeTest\bin\Debug\IlProbe.exe
```

Expect `ALL SMOKE TESTS PASSED`.

### Live IIS tests

Requires administrator. Creates temporary IIS objects named for IntraDeploy tests and removes them afterward.

```powershell
& $msbuild LiveIisTest\LiveIisTest.csproj /t:Restore,Build /p:Configuration=Debug
Start-Process .\LiveIisTest\bin\Debug\net48\LiveIisTest.exe -Verb RunAs -Wait
```

### Package versions

Keep PackageReference versions on `SmokeTest` and `LiveIisTest` aligned with `IntraDeploy.csproj`. Mismatches produce **MSB3277** assembly conflicts when referencing the main EXE.

There is no separate ESLint/Roslyn analyzer project. Compiler **WarningLevel** is 4; a clean Debug build should report no CS warnings.

---

## 6. Configuration

`App.config` → `Configuration/AppConfig.cs`:

| Key | Meaning |
|-----|---------|
| `DeploymentRoot` | Base folder for `App\Version` deploys |
| `SqlServer` | Default SQL instance in the UI |
| `LogDirectory` | Serilog directory (`intradeploy-.log` daily rolling) |
| `SerilogMinimumLevel` | e.g. `Information`, `Debug` |
| `HealthCheckPath` | Default health URL path (UI can override) |
| `HealthCheckTimeoutSeconds` | Health GET timeout (default 15) |
| `HealthCheckFailureFailsDeploy` | When true, unhealthy fails the deploy |
| `MaxVersionsToKeep` | After success, prune oldest version folders under `App\` (never the live IIS path); default 5 |
| `PostDeployHooks` | Semicolon/comma-separated `.ps1` paths and/or http(s) URLs; empty = none |
| `PostDeployHookTimeoutSeconds` | Per-hook timeout (default 60) |
| `PostDeployHookFailureFailsDeploy` | When true, hook failure fails the deploy |
| `CopyMaxDegreeOfParallelism` | Parallel file-copy workers (1–16, default 4) |
| `CopyCompareMode` | `SizeAndTime` = reuse bak files with matching Length + LastWriteTimeUtc; else always copy from source |
| `HealthCheckRetries` | Health GET attempts including first (1–10, default 3) |
| `HealthCheckRetryDelayMilliseconds` | Base backoff between retries; wait is delay × attempt (0–60000, default 2000) |

Operator convenience paths (last folders) are stored in `%LOCALAPPDATA%\IntraDeploy\uistate.ini` via `RecentPathsStore` — paths only, never credentials. Deploy history is append-only `%LOCALAPPDATA%\IntraDeploy\history.jsonl` via `DeployHistoryStore` — no secrets.

LiveIisTest **TEST G** exercises HTTPS binding create when a `LocalMachine\My` cert with a private key exists; otherwise it prints `SKIP` (not a failure).

---

## 7. Coding conventions

- Keep IIS/SQL/filesystem side effects in `Services/`; UI only orchestrates and displays.  
- Use early returns; prefer explicit braces.  
- Log with Serilog; never log passwords or full connection strings.  
  - Do not bind `{ConnectionString}`, `{Password}`, or `{SqlPassword}` in log templates.  
  - Confirm / dry-run summaries describe intent only — never the raw connection string.  
  - Use `IntraDeploy.Logging.SecretRedaction` in SmokeTest when adding new summary surfaces.  
- Throw `DeploymentStepException` from services for expected pipeline failures.  
- Match existing naming: PascalCase types/methods, `_camelCase` private fields.  
- Prefer editing existing files over new abstractions.  
- XML docs on public APIs that encode safety rules are encouraged.

---

## 8. Contribution checklist

1. Understand the affected pipeline step and callers.  
2. Make the smallest change that fixes the root cause.  
3. `msbuild IntraDeploy.csproj /t:Build /p:Configuration=Debug` — zero errors/warnings.  
4. Run SmokeTest for logic/file/config changes.  
5. Run LiveIisTest (elevated) for IIS behavior changes.  
6. Update `documentation/MANUAL.md` if operator-visible behavior changes.  
7. Update `knowledge.md` if architecture, commands, or conventions change.

Do not commit secrets. Do not add network telemetry or new HTTP clients unless required for a feature.

---

## 9. Common extension points

| Goal | Start here |
|------|------------|
| New pipeline step | `DeploymentStep` + `DeploymentService` + UI progress text (`MainForm.Deploy.cs`) |
| New IIS behavior | `IisService` (+ LiveIisTest coverage) |
| Copy / overwrite behavior | `FileService` (+ SmokeTest SizeAndTime cases) |
| Detection rules | `ApplicationDetector` |
| Config file formats | `ConfigurationService` |
| Validation rules | `ValidationHelper` / `PreflightService` (+ SmokeTest assertions) |
| Secret leak checks | `Logging/SecretRedaction.cs` + SmokeTest |
| Branding / icon | `UI/Branding.cs`, `Images/`, `Tools/MakeIcon` |
| MainForm UI layout / events | `MainForm.Layout.cs` / `MainForm.Events.cs` / `MainForm.Deploy.cs` |
