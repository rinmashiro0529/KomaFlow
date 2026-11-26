[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$OutputDir = "",
    [string]$IsccPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot $OutputDir
}

# Read version from MangaGui.csproj (single source of truth)
$GuiCsprojPath = Join-Path $RepoRoot "src\MangaGui\MangaGui.csproj"
$CliCsprojPath = Join-Path $RepoRoot "src\MangaCli\MangaCli.csproj"
[xml]$GuiCsproj = Get-Content -Path $GuiCsprojPath -Raw -Encoding UTF8
$Version = ($GuiCsproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Failed to read <Version> from $GuiCsprojPath"
}

Write-Host "==> KomaFlow Release Builder v$Version" -ForegroundColor Cyan
Write-Host "    RepoRoot  : $RepoRoot"
Write-Host "    OutputDir : $OutputDir"

# Locate a dotnet.exe that actually has a .NET 8 SDK installed
$DotnetCandidates = [System.Collections.Generic.List[string]]::new()
$LocalDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $LocalDotnet) { $DotnetCandidates.Add($LocalDotnet) }
$PathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $PathDotnet -and -not ($DotnetCandidates -contains $PathDotnet.Source)) {
    $DotnetCandidates.Add($PathDotnet.Source)
}

$DotnetExe = $null
foreach ($cand in $DotnetCandidates) {
    $sdks = & $cand --list-sdks 2>$null
    if ($sdks -match '^8\.') {
        $DotnetExe = $cand
        break
    }
}
if ([string]::IsNullOrWhiteSpace($DotnetExe)) {
    throw "No dotnet executable with .NET 8 SDK found (checked: $($DotnetCandidates -join ', '))."
}

# Locate ISCC.exe (Inno Setup 6)
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $IsccCmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($null -ne $IsccCmd) {
        $IsccPath = $IsccCmd.Source
    } else {
        $Candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $Candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path $IsccPath)) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install Inno Setup 6 or pass -IsccPath."
}
Write-Host "    dotnet    : $DotnetExe"
Write-Host "    ISCC      : $IsccPath"

# Step 1: Asset invariants check (matches RELEASE_CHECKLIST.md)
Write-Host "`n[1/6] Checking built-in asset invariants..." -ForegroundColor Yellow
$AssetsSourceDir = Join-Path $RepoRoot "assets"
$SakuraDictPath = Join-Path $AssetsSourceDir "sakura_dict.txt"
if (-not (Test-Path $SakuraDictPath) -or (Get-Item $SakuraDictPath).Length -ne 0) {
    throw "assets/sakura_dict.txt must exist and be 0 bytes."
}
$MtuiConfig = Get-Content -Path (Join-Path $AssetsSourceDir "mtui_config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ($MtuiConfig.cli.verbose -ne $false -or $MtuiConfig.cli.batch_size -ne 1) {
    throw "assets/mtui_config.json must have cli.verbose=false and cli.batch_size=1."
}

$GitBashCandidates = @(
    "C:\Program Files\Git\bin\bash.exe",
    "C:\Program Files\Git\usr\bin\bash.exe"
)
$BashExe = $GitBashCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($BashExe)) {
    $BashCmd = Get-Command bash -ErrorAction SilentlyContinue
    if ($null -ne $BashCmd) { $BashExe = $BashCmd.Source }
}
if (-not [string]::IsNullOrWhiteSpace($BashExe)) {
    Write-Host "    bash      : $BashExe"
    Push-Location $RepoRoot
    try {
        foreach ($sh in @("vm_setup.sh", "vm_job.sh", "vm_status.sh", "vm_pack.sh")) {
            & $BashExe -n "assets/$sh"
            if ($LASTEXITCODE -ne 0) { throw "bash -n failed for $sh" }
        }
        if (-not $SkipTests) {
            $VmTestScript = Join-Path $RepoRoot "tests\scripts\test_vm_scripts.sh"
            if (Test-Path $VmTestScript) {
                & $BashExe "tests/scripts/test_vm_scripts.sh"
                if ($LASTEXITCODE -ne 0) { throw "test_vm_scripts.sh failed." }
            }
        }
    } finally {
        Pop-Location
    }
} else {
    Write-Warning "bash not found; skipping shell syntax checks."
}

# Step 2: Build & Test gate
Write-Host "`n[2/6] Running Release build & test gate..." -ForegroundColor Yellow
$SlnPath = Join-Path $RepoRoot "MangaGui.sln"
& $DotnetExe build $SlnPath -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

if (-not $SkipTests) {
    & $DotnetExe test $SlnPath -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
} else {
    Write-Host "    (-SkipTests specified, skipping dotnet test)" -ForegroundColor DarkGray
}

# Step 3: Self-contained shared-runtime publish
Write-Host "`n[3/6] Publishing self-contained shared-runtime payload..." -ForegroundColor Yellow
$StageRoot = Join-Path $OutputDir "stage"
$StageAppDir = Join-Path $StageRoot "KomaFlow"
if (Test-Path $StageRoot) {
    Remove-Item -Path $StageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $StageAppDir -Force | Out-Null

$CommonPublishArgs = @(
    "-c", "Release",
    "-r", "win-x64",
    "-p:SelfContained=true",
    "-p:PublishSingleFile=false",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "--nologo",
    "-o", $StageAppDir
)

& $DotnetExe publish $GuiCsprojPath @CommonPublishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish MangaGui failed." }

& $DotnetExe publish $CliCsprojPath @CommonPublishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish MangaCli failed." }

# Strip any leftover PDBs from NuGet packages
Get-ChildItem -Path $StageAppDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

# Verify required files & generate build_manifest.json
$ExpectedAssets = @(
    "assets/manga.example.json",
    "assets/mtui_config.json",
    "assets/mtui_patch.py",
    "assets/prompt_hq.yaml",
    "assets/sakura_dict.txt",
    "assets/vm_job.sh",
    "assets/vm_pack.sh",
    "assets/vm_setup.sh",
    "assets/vm_status.sh"
)

$ManifestEntries = [System.Collections.Generic.List[object]]::new()
foreach ($rel in @("MangaGui.exe", "MangaCli.exe") + $ExpectedAssets) {
    $full = Join-Path $StageAppDir ($rel -replace "/", "\")
    if (-not (Test-Path $full)) {
        throw "Missing required staged artifact: $rel"
    }
    $hash = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($rel.StartsWith("assets/")) {
        $srcFile = Join-Path $RepoRoot ($rel -replace "/", "\")
        $srcHash = (Get-FileHash -Path $srcFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $srcHash) {
            throw "Asset hash mismatch for $rel (staged=$hash, source=$srcHash)"
        }
    }
    $ManifestEntries.Add([ordered]@{
        path   = $rel
        sha256 = $hash
    })
}

$ManifestObj = [ordered]@{
    version    = $Version
    built_at   = [DateTimeOffset]::UtcNow.ToString("o")
    layout     = "self-contained-shared-win-x64"
    files      = $ManifestEntries
}
$ManifestJson = $ManifestObj | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $StageAppDir "build_manifest.json"),
    $ManifestJson + "`n",
    [System.Text.UTF8Encoding]::new($false)
)

$StageStats = Get-ChildItem -Path $StageAppDir -Recurse -File | Measure-Object -Property Length -Sum
Write-Host ("    Staged {0} files ({1:N1} MB uncompressed)" -f $StageStats.Count, ($StageStats.Sum / 1MB))

# Step 4: Build Portable ZIP (with KomaFlow\ top-level directory)
Write-Host "`n[4/6] Creating Portable ZIP..." -ForegroundColor Yellow
$PortableZipName = "KomaFlow-v$Version-win-x64-portable.zip"
$PortableZipPath = Join-Path $OutputDir $PortableZipName
if (Test-Path $PortableZipPath) {
    Remove-Item -Path $PortableZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StageAppDir,
    $PortableZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true
)
$ZipSizeMb = (Get-Item $PortableZipPath).Length / 1MB
Write-Host ("    Created {0} ({1:N2} MB)" -f $PortableZipName, $ZipSizeMb)

# Step 5: Build Inno Setup Installer EXE
Write-Host "`n[5/6] Compiling Inno Setup installer..." -ForegroundColor Yellow
$IssPath = Join-Path $RepoRoot "installer\KomaFlow.iss"
$SetupExeName = "KomaFlow-v$Version-win-x64-setup.exe"
$SetupExePath = Join-Path $OutputDir $SetupExeName
if (Test-Path $SetupExePath) {
    Remove-Item -Path $SetupExePath -Force
}

& $IsccPath "/DAppVersion=$Version" "/DSourceDir=$StageAppDir" "/DOutputDir=$OutputDir" $IssPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $SetupExePath)) {
    throw "Expected installer not found at $SetupExePath"
}
$SetupSizeMb = (Get-Item $SetupExePath).Length / 1MB
Write-Host ("    Created {0} ({1:N2} MB)" -f $SetupExeName, $SetupSizeMb)

# Step 6: Generate SHA256SUMS.txt
Write-Host "`n[6/6] Writing SHA256SUMS.txt..." -ForegroundColor Yellow
$SumsLines = @()
foreach ($artifact in @($SetupExePath, $PortableZipPath)) {
    $h = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $artifact
    $SumsLines += "$h  $name"
}
$SumsPath = Join-Path $OutputDir "SHA256SUMS.txt"
[System.IO.File]::WriteAllText(
    $SumsPath,
    ($SumsLines -join "`n") + "`n",
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "`n==> Release artifacts ready in $OutputDir" -ForegroundColor Green
foreach ($line in $SumsLines) {
    Write-Host "    $line"
}
