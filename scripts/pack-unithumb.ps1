<#
.SYNOPSIS
Pack the UniThumb UPM package from the repo root (package.json at root, Editor/,
images/) into a distributable tarball at dist/com.maykerstudio.unithumb-<version>.tgz.

.DESCRIPTION
# - Arg-only interface: -Version, -OutputDir, -DryRun.
# - Stages the package source to a temp dir with a copy-filter that excludes all
#   .meta files, scene/content files (.unity/.prefab), the Project/ dev tree,
#   docs/, scripts/, and other dev-only artifacts. The source is never mutated.
# - Builds the tarball with npm pack, falling back to tar.exe (bsdtar on Windows),
#   then Compress-Archive (zip with the package root folder inside, renamed to .tgz).
# - Validates the tarball listing: single root folder, package.json at its root,
#   Editor/ present, zero .meta/.unity/.prefab files, zero Project/ and docs/
#   entries, zero "Samples~" entries.
# - Dry-run prints exactly what would be packaged (file count + paths) and writes
#   nothing. Non-zero exit on any failure.

.EXAMPLE
pwsh ./scripts/pack-unithumb.ps1 -DryRun

.EXAMPLE
pwsh ./scripts/pack-unithumb.ps1 -Version 1.0.1 -OutputDir dist
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = 'dist',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- constants ---------------------------------------------------------------

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PackageSource = $RepoRoot
$PackageJsonPath = Join-Path $PackageSource 'package.json'
$DefaultVersion = '1.0.0'
$VersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'

# Directory names excluded at any depth (dev-only artifacts + example content).
# Examples/ and Samples~/ are never published: scenes are generator-only.
# Project/ is the Unity dev tree, docs/ holds plan artifacts, scripts/ holds
# dev tooling - none ship in the tarball.
$ExcludedDirNames = @(
    '.git', '.github', '.vs', '.vscode', '.idea',
    'Tests', 'Examples', 'Samples~', 'node_modules', 'bin', 'obj', 'dist',
    'Build', 'Builds',
    'Project', 'Project~', 'docs', 'scripts'
)
# Exact file names excluded (dev-only artifacts).
$ExcludedFileNames = @(
    '.gitignore', '.gitattributes', '.npmrc', 'package-lock.json', 'Thumbs.db',
    'AGENTS.md'
)
# Extensions excluded (Unity metas, scene/content files, build droppings).
# .unity/.prefab never ship, even if scene content reappears in the dev folder.
$ExcludedExtensions = @(
    '.meta', '.unity', '.prefab', '.tgz', '.zip', '.csproj', '.sln', '.suo',
    '.user', '.userprefs'
)

# --- helpers -----------------------------------------------------------------

function Write-ProgressLog {
    param([string]$Message)
    Write-Host "[pack] $Message"
}

function Get-PackageFileList {
    <#
    Returns sorted relative paths (forward slashes) of every file that would be
    packaged: all files under $PackageSource minus .meta and dev-only artifacts.
    This is the single source of truth for both dry-run and staging.
    #>
    $files = Get-ChildItem -LiteralPath $PackageSource -Recurse -File -Force
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $rel = $file.FullName.Substring($PackageSource.Length).TrimStart('\', '/')
        $relUnix = $rel.Replace('\', '/')
        if (Test-IsExcluded $relUnix) {
            continue
        }
        $result.Add($relUnix)
    }
    $result.Sort()
    return $result
}

function Test-IsExcluded {
    param([string]$RelPath)
    $segments = $RelPath.Split('/')
    foreach ($segment in $segments) {
        if ($ExcludedDirNames -contains $segment) {
            return $true
        }
    }
    $name = $segments[-1]
    if ($ExcludedFileNames -contains $name) {
        return $true
    }
    $ext = [System.IO.Path]::GetExtension($name)
    if ($ExcludedExtensions -contains $ext) {
        return $true
    }
    return $false
}

function Copy-StagedPackage {
    <#
    Copies the filtered file list into $TargetRoot/<name>/ so the archive always
    has a single package root folder (npm pack convention). Returns the staged
    package directory. The embedded package is only read, never modified.
    #>
    param(
        [string]$TargetRoot,
        [string]$RootName,
        [string[]]$FileList
    )
    $packageDir = Join-Path $TargetRoot $RootName
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    foreach ($rel in $FileList) {
        $src = Join-Path $PackageSource ($rel.Replace('/', '\'))
        $dst = Join-Path $packageDir ($rel.Replace('/', '\'))
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir)) {
            New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
    return $packageDir
}

function Read-PackageVersion {
    <#
    Reads the version from package.json. Returns $null when missing/invalid so the
    caller can fall back to $DefaultVersion. Never invents metadata.
    #>
    $json = Get-Content -LiteralPath $PackageJsonPath -Raw | ConvertFrom-Json
    if ($null -ne $json.version -and $json.version -match $VersionPattern) {
        return [string]$json.version
    }
    return $null
}

function Read-PackageName {
    $json = Get-Content -LiteralPath $PackageJsonPath -Raw | ConvertFrom-Json
    if ($null -ne $json.name) {
        return [string]$json.name
    }
    return 'com.maykerstudio.unithumb'
}

function Get-TarballListing {
    <#
    Returns the entry list of an archive. Prefers tar.exe (bsdtar reads both gzip
    tarballs and zip), falls back to .NET ZipFile.
    #>
    param([string]$ArchivePath)
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        $lines = & $tar.Source -tzf $ArchivePath 2>$null
        if ($LASTEXITCODE -eq 0 -and $null -ne $lines) {
            return @($lines)
        }
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function Test-TarballValid {
    <#
    Validates the archive listing:
    - exactly one top-level entry forming the package root folder
    - package.json directly inside the root folder
    - Editor/ inside the root folder
    - zero entries containing ".meta", ".unity", ".prefab"
    - zero Project/ and zero docs/ entries (dev tree + plan artifacts)
    - zero entries containing "Samples~"
    Returns a hashtable with pass/fail plus counts.
    #>
    param([string[]]$Entries)
    $rootFolder = $null
    $hasPackageJson = $false
    $hasEditor = $false
    $examplesCount = 0
    $projectCount = 0
    $docsCount = 0
    $metaCount = 0
    $unityCount = 0
    $prefabCount = 0
    $samplesCount = 0

    foreach ($entry in $Entries) {
        $clean = $entry.TrimEnd('/')
        if ($clean.Length -eq 0) {
            continue
        }
        $segments = $clean.Split('/')
        if ($null -eq $rootFolder) {
            $rootFolder = $segments[0]
        }
        elseif ($segments[0] -ne $rootFolder) {
            return @{ pass = $false; reason = "multiple top-level entries: '$rootFolder' vs '$($segments[0])'" }
        }
        if ($entry -match '\.meta($|/)') {
            $metaCount++
        }
        if ($entry -match '\.unity($|/)') {
            $unityCount++
        }
        if ($entry -match '\.prefab($|/)') {
            $prefabCount++
        }
        if ($entry -match 'Samples~') {
            $samplesCount++
        }
        if ($segments.Count -eq 2 -and $segments[1] -eq 'package.json') {
            $hasPackageJson = $true
        }
        if ($segments.Count -ge 2 -and $segments[1] -eq 'Editor') {
            $hasEditor = $true
        }
        if ($segments.Count -ge 2 -and $segments[1] -eq 'Examples') {
            $examplesCount++
        }
        if ($segments.Count -ge 2 -and $segments[1] -eq 'Project') {
            $projectCount++
        }
        if ($segments.Count -ge 2 -and $segments[1] -eq 'docs') {
            $docsCount++
        }
    }

    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $rootFolder) {
        $reasons.Add('archive is empty')
    }
    if (-not $hasPackageJson) {
        $reasons.Add('package.json missing at package root')
    }
    if (-not $hasEditor) {
        $reasons.Add('Editor/ missing')
    }
    if ($examplesCount -gt 0) {
        $reasons.Add("$examplesCount Examples/ entries found")
    }
    if ($unityCount -gt 0) {
        $reasons.Add("$unityCount .unity entries found")
    }
    if ($prefabCount -gt 0) {
        $reasons.Add("$prefabCount .prefab entries found")
    }
    if ($metaCount -gt 0) {
        $reasons.Add("$metaCount .meta entries found")
    }
    if ($samplesCount -gt 0) {
        $reasons.Add("$samplesCount Samples~ entries found")
    }
    if ($projectCount -gt 0) {
        $reasons.Add("$projectCount Project/ entries found")
    }
    if ($docsCount -gt 0) {
        $reasons.Add("$docsCount docs/ entries found")
    }

    return @{
        pass        = ($reasons.Count -eq 0)
        reason      = ($reasons -join '; ')
        rootFolder  = $rootFolder
        entryCount  = $Entries.Count
        metaCount   = $metaCount
        samplesCount = $samplesCount
        hasPackageJson = $hasPackageJson
        hasEditor   = $hasEditor
        examplesCount = $examplesCount
        projectCount = $projectCount
        docsCount   = $docsCount
        unityCount  = $unityCount
        prefabCount = $prefabCount
    }
}

function Invoke-NpmPack {
    <#
    Runs npm pack in $PackageDir, writing into $OutputRoot. Returns the path of
    the produced tarball, or $null on failure. Throws only on hard errors.
    #>
    param(
        [string]$PackageDir,
        [string]$OutputRoot,
        [string]$ExpectedName
    )
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if ($null -eq $npm) {
        Write-ProgressLog 'npm not found, skipping npm pack'
        return $null
    }
    Write-ProgressLog "building with npm pack in $PackageDir"
    Push-Location -Path $PackageDir
    try {
        $null = & $npm.Source pack --pack-destination $OutputRoot 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-ProgressLog "npm pack failed with exit code $LASTEXITCODE"
            return $null
        }
    }
    finally {
        Pop-Location
    }
    $candidate = Get-ChildItem -LiteralPath $OutputRoot -Filter '*.tgz' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $candidate) {
        Write-ProgressLog 'npm pack produced no tarball'
        return $null
    }
    if ($candidate.Name -ne $ExpectedName) {
        $target = Join-Path $OutputRoot $ExpectedName
        Move-Item -LiteralPath $candidate.FullName -Destination $target -Force
        return $target
    }
    return $candidate.FullName
}

function Invoke-TarPack {
    <#
    Builds a gzip tarball with tar.exe (bsdtar). $PackageDir must be
    $TargetRoot/<name>. Returns the tarball path or $null on failure.
    #>
    param(
        [string]$TargetRoot,
        [string]$RootName,
        [string]$OutputPath
    )
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        return $null
    }
    Write-ProgressLog "building with tar.exe (bsdtar) -> $OutputPath"
    $outDir = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    Push-Location -Path $TargetRoot
    try {
        & $tar.Source -czf $OutputPath $RootName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-ProgressLog "tar.exe failed with exit code $LASTEXITCODE"
            return $null
        }
    }
    finally {
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $OutputPath)) {
        return $null
    }
    return $OutputPath
}

function Invoke-ZipPack {
    <#
    Last-resort fallback: Compress-Archive of the staged package root folder,
    then rename to .tgz. The archive contains <name>/ as its root folder,
    never a flat zip.
    #>
    param(
        [string]$TargetRoot,
        [string]$RootName,
        [string]$OutputPath
    )
    Write-ProgressLog "building with Compress-Archive (zip fallback) -> $OutputPath"
    $outDir = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $zipPath = [System.IO.Path]::ChangeExtension($OutputPath, '.zip')
    $packageDir = Join-Path $TargetRoot $RootName
    Compress-Archive -Path $packageDir -DestinationPath $zipPath -Force
    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw "Compress-Archive produced no archive at $zipPath"
    }
    Move-Item -LiteralPath $zipPath -Destination $OutputPath -Force
    return $OutputPath
}

# --- main --------------------------------------------------------------------

$exitCode = 0
$stagingRoot = $null
try {
    if (-not (Test-Path -LiteralPath $PackageSource)) {
        throw "package source not found: $PackageSource"
    }
    if (-not (Test-Path -LiteralPath $PackageJsonPath)) {
        throw "package.json not found: $PackageJsonPath"
    }

    $name = Read-PackageName

    # Version resolution: -Version wins, then package.json, then the default.
    # NOTE: the local MUST NOT be named $Version - PowerShell variables are
    # case-insensitive, so assigning $version would clobber the parameter.
    $resolvedVersion = $null
    if ($PSBoundParameters.ContainsKey('Version') -and -not [string]::IsNullOrWhiteSpace($Version)) {
        $resolvedVersion = $Version.Trim()
    }
    else {
        $resolvedVersion = Read-PackageVersion
        if ($null -eq $resolvedVersion) {
            $resolvedVersion = $DefaultVersion
        }
    }
    if ($resolvedVersion -notmatch $VersionPattern) {
        throw "invalid version '$resolvedVersion' (expected semver like 1.0.0)"
    }

    $fileList = @(Get-PackageFileList)
    Write-ProgressLog "package '$name' version $resolvedVersion, $($fileList.Count) files to pack from $PackageSource"

    if ($DryRun) {
        Write-ProgressLog 'DRY RUN - listing files that would be packaged, writing nothing'
        foreach ($rel in $fileList) {
            Write-Host "  $rel"
        }
        Write-ProgressLog "DRY RUN complete: $($fileList.Count) files"
        exit 0
    }

    $outputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $RepoRoot $OutputDir }
    if (-not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
        Write-ProgressLog "created output dir $outputDir"
    }
    $archivePath = Join-Path $outputDir "$name-$resolvedVersion.tgz"

    # Stage a filtered copy (never mutate the embedded package). The root folder
    # inside the archive is named 'package' to match the npm pack layout exactly,
    # so every backend produces an identical archive shape.
    $rootName = 'package'
    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("unithumb-pack-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $packageDir = Copy-StagedPackage -TargetRoot $stagingRoot -RootName $rootName -FileList $fileList
    Write-ProgressLog "staged $($fileList.Count) files to $packageDir"

    # Builder chain: npm pack -> tar.exe -> Compress-Archive.
    $built = Invoke-NpmPack -PackageDir $packageDir -OutputRoot $outputDir -ExpectedName "$name-$resolvedVersion.tgz"
    if ($null -eq $built) {
        $built = Invoke-TarPack -TargetRoot $stagingRoot -RootName $rootName -OutputPath $archivePath
    }
    if ($null -eq $built) {
        $built = Invoke-ZipPack -TargetRoot $stagingRoot -RootName $rootName -OutputPath $archivePath
    }
    if ($null -eq $built -or -not (Test-Path -LiteralPath $built)) {
        throw 'all packaging backends failed'
    }

    $sizeBytes = (Get-Item -LiteralPath $built).Length
    Write-ProgressLog "tarball written: $built ($sizeBytes bytes)"

    # Validate the archive listing.
    $entries = @(Get-TarballListing -ArchivePath $built)
    $validation = Test-TarballValid -Entries $entries
    Write-ProgressLog "listing: $($validation.entryCount) entries, root folder '$($validation.rootFolder)'"
    Write-ProgressLog "validation: package.json=$($validation.hasPackageJson) Editor=$($validation.hasEditor) Examples=$($validation.examplesCount) Project=$($validation.projectCount) docs=$($validation.docsCount) unity=$($validation.unityCount) prefab=$($validation.prefabCount) meta=$($validation.metaCount) Samples~=$($validation.samplesCount)"
    if (-not $validation.pass) {
        throw "tarball validation failed: $($validation.reason)"
    }
    Write-ProgressLog "validation passed: $built"
}
catch {
    Write-Error "[pack] FAILED: $_"
    $exitCode = 1
}
finally {
    if ($null -ne $stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
