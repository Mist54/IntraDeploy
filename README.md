# IntraDeploy

Windows desktop tool for deploying **published** ASP.NET Framework and ASP.NET Core application folders to **IIS**, with optional SQL Server `.bak` restore, versioned target folders, dry-run / partial modes, and a confirmation dialog that shows **live IIS state** before any change.

[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Features

- **Versioned deploys** under `DeploymentRoot\App\Version` with automatic prune (`MaxVersionsToKeep`)
- **IIS modes**: application under an existing site, or a dedicated site (HTTP + optional HTTPS)
- **Safe overwrite**: backup-swap; optional incremental reuse of unchanged files (`SizeAndTime`)
- **Parallel file copy** with responsive cancel
- **Optional SQL restore** from `.bak` (explicit overwrite confirm when the database exists)
- **Connection-string update** on the deployed copy only (never the publish source)
- **Validate / dry-run / partial modes** (`Full`, `Files + IIS`, `IIS only`, `Database only`, `Recycle only`)
- **Health check** with retries, post-deploy hooks (`.ps1` / HTTP), rollback, and local deploy history

---

## Requirements

| Requirement | Notes |
|-------------|--------|
| Windows | With .NET Framework **4.8** |
| Build tools | Visual Studio 2022+ or Build Tools (MSBuild) |
| IIS | Application hosting enabled |
| Elevation | **Run as administrator** for IIS management |
| ASP.NET Core apps | ASP.NET Core Hosting Bundle (AspNetCoreModuleV2) |
| SQL restore | Reachable SQL Server instance |

---

## Quick start

### Operators

1. Obtain or build `IntraDeploy.exe`.
2. Right-click → **Run as administrator**.
3. Select the published folder, IIS target, and database options.
4. Click **DEPLOY**, review the confirmation dialog, then confirm.

Full operator guide: [documentation/MANUAL.md](documentation/MANUAL.md)

### Developers

```powershell
msbuild IntraDeploy.csproj /t:Restore,Build /p:Configuration=Debug
Start-Process .\bin\Debug\IntraDeploy.exe -Verb RunAs
```

Smoke tests (no elevation required):

```powershell
msbuild SmokeTest\SmokeTest.csproj /t:Build /p:Configuration=Debug
.\SmokeTest\bin\Debug\IlProbe.exe
```

Architecture and contribution notes: [documentation/DEV.md](documentation/DEV.md)  
Project knowledge base: [knowledge.md](knowledge.md)

---

## Configuration

Defaults are in `App.config` (copied beside the EXE as `IntraDeploy.exe.config`). Edit the config next to the EXE on deployed machines; restart IntraDeploy after changes.

| Key | Purpose | Default |
|-----|---------|---------|
| `DeploymentRoot` | Root for versioned deploy folders | `C:\IntraDeploy\Applications` |
| `SqlServer` | Default SQL instance name | `localhost` |
| `LogDirectory` | Serilog rolling logs | `C:\IntraDeploy\Logs` |
| `SerilogMinimumLevel` | Log level | `Information` |
| `HealthCheckPath` | Default health URL path (form can override) | `/` |
| `HealthCheckTimeoutSeconds` | Health GET timeout (1–300) | `15` |
| `HealthCheckFailureFailsDeploy` | Unhealthy → fail deploy (else success + warning) | `false` |
| `HealthCheckRetries` | Health attempts including first (1–10) | `3` |
| `HealthCheckRetryDelayMilliseconds` | Base backoff; wait = delay × attempt | `2000` |
| `MaxVersionsToKeep` | Keep newest N version folders after success | `5` |
| `PostDeployHooks` | Semicolon-separated `.ps1` and/or http(s) URLs | *(empty)* |
| `PostDeployHookTimeoutSeconds` | Per-hook timeout (1–600) | `60` |
| `PostDeployHookFailureFailsDeploy` | Hook failure → fail deploy (else warning) | `false` |
| `CopyMaxDegreeOfParallelism` | Parallel file-copy workers (1–16) | `4` |
| `CopyCompareMode` | `SizeAndTime` reuses bak files with matching size + `LastWriteTimeUtc`; other values always copy from source | `SizeAndTime` |

Operator browse paths and deploy history live under `%LOCALAPPDATA%\IntraDeploy\` (no credentials).

---

## Safety model

- Existing IIS sites and applications are **updated in place**; they are never deleted.
- In “application under site” mode, **parent site bindings are not modified**.
- Database restore is **opt-in** and requires an explicit overwrite confirmation when the DB already exists.
- Overwriting a version folder uses a **backup-swap** so a failed copy can restore the previous tree.
- With `CopyCompareMode=SizeAndTime`, unchanged files (same length and last-write time) are reused from the backup — content is not hashed.
- Connection string updates apply only to the **deployed** copy.
- Dry-run performs pre-flight and reports the plan with **no writes**.

---

## Repository layout

```
IntraDeploy/
├── Configuration/     App.config readers
├── Services/          IIS, SQL, files, pipeline, hooks
├── UI/                WinForms + Krypton operator UI
├── Models/            Requests, results, enums
├── Logging/           Serilog bootstrap
├── Utilities/         Validation, elevation, history
├── SmokeTest/         Non-elevated logic harness
├── LiveIisTest/       Elevated IIS integration harness
├── documentation/     MANUAL.md (operators), DEV.md (developers)
└── knowledge.md       Project knowledge base
```

---

## License

Released under the [MIT License](LICENSE). Copyright © 2026 Sankalp M Shet.
