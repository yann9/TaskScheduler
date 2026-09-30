<#
.SYNOPSIS
  Publish a distributable package: main app + NSIS installer, into dist\.
  Target: .NET Framework 4.8 (framework-dependent). End users need only the
  OS-built-in .NET Framework 4.8 (Win10 1809+ / Win11 ship it), so the package
  stays small and installs with zero runtime download.
  End users double-click dist\Setup.exe; uninstall from Settings > Apps.

.NOTES
  IMPORTANT: keep this file ASCII-only.
  Windows PowerShell 5.1 reads a BOM-less file as ANSI/GBK, so non-ASCII
  characters corrupt parsing and the script fails with bogus syntax errors.

  Installer is built by NSIS (makensis) -- see TaskScheduler.nsi in the repo
  root. NSIS is the only supported installer.

  Design: zero recursive delete/move of dist.
  - Publish the app straight into dist with -o (in-place overwrite).
  - Let makensis write dist\Setup.exe directly (OutFile is set in the .nsi).
  - Every cleanup step is wrapped in try/catch and cannot break the result.
  Cost: dist may keep orphan files from older versions. To get a pristine dist,
  delete it manually and run this script again.
#>
$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $MyInvocation.MyCommand.Definition
$mainProj = Join-Path $root "TaskScheduler\TaskScheduler.csproj"
$dist     = Join-Path $root "dist"
$nsiFile  = Join-Path $root "TaskScheduler.nsi"

# Do not let MSBuild reuse nodes: they keep file handles on build output.
$env:MSBUILDDISABLENODEREUSE = "1"

# ---------------------------------------------------------------- call helper
# Windows PowerShell 5.1 TRAP: any line an external program writes to STDERR is
# wrapped into a NativeCommandError record, and with $ErrorActionPreference =
# 'Stop' that becomes a TERMINATING error. The script then dies mid-way even
# though the tool actually succeeded. This is exactly how ISCC killed a run:
# its "Warning: PrivilegesRequired ... UsedUserAreasWarning" goes to stderr, so
# the script aborted at the ISCC line (exit code 0, no dist\Setup.exe summary).
#   => Never invoke an .exe directly in this script; always go through Invoke-Exe,
#      which relaxes the preference for the duration of the call and reports the
#      exit code separately. Success/failure is decided by the exit code alone.
$script:LastExeExit = 0
function Invoke-Exe {
    param(
        [Parameter(Mandatory = $true)][string]   $Path,
        [string[]] $ExeArgs = @()
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # 2>&1 turns the child's stderr into ErrorRecord objects inside the success
        # stream. Re-emit them as plain warnings so the text still reaches the user
        # but without PowerShell's "CategoryInfo / NativeCommandError" framing --
        # that framing makes a harmless warning look like a fatal crash.
        & $Path @ExeArgs 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                Write-Warning $_.Exception.Message
            } else {
                $_
            }
        }
        $script:LastExeExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
}

# Prefer the locally installed .NET 8 SDK (has full publish support).
$dotnet = "dotnet"
$candidate = "C:\Users\zhouy\.dotnet8\dotnet.exe"
if (Test-Path $candidate) { $dotnet = $candidate }

# net48 is framework-dependent: no -r / --self-contained / PublishSingleFile.
$commonArgs = @(
    "-c", "Release",
    "-p:DebugType=none",
    "-p:UseSharedCompilation=false",
    "--source", "https://api.nuget.org/v3/index.json"
)

# ------------------------------------------------- 0) cross-file contract check
# A few magic strings and ids are shared across the language boundary between the
# installer (.nsi) and the app (C#). Nothing type-checks them, and a mismatch
# fails SILENTLY at runtime. Worst case: if ExitMessage and ExitRequestMessageId
# drift apart, the installer's "please exit" broadcast reaches nobody, upgrades
# fall back to taskkill, and the in-memory schedule is lost. Cheap to check here,
# expensive to debug in the field.
function Get-ContractValue {
    param(
        [Parameter(Mandatory = $true)][string] $File,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $What
    )
    if (-not (Test-Path $File)) { throw "Contract check: missing file $File (needed for $What)" }
    $text = Get-Content -LiteralPath $File -Raw -Encoding UTF8
    $m = [regex]::Match($text, $Pattern)
    if (-not $m.Success) { throw "Contract check: cannot find $What in $File" }
    return $m.Groups[1].Value
}

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][string] $What,
        [Parameter(Mandatory = $true)][string] $NsiValue,
        [Parameter(Mandatory = $true)][string] $CsValue,
        [bool] $AsHex = $false
    )
    if ($AsHex) {
        $same = ([Convert]::ToInt32($NsiValue, 16) -eq [Convert]::ToInt32($CsValue, 16))
    } else {
        $same = ($NsiValue -ceq $CsValue)
    }
    if (-not $same) {
        throw ("Contract check FAILED for '$What': installer has [$NsiValue] but the app has" +
               " [$CsValue]. Fix whichever side is wrong, then re-run.")
    }
    Write-Output ("  contract ok: {0} = {1}" -f $What, $NsiValue)
}

$autoStartCs = Join-Path $root "TaskScheduler\Engine\AutoStartRegistration.cs"
$nativeCs    = Join-Path $root "TaskScheduler\Native\NativeMethods.cs"
$serviceCs   = Join-Path $root "TaskScheduler\Service\ServiceControl.cs"
$appCs       = Join-Path $root "TaskScheduler\App.xaml.cs"

Write-Output "Cross-file contracts (.nsi <-> C#):"
Assert-Contract -What "exit broadcast message id" -AsHex $true `
    -NsiValue (Get-ContractValue $nsiFile 'TS_EXIT_MESSAGE\s+0x([0-9A-Fa-f]+)' "nsi TS_EXIT_MESSAGE") `
    -CsValue  (Get-ContractValue $nativeCs 'ExitRequestMessageId\s*=\s*0x([0-9A-Fa-f]+)' "cs ExitRequestMessageId")
# The installer opens this event to ask a running instance to exit gracefully, and
# uses "can I open it" as a probe for "is a UI instance running at all". If the two
# names drift apart the probe silently reports "nothing running" and every upgrade
# degrades to a force-kill (losing the in-memory NextRunTime).
Assert-Contract -What "graceful exit event name" `
    -NsiValue (Get-ContractValue $nsiFile 'TS_EXIT_EVENT\s+"([^"]+)"' "nsi TS_EXIT_EVENT") `
    -CsValue  (Get-ContractValue $appCs 'ExitEventName\s*=\s*@"([^"]+)"' "cs ExitEventName")
# The installer clears the service by deleting this registry key (see
# StopOldService in the .nsi) -- it is the only reliable way to tell whether the
# old service is really gone. A wrong name means it waits its full timeout every time.
Assert-Contract -What "windows service name" `
    -NsiValue (Get-ContractValue $nsiFile 'TS_SERVICE_NAME\s+"([^"]+)"' "nsi TS_SERVICE_NAME") `
    -CsValue  (Get-ContractValue $serviceCs 'public const string ServiceName\s*=\s*"([^"]+)"' "cs ServiceName")
Assert-Contract -What "autostart registry value name" `
    -NsiValue (Get-ContractValue $nsiFile 'TS_AUTOSTART_VALUE\s+"([^"]+)"' "nsi TS_AUTOSTART_VALUE") `
    -CsValue  (Get-ContractValue $autoStartCs 'ValueName\s*=\s*"([^"]+)"' "cs ValueName")
Assert-Contract -What "autostart minimized argument" `
    -NsiValue (Get-ContractValue $nsiFile 'TS_MINIMIZED_ARG\s+"([^"]+)"' "nsi TS_MINIMIZED_ARG") `
    -CsValue  (Get-ContractValue $autoStartCs 'MinimizedArgument\s*=\s*"([^"]+)"' "cs MinimizedArgument")

# --------------------------------------------------------------- stop running
# Only instances launched FROM dist are ever touched.
#
# Two traps this avoids:
#  1) Do NOT stop every process named TaskScheduler.exe. The installed copy under
#     Program Files and the Windows SERVICE running from there hold none of the
#     files we are about to overwrite. Killing them interrupts the user's running
#     schedule and makes the SCM log an unclean service termination. The only
#     instance that can block "dotnet publish -o dist" is one started from dist.
#  2) Do NOT drive the exit by running "dist\TaskScheduler.exe --exit". The exe in
#     dist may be an older build that does not know the switch; it would treat it
#     as an unknown argument and just show the UI again. Open the named event
#     directly instead -- no child process, no version dependency.
#
# Graceful exit matters: the app's OnExit runs Stop() + Save(), persisting the
# in-memory NextRunTime. A bare Stop-Process skips that, so the next start could
# treat already-executed tasks as "missed" and run them a second time.
$procName     = "TaskScheduler"
$exeInDist    = Join-Path $dist "TaskScheduler.exe"
$exitEvent    = "Local\TaskSchedulerUI_Exit"   # must match App.ExitEventName

function Get-DistInstance {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        if ([string]::IsNullOrEmpty($path)) { $false }
        else { $path.StartsWith($dist, [System.StringComparison]::OrdinalIgnoreCase) }
    }
}

$running = @(Get-DistInstance)
if ($running.Count -gt 0) {
    Write-Output ("Stopping {0} instance(s) started from dist: {1}" -f $running.Count, (($running | ForEach-Object { $_.Id }) -join ', '))

    $signalled = $false
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting($exitEvent)
        [void]$ev.Set()
        $ev.Dispose()
        $signalled = $true
        Write-Output "  sent graceful-exit signal via named event."
    } catch {
        Write-Warning "Named event '$exitEvent' not found (instance may predate it). Will force-kill."
    }

    if ($signalled) {
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and @(Get-DistInstance).Count -gt 0) {
            Start-Sleep -Milliseconds 300
        }
    }
    Get-DistInstance | Stop-Process -Force -ErrorAction SilentlyContinue
} else {
    Write-Output "No instance running from dist; nothing to stop."
}

# --------------------------------------------------------------- 1) main app
# Read TS_VERSION from the .nsi (CI patches it from the tag BEFORE this script
# runs; a local build just gets the static default) and inject it into the
# managed assemblies via -p:Version. Without this the exe keeps the static
# 1.0.0 from the .csproj while the installer reports the real version -- and
# the About window showed "1.0.0" for every release. Same trap as the contract
# checks above: nothing type-checks this cross-file agreement, so read it from
# the one place CI already patched and fail loudly if the define is missing.
$appVersion   = Get-ContractValue $nsiFile '!define\s+TS_VERSION\s+"([^"]+)"' "nsi TS_VERSION"
$versionParts = @($appVersion -split '[.\-+]' | Where-Object { $_ -match '^\d+$' })
while ($versionParts.Count -lt 4) { $versionParts += '0' }
$appVersion4  = ($versionParts[0..3] -join '.')
Write-Output "App version (nsi TS_VERSION): $appVersion (file version: $appVersion4)"

Invoke-Exe -Path $dotnet -ExeArgs (@("publish", $mainProj) + $commonArgs + @(
    "-o", $dist,
    "-p:Version=$appVersion",
    "-p:FileVersion=$appVersion4",
    "-p:AssemblyVersion=$appVersion4"
))
if ($script:LastExeExit -ne 0) { throw "Main publish failed (exit $script:LastExeExit)" }

# --------------------------------------------------- 2) installer (NSIS)
function Find-Makensis {
    # Order: machine-wide installs first, then PATH.
    # winget / the official installer both land under "Program Files (x86)\NSIS"
    # (NSIS is a 32-bit application even on 64-bit Windows).
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
        (Join-Path $env:ProgramFiles "NSIS\makensis.exe")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$makensis = Find-Makensis
if (-not $makensis) {
    $stale = Join-Path $dist "Setup.exe"
    $staleNote = if (Test-Path $stale) {
        "`nWARNING: $stale still exists but it will NOT be rebuilt" +
        "`n         (an older artifact). Do not ship it -- install NSIS" +
        "`n         and re-run this script so a fresh Setup.exe replaces it."
    } else { "" }
    throw ("NSIS not found.`n" +
           "Install it with:  winget install --id NSIS.NSIS -e`n" +
           "Or grab it from https://nsis.sourceforge.io , then re-run this script." +
           $staleNote)
}
if (-not (Test-Path $nsiFile)) { throw "Missing script: $nsiFile" }
Write-Output "Using makensis: $makensis"

# makensis writes dist\Setup.exe (OutFile is set in the .nsi).
# It may print warnings to stderr (e.g. the harmless "Insecure filename
# Setup.exe" for the /x-excluded old artifact) -- see Invoke-Exe for why this
# must NOT be a bare '& $makensis'. Only the exit code decides success.
Invoke-Exe -Path $makensis -ExeArgs @($nsiFile)
if ($script:LastExeExit -ne 0) { throw "makensis failed with exit code $script:LastExeExit" }

$setupExe = Join-Path $dist "Setup.exe"
if (-not (Test-Path $setupExe)) { throw "makensis reported success but $setupExe is missing" }

# ------------------------------------------------------- 3) release + report
Invoke-Exe -Path $dotnet -ExeArgs @("build-server", "shutdown") | Out-Null
foreach ($d in @((Join-Path $root "publish_main"), (Join-Path $root "publish_setup"))) {
    try {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction Stop }
    } catch {
        Write-Warning "Leftover dir not removed (safe to ignore): $d"
    }
}

$setupSize = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Output ""
Write-Output "Dist package ready: $dist"
Write-Output "  Main app : TaskScheduler.exe (+ dependency DLLs, framework-dependent on .NET 4.8)"
Write-Output "  Installer: Setup.exe ($setupSize MB, built by NSIS)"
Write-Output "Silent   : Setup.exe /S"
Write-Output "Uninstall: Settings > Apps, or uninstall.exe inside the install folder."
