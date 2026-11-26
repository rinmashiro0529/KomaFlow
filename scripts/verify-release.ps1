[CmdletBinding()]
param(
    [string]$DistDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DistDir)) {
    $DistDir = Join-Path $RepoRoot "dist"
}

$GuiCsprojPath = Join-Path $RepoRoot "src\MangaGui\MangaGui.csproj"
[xml]$GuiCsproj = Get-Content -Path $GuiCsprojPath -Raw -Encoding UTF8
$Version = ($GuiCsproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$SetupExe = Join-Path $DistDir "KomaFlow-v$Version-win-x64-setup.exe"
$PortableZip = Join-Path $DistDir "KomaFlow-v$Version-win-x64-portable.zip"
$SumsFile = Join-Path $DistDir "SHA256SUMS.txt"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "ASSERTION FAILED: $Message"
    }
    Write-Host "  [PASS] $Message" -ForegroundColor Green
}

function Verify-Manifest {
    param([string]$AppDir)
    $manifestPath = Join-Path $AppDir "build_manifest.json"
    Assert-True (Test-Path $manifestPath) "build_manifest.json exists in $AppDir"
    $manifest = Get-Content -Path $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($manifest.version -eq $Version) "Manifest version matches $Version"
    Assert-True ($manifest.files.Count -ge 11) "Manifest tracks >= 11 files ($($manifest.files.Count))"
    foreach ($f in $manifest.files) {
        $p = Join-Path $AppDir ($f.path -replace "/", "\")
        if (-not (Test-Path $p)) { throw "Missing manifest file: $p" }
        $actual = (Get-FileHash -Path $p -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $f.sha256) {
            throw "SHA256 mismatch for $($f.path): expected $($f.sha256), got $actual"
        }
    }
    Write-Host "  [PASS] All $($manifest.files.Count) manifest SHA256 hashes verified" -ForegroundColor Green
}

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class KomaFlowWin32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_CLOSE = 0x0010;

    public static IntPtr FindWpfMainWindow(uint targetPid, out string title) {
        IntPtr found = IntPtr.Zero;
        string foundTitle = "";
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid && IsWindowVisible(h)) {
                var c = new StringBuilder(256);
                GetClassName(h, c, 256);
                if (c.ToString().StartsWith("HwndWrapper[MangaGui", StringComparison.Ordinal)) {
                    var t = new StringBuilder(256);
                    GetWindowText(h, t, 256);
                    if (t.Length > 0) {
                        found = h;
                        foundTitle = t.ToString();
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        title = foundTitle;
        return found;
    }
}
"@

function Invoke-IsolatedSmoke {
    param([string]$AppDir, [string]$IsolatedAppData, [string]$Label)
    New-Item -ItemType Directory -Path $IsolatedAppData -Force | Out-Null

    # 1. CLI version + help + doctor under isolated APPDATA
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $AppDir "MangaCli.exe"
    $psi.Arguments = "version"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["APPDATA"] = $IsolatedAppData
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit(15000) | Out-Null
    Assert-True ($proc.ExitCode -eq 0) "[$Label] MangaCli version exits 0 (stderr='$($stderr.Trim())')"
    $verObj = ($stdout -split "`r?`n" | Where-Object { $_.Trim() -ne "" } | Select-Object -Last 1) | ConvertFrom-Json
    Assert-True ($verObj.ok -eq $true -and $verObj.version -eq $Version) "[$Label] MangaCli version reports ok=true and version=$Version"

    # 2. GUI real launch + real WPF window check + WM_CLOSE graceful exit under isolated APPDATA
    $guiPsi = New-Object System.Diagnostics.ProcessStartInfo
    $guiPsi.FileName = Join-Path $AppDir "MangaGui.exe"
    $guiPsi.WorkingDirectory = $AppDir
    $guiPsi.UseShellExecute = $false
    $guiPsi.EnvironmentVariables["APPDATA"] = $IsolatedAppData
    $guiProc = [System.Diagnostics.Process]::Start($guiPsi)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $mainHwnd = [IntPtr]::Zero
        $mainTitle = ""
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($guiProc.HasExited) {
                throw "[$Label] MangaGui exited prematurely with code $($guiProc.ExitCode)"
            }
            $t = ""
            $h = [KomaFlowWin32]::FindWpfMainWindow([uint32]$guiProc.Id, [ref]$t)
            if ($h -ne [IntPtr]::Zero) {
                $mainHwnd = $h
                $mainTitle = $t
                break
            }
            Start-Sleep -Milliseconds 250
        }
        Assert-True ($mainHwnd -ne [IntPtr]::Zero) "[$Label] MangaGui created visible WPF main window (title='$mainTitle')"

        # Give WPF a brief moment to finish initial load, then send WM_CLOSE to the real WPF HWND
        Start-Sleep -Milliseconds 800
        $posted = [KomaFlowWin32]::PostMessage($mainHwnd, [KomaFlowWin32]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
        Assert-True $posted "[$Label] Posted WM_CLOSE to WPF main window"
        $exited = $guiProc.WaitForExit(10000)
        if (-not $exited) {
            $guiProc.Kill()
            throw "[$Label] MangaGui did not exit within 10s after WM_CLOSE"
        }
        Assert-True ($guiProc.ExitCode -eq 0) "[$Label] MangaGui exited cleanly with code 0"
    } finally {
        if (-not $guiProc.HasExited) {
            $guiProc.Kill()
            $guiProc.WaitForExit(5000) | Out-Null
        }
    }

    $crashLog = Join-Path $IsolatedAppData "manga_gui\crash.log"
    Assert-True (-not (Test-Path $crashLog)) "[$Label] No crash.log written during GUI startup/shutdown"
}

Write-Host "==> Verifying KomaFlow v$Version release artifacts in $DistDir" -ForegroundColor Cyan

# Check running MangaGui/MangaCli before testing
$runningGui = Get-Process -Name "MangaGui" -ErrorAction SilentlyContinue
if ($null -ne $runningGui) {
    throw "MangaGui is currently running; close it before running isolated verification."
}

# 1. SHA256SUMS.txt verification
Write-Host "`n[1/3] Checking SHA256SUMS.txt..." -ForegroundColor Yellow
Assert-True (Test-Path $SetupExe) "Installer exists: $SetupExe"
Assert-True (Test-Path $PortableZip) "Portable ZIP exists: $PortableZip"
Assert-True (Test-Path $SumsFile) "SHA256SUMS.txt exists"

$sumLines = Get-Content -Path $SumsFile -Encoding UTF8 | Where-Object { $_.Trim() -ne "" }
Assert-True ($sumLines.Count -eq 2) "SHA256SUMS.txt contains 2 artifact hashes"
foreach ($line in $sumLines) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Invalid SHA256SUMS line: $line"
    }
    $expectedHash = $Matches[1]
    $filePath = Join-Path $DistDir $Matches[2]
    $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actualHash -eq $expectedHash) "SHA256 matches for $($Matches[2])"
}

$WorkDir = Join-Path $env:TEMP ("KomaFlowVerify_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

# Snapshot HKCU\Environment\Path before installer test
$EnvRegKeyPath = "HKCU:\Environment"
$OrigPathRaw = $null
$OrigPathKind = $null
$RegKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $false)
if ($null -ne $RegKey) {
    $OrigPathRaw = $RegKey.GetValue("Path", $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -ne $OrigPathRaw) {
        $OrigPathKind = $RegKey.GetValueKind("Path")
    }
    $RegKey.Close()
}
$DesktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "KomaFlow.lnk"
$HadDesktopLnkBefore = Test-Path $DesktopLnk
$StartMenuDir = [Environment]::GetFolderPath("Programs")
$StartMenuLnk = Join-Path $StartMenuDir "KomaFlow.lnk"
$UninstallRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B3F2E8A4-9C61-4D17-A2F5-7E4C8B1D6390}_is1"
$InstallDir = Join-Path $WorkDir "CustomInstallDir"

try {
    # 2. Portable ZIP verification
    Write-Host "`n[2/3] Verifying Portable ZIP..." -ForegroundColor Yellow
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PortableZip)
    try {
        $entries = $zip.Entries
        $badRoot = @($entries | Where-Object { -not ($_.FullName -like "KomaFlow/*" -or $_.FullName -like "KomaFlow\*") })
        $pdbEntries = @($entries | Where-Object { $_.FullName -like "*.pdb" })
        Assert-True ($badRoot.Count -eq 0) "All ZIP entries live under top-level KomaFlow/ folder ($($entries.Count) entries)"
        Assert-True ($pdbEntries.Count -eq 0) "Portable ZIP contains zero .pdb files"
    } finally {
        $zip.Dispose()
    }

    $PortableExtractDir = Join-Path $WorkDir "portable"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PortableZip, $PortableExtractDir)
    $PortableAppDir = Join-Path $PortableExtractDir "KomaFlow"
    Verify-Manifest -AppDir $PortableAppDir

    $guiInfo = (Get-Item (Join-Path $PortableAppDir "MangaGui.exe")).VersionInfo
    Assert-True ($guiInfo.ProductName -eq "KomaFlow" -and $guiInfo.FileVersion.StartsWith($Version)) "MangaGui.exe metadata: Product=$($guiInfo.ProductName), FileVersion=$($guiInfo.FileVersion)"

    $setupInfo = (Get-Item $SetupExe).VersionInfo
    Assert-True ($setupInfo.FileVersion.StartsWith($Version)) "Setup.exe metadata: Product=$($setupInfo.ProductName.Trim()), FileVersion=$($setupInfo.FileVersion.Trim())"

    Invoke-IsolatedSmoke -AppDir $PortableAppDir -IsolatedAppData (Join-Path $WorkDir "appdata_portable") -Label "Portable"

    # 3. Installer custom-dir + default tasks + uninstall verification
    Write-Host "`n[3/3] Verifying Installer (custom dir, default desktop icon & PATH, smoke run, clean uninstall)..." -ForegroundColor Yellow
    Assert-True (-not $HadDesktopLnkBefore) "Precondition: Desktop has no pre-existing KomaFlow.lnk"
    Assert-True (-not (Test-Path $UninstallRegPath)) "Precondition: No pre-existing KomaFlow uninstall registry entry"

    $installLog = Join-Path $WorkDir "install.log"
    $installArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$InstallDir`" /LOG=`"$installLog`""
    $instProc = Start-Process -FilePath $SetupExe -ArgumentList $installArgs -Wait -PassThru
    Assert-True ($instProc.ExitCode -eq 0) "Silent install to custom directory exited 0"

    Assert-True (Test-Path (Join-Path $InstallDir "MangaGui.exe")) "Custom dir contains MangaGui.exe"
    Assert-True (Test-Path (Join-Path $InstallDir "MangaCli.exe")) "Custom dir contains MangaCli.exe"
    Assert-True (Test-Path (Join-Path $InstallDir "unins000.exe")) "Custom dir contains unins000.exe"
    Verify-Manifest -AppDir $InstallDir

    # Verify default desktop shortcut created & points to custom install dir
    Assert-True (Test-Path $DesktopLnk) "Default task created Desktop shortcut KomaFlow.lnk"
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($DesktopLnk)
    Assert-True ($sc.TargetPath -eq (Join-Path $InstallDir "MangaGui.exe")) "Desktop shortcut points to custom MangaGui.exe ($($sc.TargetPath))"
    Assert-True (Test-Path $StartMenuLnk) "Start Menu shortcut created"

    # Verify uninstall registry entry
    Assert-True (Test-Path $UninstallRegPath) "Uninstall registry key created"
    $uProps = Get-ItemProperty -Path $UninstallRegPath
    Assert-True ($uProps.DisplayName -eq "KomaFlow" -and $uProps.DisplayVersion -eq $Version) "Uninstall metadata matches KomaFlow $Version"

    # Verify PATH addition & REG_EXPAND_SZ preserved
    $RegKeyAfter = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $false)
    $PathAfterInstall = $RegKeyAfter.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $PathKindAfter = $RegKeyAfter.GetValueKind("Path")
    $RegKeyAfter.Close()
    $MatchedPath = @($PathAfterInstall -split ";" | Where-Object { $_.TrimEnd("\") -ieq $InstallDir.TrimEnd("\") })
    Assert-True ($MatchedPath.Count -ge 1) "Custom install dir added to HKCU\Environment\Path"
    Assert-True ($PathKindAfter -eq [Microsoft.Win32.RegistryValueKind]::ExpandString) "HKCU\Environment\Path kept REG_EXPAND_SZ type"

    # Smoke test installed binaries under isolated APPDATA
    Invoke-IsolatedSmoke -AppDir $InstallDir -IsolatedAppData (Join-Path $WorkDir "appdata_installed") -Label "Installed"

    # Run real uninstaller
    $uninstallLog = Join-Path $WorkDir "uninstall.log"
    $uninstProc = Start-Process -FilePath (Join-Path $InstallDir "unins000.exe") -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$uninstallLog`"" -Wait -PassThru
    Assert-True ($uninstProc.ExitCode -eq 0) "Silent uninstaller exited 0"

    # Wait briefly for uninstaller self-delete cleanup
    Start-Sleep -Milliseconds 1200
    Assert-True (-not (Test-Path $InstallDir)) "Uninstaller removed custom install directory"
    Assert-True (-not (Test-Path $DesktopLnk)) "Uninstaller removed Desktop shortcut"
    Assert-True (-not (Test-Path $StartMenuLnk)) "Uninstaller removed Start Menu shortcut"
    Assert-True (-not (Test-Path $UninstallRegPath)) "Uninstaller removed uninstall registry entry"

    $RegKeyFinal = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $false)
    $PathAfterUninstall = $RegKeyFinal.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $RegKeyFinal.Close()
    $StillInPath = @($PathAfterUninstall -split ";" | Where-Object { $_.TrimEnd("\") -ieq $InstallDir.TrimEnd("\") })
    Assert-True ($StillInPath.Count -eq 0) "Uninstaller cleanly removed install dir from HKCU\Environment\Path"

    Write-Host "`n==> ALL VERIFICATION CHECKS PASSED!" -ForegroundColor Green
} finally {
    # Safety net: restore Path if test aborted mid-way and left test dir in Path
    try {
        $RegKeyCheck = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $true)
        if ($null -ne $RegKeyCheck) {
            $cur = $RegKeyCheck.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($cur -and ($cur -split ";" | Where-Object { $_.TrimEnd("\") -ieq $InstallDir.TrimEnd("\") })) {
                if ($null -ne $OrigPathRaw) {
                    $RegKeyCheck.SetValue("Path", $OrigPathRaw, $OrigPathKind)
                }
            }
            $RegKeyCheck.Close()
        }
    } catch { }

    if (Test-Path $WorkDir) {
        Remove-Item -Path $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
