# IntraDeploy — Operator Manual

**Audience:** Operators and administrators who deploy applications with IntraDeploy.  
**Goal:** Install/run the tool, deploy an application safely, and troubleshoot common outcomes.  
**Type:** How-to guide (Diátaxis).

Related: [../README.md](../README.md) · [DEV.md](DEV.md)

---

## 1. What IntraDeploy does

IntraDeploy copies a **published application folder** (from Visual Studio “Publish as Folder”) into a versioned directory, configures **IIS**, optionally **restores a SQL Server backup**, and can update the **connection string** in the deployed copy. Before any change, it shows a confirmation screen with **what IIS currently has** and **what will happen**.

It does **not**:

- Build or compile your application (publish first in Visual Studio).  
- Install IIS or the ASP.NET Core Hosting Bundle for you.  
- Delete existing IIS sites or applications.  
- Modify the published source folder when updating connection strings.

---

## 2. Before you start

### Machine requirements

- Windows with .NET Framework 4.8  
- IIS installed with application support  
- **Run as administrator** (required for IIS)  
- For ASP.NET Core apps: **ASP.NET Core Hosting Bundle** (AspNetCoreModuleV2) installed  
- SQL Server reachable if you choose restore-from-`.bak`

### What you need ready

1. A published folder that contains either:  
   - `web.config` (ASP.NET Framework), or  
   - `*.runtimeconfig.json` (modern .NET / ASP.NET Core), often also with a small `web.config` for IIS.  
2. Application name and version (e.g. `BiCore` / `2.5.0`).  
3. IIS target: either an existing parent site + application path (e.g. `/BiCore`), or a new site name + port.  
4. Optional: `.bak` file and database name if restoring.

### Default folders

Unless changed in `IntraDeploy.exe.config`:

| Setting | Default |
|---------|---------|
| Deployment root | `C:\IntraDeploy\Applications` |
| Logs | `C:\IntraDeploy\Logs` |
| SQL Server | `localhost` |

Deployed files land in:

`{DeploymentRoot}\{ApplicationName}\{Version}`

Example: `C:\IntraDeploy\Applications\BiCore\2.5.0`

---

## 3. Run IntraDeploy

1. Locate `IntraDeploy.exe`.  
2. Right-click → **Run as administrator**.  
3. If prompted that elevation is missing, choose **Yes** to restart elevated.  
4. Only one IntraDeploy window can run at a time.

---

## 4. Fill in the form

### Application

| Field | What to enter |
|-------|----------------|
| Application | Logical name (e.g. `BiCore`). Used for folders and IIS defaults. |
| Version | Version folder name (e.g. `2.5.0`). No trailing dots/spaces. |
| Published Application Folder | Full path to the publish output. Use **Browse...**. |

After you select a folder, IntraDeploy shows the detected type (Framework vs ASP.NET Core).

| Field | What to enter |
|-------|----------------|
| **Deploy mode** | **Full deploy** (default), **Files + IIS**, **IIS only**, **Database only**, or **Recycle only**. Partial modes skip irrelevant pipeline steps. |
| **Dry-run** | When checked, Deploy runs pre-flight then reports the planned target folder, IIS action, database, and config — then **stops with no writes**. |

### Database

| Option | Behavior |
|--------|----------|
| **Use existing database** | No SQL operations. Safest default. |
| **Restore database from .bak** | Validates the backup, then restores during deploy. |

Also set **Database Name**, **SQL Server**, and (in restore mode) the **Backup File**.

#### SQL authentication

| Option | Behavior |
|--------|----------|
| **Windows authentication** (default) | Connects as the Windows account running IntraDeploy. |
| **SQL Server login** | Prompts for user/password for **this session only**. Credentials are never written to `uistate.ini`, RecentPaths, or logs. |

Use **Set login...** to enter or change the SQL login while SQL authentication is selected. Switching back to Windows clears the in-memory credentials.

#### Connection string update (optional)

| Control | Behavior |
|---------|----------|
| **Update connection string in deployed config** | When checked, IntraDeploy updates the **first** connection string in the deployed `web.config` / `appsettings.json` only. The published source folder is never modified. |
| **Connection string** | Full string to write. Prefer **Build...** from SQL Server + Database Name (+ session SQL auth if selected). |
| Confirm dialog | Shows whether the connection string will be updated (never displays the string itself). |

If update is enabled with an empty string, Deploy/Validate is blocked until you enter or build one.

If the database already exists, the confirmation dialog requires an explicit **overwrite** checkbox before restore proceeds.

### IIS

**Application under existing IIS Site** (typical):

- Choose the **Parent IIS Site** (e.g. Default Web Site).  
- Set **Application Path** (e.g. `/BiCore` → `http://localhost/BiCore`).  
- Path must start with `/`, must not be only `/`, and must not end with `/`.

**Separate IIS Site**:

- Set **IIS Site Name** and **Port** (HTTP, e.g. `8099` → `http://localhost:8099`).  
- Optional **HTTPS port** + **HTTPS certificate** (from `LocalMachine\My`, private key required). Leave HTTPS blank for HTTP-only (default).  
- IntraDeploy checks for binding conflicts with other sites first (HTTP and HTTPS).  
- A **soft warning** appears when the TCP port already has a local listener (`PortHelper`); that does not block deploy by itself.
- Validate / pre-flight confirms the selected certificate exists before any IIS write.

**Shared fields:**

- **Application Pool** — created if missing; if it already exists it is reused.  
- **Deployment Location** — root folder for versioned copies.

---

## 5. Validate (pre-flight, no writes)

Use **Validate** before Deploy when you want a dry check of the current form:

1. Click **Validate**.  
2. IntraDeploy runs read-only checks:  
   - Administrator elevation  
   - Form input / published folder probe  
   - IIS availability  
   - ASP.NET Core Hosting Bundle (when the app needs AspNetCoreModuleV2)  
   - Existing application pool CLR mismatch (warning tagged **CONTINUE REQUIRED** — Deploy will ask before proceeding)  
   - Separate-site IIS binding conflict (`GetPortConflict`)  
   - Soft TCP port-in-use warning (`PortHelper`)  
   - If restore mode: SQL `TestConnection` + backup header validation (`ValidateBackup`) — **no restore**  
   - Connection-string intent (enabled vs empty)  
3. Review the result dialog (`[INFO]` / `[WARNING]` / `[ERROR]`). Nothing is copied and IIS/SQL are not changed.  
4. Fix errors, then **DEPLOY** when ready.

Validate does **not** replace the confirmation dialog on Deploy.

---

## 6. Deploy

1. Choose **Deploy mode** (Full or a partial mode) and optionally tick **Dry-run**.  
2. Click **DEPLOY**.  
3. If the named application pool already exists with the wrong CLR for this app type (modes that configure IIS, not dry-run), IntraDeploy shows a **Continue** prompt (it never changes an existing pool). Choose **No** to cancel, or **Yes** only if you accept that risk.  
4. Review the **Confirm deployment** dialog carefully:  
   - Deploy mode and whether this is a dry-run  
   - **Steps that will run** (or dry-run plan only)  
   - Live IIS action (create vs update in place) when the mode touches IIS  
   - Current physical path / pool if updating  
   - SQL auth mode (Windows vs SQL login name — never the password)  
   - Connection string action (update vs leave unchanged vs skipped by mode)  
   - Health check path / timeout / fail-deploy policy  
   - Database and folder overwrite warnings  
5. Tick the database overwrite checkbox if required and you intend to replace that database.  
6. Confirm to start (**DEPLOY** or **DRY-RUN**), or cancel to abort with no changes.  
7. Watch progress in the status area. Use **Cancel** to stop; during file copy the status shows **Cancelling after current file…** (cancel is checked per file and every 50 files).  
8. Read the result dialog (success, dry-run plan, failed step, health check summary, any rollback note).

### Deploy modes (what runs)

| Mode | Runs |
|------|------|
| **Full deploy** | Copy → IIS → optional DB restore → config → recycle → health |
| **Files + IIS** | Copy → IIS → config → recycle → health (**skips database**) |
| **IIS only** | IIS pool/target ensure → recycle → health (no file copy) |
| **Database only** | Restore from `.bak` only (requires restore option selected) |
| **Recycle only** | Start/recycle IIS target → health |

**Dry-run** always stops after checks and the plan report — no copy, IIS write, SQL restore, or config write.

### After a successful deploy

- Open the health-check URL shown in the result (if provided).  
- Confirm the site/app in IIS Manager points at the new physical path.  
- Check logs under the configured log directory if something looks wrong.  
- Oldest version folders beyond `MaxVersionsToKeep` are pruned automatically; the live IIS path is never deleted.  
- Each run is appended to `%LOCALAPPDATA%\IntraDeploy\history.jsonl` (no secrets).

### Rollback to a previous version

1. Fill **Application name**, **Deployment Location**, and the IIS target (site/app or separate site + pool) to match what is live.  
2. Click **Rollback...** (or **Rollback...** on the result dialog after a deploy).  
3. Pick a prior version folder (the current IIS path is marked).  
4. Confirm — IntraDeploy retargets the IIS physical path and recycles. The previous folder stays on disk.

### Recent deployments

Click **Recent...** to browse local history. **Fill form** loads app/version/root; **Open folder** / **Open URL** use the stored paths when present.

### Health check behavior

- Path comes from the form (**Health check path**) or `HealthCheckPath` in config (default `/`).  
- Timeout from `HealthCheckTimeoutSeconds` (default 15).  
- Retries: `HealthCheckRetries` (default 3) with backoff `HealthCheckRetryDelayMilliseconds` × attempt (default 2000 ms).  
- By default (`HealthCheckFailureFailsDeploy=false`), an unhealthy response still counts as a **successful** deploy with a health warning.  
- Set `HealthCheckFailureFailsDeploy=true` so an unhealthy / unreachable check marks the deploy **failed** at Health check.

### Post-deploy hooks

After the health check, IntraDeploy can run optional hooks from `PostDeployHooks` in config (semicolon- or comma-separated):

- Local **`.ps1`** scripts (PowerShell `-File`), or  
- **`http://` / `https://`** URLs (HTTP GET).

Exit codes / non-2xx responses are recorded on the result. By default (`PostDeployHookFailureFailsDeploy=false`) a hook failure is a **warning** and the deploy still succeeds. Set `PostDeployHookFailureFailsDeploy=true` to fail the deploy at Post-deploy hook. Timeout per hook: `PostDeployHookTimeoutSeconds` (default 60).

---

## 7. Safety behavior (what to expect)

| Situation | What IntraDeploy does |
|-----------|------------------------|
| Application/site already exists | Updates in place; does not delete it |
| Same version folder already exists | Asks to overwrite; moves old folder aside (`.deploying-bak`), restores it if copy fails. With default `CopyCompareMode=SizeAndTime`, files with the **same size and LastWriteTimeUtc** as the publish folder are reused from that backup (content is not hashed). Change `CopyCompareMode` to anything else to always copy from the publish folder. Copy uses up to `CopyMaxDegreeOfParallelism` workers (default 4). |
| Different version | New folder under the same app name; after success, oldest beyond `MaxVersionsToKeep` are pruned (live path protected) |
| Rollback | IIS physical path retarget + recycle only; current folder not deleted |
| Deploy history | Append-only `%LOCALAPPDATA%\IntraDeploy\history.jsonl` (app, version, target, URL, success, step, timestamp, log hint) — no secrets |
| Use existing database | Does not touch SQL Server |
| Restore when DB exists | Requires explicit confirmation |
| Dry-run | Pre-flight + plan report only; no file/IIS/SQL/config writes |
| Partial deploy mode | Skips steps not included in the selected mode (see Deploy modes) |
| SQL Server login | Session-only credentials; never persisted to disk |
| Connection string update | Edits deployed copy only; source publish folder untouched; value never logged |
| Validate | Read-only checks; no file/IIS/SQL writes |
| Existing pool CLR mismatch | Never mutates the pool; Validate warns; Deploy requires explicit Continue |
| Not elevated | Warns; IIS steps will fail until restarted as admin |
| ASP.NET Core without hosting bundle | Stops **before** changing IIS |
| Separate-site port in use (TCP) | Soft warning on form / Validate; hard fail only on IIS binding conflict |
| Separate-site HTTPS | Optional; cert must exist in LocalMachine\My; HTTP binding always kept |
| Post-deploy hook failed | Success + warning by default; fails deploy when `PostDeployHookFailureFailsDeploy=true` |
| Health check unhealthy | Success + warning by default; fails deploy when `HealthCheckFailureFailsDeploy=true` |

---

## 8. Troubleshooting

| Symptom | What to try |
|---------|-------------|
| “IntraDeploy is already running” | Switch to the existing window or end the other process |
| IIS operations fail / access denied | Run as administrator; confirm IIS is installed |
| Folder not accepted as published app | Ensure `web.config` or `*.runtimeconfig.json` exists at the folder root |
| ASP.NET Core deploy blocked | Install the .NET / ASP.NET Core Hosting Bundle, then retry |
| Port / binding conflict | Choose another port or free the conflicting site binding |
| Soft port warning but Validate OK | Another process listens on the port; IIS binding may still be free — or free the port |
| SQL connection failed | Check instance name, firewall, and Windows/SQL auth; confirm the service is up; re-enter session SQL login if used |
| Restore refused | Confirm `.bak` path; for existing DBs tick overwrite on confirmation |
| Connection string not applied | Enable the update checkbox, supply a non-empty string (or Build...), confirm on the dialog |
| Health check failed but deploy “succeeded” | Default policy; check pool/bindings/app errors — or set `HealthCheckFailureFailsDeploy=true` |
| Post-deploy hook failed but deploy “succeeded” | Default policy; check script/URL — or set `PostDeployHookFailureFailsDeploy=true` |
| HTTPS certificate not found | Pick a LocalMachine\My cert with a private key, or clear HTTPS port for HTTP only |
| Deploy asks to Continue for pool CLR | Pool exists with wrong CLR; pick another pool name, fix CLR in IIS Manager, or Continue knowingly |
| Need detailed history | Open today’s file under `C:\IntraDeploy\Logs` (or your `LogDirectory`): `intradeploy-YYYYMMDD.log` |
| Want past deploys in the UI | **Recent...** reads `%LOCALAPPDATA%\IntraDeploy\history.jsonl` |
| Rollback has no versions | Confirm `DeploymentRoot\AppName\` has sibling version folders from prior deploys |
| Overwrite kept an old file unexpectedly | Default `SizeAndTime` reuse: if size and LastWriteTimeUtc both matched bak, content was not re-read. Touch/save the publish file, or set `CopyCompareMode` to something other than `SizeAndTime` |

---

## 9. Changing defaults

Edit `IntraDeploy.exe.config` next to the EXE (same keys as `App.config` in source):

```xml
<appSettings>
  <add key="DeploymentRoot" value="C:\IntraDeploy\Applications" />
  <add key="SqlServer" value="localhost" />
  <add key="LogDirectory" value="C:\IntraDeploy\Logs" />
  <add key="SerilogMinimumLevel" value="Information" />
  <add key="HealthCheckPath" value="/" />
  <add key="HealthCheckTimeoutSeconds" value="15" />
  <add key="HealthCheckFailureFailsDeploy" value="false" />
  <add key="HealthCheckRetries" value="3" />
  <add key="HealthCheckRetryDelayMilliseconds" value="2000" />
  <add key="MaxVersionsToKeep" value="5" />
  <add key="PostDeployHooks" value="" />
  <add key="PostDeployHookTimeoutSeconds" value="60" />
  <add key="PostDeployHookFailureFailsDeploy" value="false" />
  <add key="CopyMaxDegreeOfParallelism" value="4" />
  <add key="CopyCompareMode" value="SizeAndTime" />
</appSettings>
```

| Key | Meaning |
|-----|---------|
| `HealthCheckPath` | Default path appended to the site/app URL (form can override) |
| `HealthCheckTimeoutSeconds` | HTTP timeout for the health GET (1–300) |
| `HealthCheckFailureFailsDeploy` | `true` = unhealthy marks deploy failed; `false` = success + warning |
| `HealthCheckRetries` | Number of health attempts including the first (1–10; default 3) |
| `HealthCheckRetryDelayMilliseconds` | Base delay between retries; wait = delay × attempt (default 2000) |
| `MaxVersionsToKeep` | After a successful deploy, keep this many newest version folders under `App\`; never delete the live IIS path |
| `PostDeployHooks` | Semicolon/comma-separated `.ps1` paths and/or `http(s)` URLs; empty = none |
| `PostDeployHookTimeoutSeconds` | Per-hook timeout (1–600; default 60) |
| `PostDeployHookFailureFailsDeploy` | `true` = hook failure fails deploy; `false` = success + warning |
| `CopyMaxDegreeOfParallelism` | Parallel file-copy workers (1–16; default 4) |
| `CopyCompareMode` | `SizeAndTime` = reuse bak files with matching size + LastWriteTimeUtc; any other value = always copy from publish folder |

Restart IntraDeploy after saving. Remembered browse paths live under `%LOCALAPPDATA%\IntraDeploy\` (`uistate.ini`); deploy history is `history.jsonl` in the same folder. SQL passwords are **never** stored there.
