# Windows maintenance shutdown

## Why NDI Tools could not update

The installed Main 0.8.12 process held the Tools-owned HX codec `avcodec-ndi-61.dll`. Environment Setup reproduced the signed NDI Tools installer failing its Restart Manager shutdown, then aborting with exit 5 and rolling back. Skipping application shutdown alone did not release the loaded DLL. Stopping the verified server task during the controlled repair allowed installation to complete.

Encoder previews load the installed NDI runtime lazily on the first real capture. Each capture destroys its temporary finder/receiver, but the service caches the runtime for subsequent previews. Closing a browser does not end the server process or dispose that cache. Display identity senders also use NDI while active. Holding runtime/codec libraries during this work is expected; it is not evidence of a second NDI configuration writer. Vendor updates need a maintenance interval with the consumer stopped.

The tray's explicit Exit action already calls `IHostApplicationLifetime.StopApplication()`. However, the old tray message loop had no handler forwarding Windows maintenance/session messages into that host stop path. A temporary file-lock fixture using the released 0.8.12 server assembly reproduces a Restart Manager shutdown timeout after 30 seconds. The updated service completes the same fixture normally.

## Change

`WindowsShutdownWindow` is a hidden top-level window owned by the existing tray thread. It acknowledges `WM_QUERYENDSESSION` without stopping, leaves the host running after a cancelled `WM_ENDSESSION`, and requests normal host shutdown after a confirmed `WM_ENDSESSION` or `WM_CLOSE`. These are the messages Windows Restart Manager uses for GUI applications. The host's existing stop/disposal path remains responsible for ending work and disposing services; this change does not unload a library while native calls are running or terminate a process.

A Windows maintenance stop suppresses even a pending tray Restart. It does not register automatic restart or change the scheduled task. After maintenance, the repair owner can restore the existing task using:

```powershell
Start-ScheduledTask -TaskPath '\' -TaskName 'NDI Job Configurator Service'
Invoke-RestMethod http://127.0.0.1:8091/api/health
```

Use the existing signed-in administrator context and verify health, executable identity and retained job state. The task's existing three failure retries at one-minute intervals still apply if a process fails. A successful normal exit is expected to leave it stopped. Coordinate the maintenance interval, finish any onboarding/configuration work first, and verify actual process exit and resource release before invoking a vendor installer. Closing the browser alone is insufficient; manual tray **Exit** remains available. A hung native call is not forcibly interrupted by this fix, so the installer must retain its blocker checks and must not assume shutdown succeeded.

There is no new HTTP endpoint, CLI stop command, credentials change, PC Agent contract, installation step or updater behavior. The Environment Setup preflight remains independent and can still tell the operator to close a blocking application. Its no-auto-close policy is unchanged.

## Isolated validation

```powershell
dotnet run --project tests/Lifecycle/Lifecycle.csproj --configuration Release
dotnet run --project tests/Regression/Regression.csproj --configuration Release
node --test tests/frontend.test.mjs
powershell.exe -NoProfile -File tests/installer.Tests.ps1
```

The native lifecycle suite launches only its own unelevated WinExe fixtures. They instantiate the actual tray/host service and lock newly created temporary test files. They do not launch the installed server, load NDI, discover devices, create scheduled tasks or install software. Tests cover cancelled session shutdown, `WM_CLOSE` with a pending restart, and actual `RmShutdown` without force flags. Each requires exit 0, completed host disposal, preserved sentinel state/configuration, and a released file; a fresh Restart Manager session also confirms the holder is gone. Failure cleanup can terminate only the exact child process created by the fixture.

For comparison against an existing release assembly, the test-only `ServerAssemblyPath` MSBuild property and `--restart-manager-only` argument select the original assembly without replacing installed files. Main 0.8.12 (`8ce9e12`) timed out in that check; the updated source passes all three native checks. Latest passing native evidence: `%TEMP%/ndi-lifecycle-2aab23d5c5344414a371322fbcf44557`.

All 23 server regression checks, 13 frontend checks, installer checks, scoped formatting and Git whitespace checks also passed.

This source/test change is included in the local 0.8.13 release candidate; it has not been published or installed. An elevated, vendor-installer acceptance run against a deployed new build remains a separately coordinated step; the fixture result does not claim that live validation has happened.

References: [Microsoft Restart Manager application guidelines](https://learn.microsoft.com/en-us/windows/win32/rstmgr/guidelines-for-applications), [WM_ENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession), and [NDI startup/shutdown guidance](https://docs.ndi.video/all/developing-with-ndi/sdk/startup-and-shutdown).
