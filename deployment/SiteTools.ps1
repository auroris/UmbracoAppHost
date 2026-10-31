# Shared by site creation, site updates, the reference reset and the test scripts. Requires PowerShell 7.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# uSync folders the reference owns on every site. Content, Domains and Media are copied once when a
# site is created and belong to that site from then on.
$script:SharedDefinitionFolders = @('ContentTypes', 'DataTypes', 'Dictionary', 'Languages', 'MediaTypes', 'MemberTypes', 'RelationTypes', 'Templates')

# The base rewrites the paper report type's allowed templates to the installed templates, so a working copy of this
# definition differs from every saved export without anyone having edited it.
$script:RewrittenDefinitions = @('ContentTypes/paperreport.config')

# What AppBlocks and its companions install themselves, one path per line. A site's working folder holds these only as
# copies: the export shipped them until they moved, and uSync's export on save writes them. AppBlocksDefinitionImport
# refuses to import them, so a copy does no harm; it is only noise, which is why they are never compared with an export
# and an update tidies them away.
$script:AppBlocksDefinitions = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot appblocks-definitions.txt) | Where-Object { $_ })

# The reference launcher: published without -Project, and run by builds and sites made before the scripts took it.
$script:ReferenceProject = 'src/UmbracoAppHost.Web/UmbracoAppHost.Web.csproj'
$script:ReferenceAssembly = 'UmbracoAppHost.Web.dll'

function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}

function Write-JsonFile([string] $Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Read-JsonFile([string] $Path) {
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Get-SafePath([string] $Root, [string] $RelativePath) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $base $RelativePath))
    if ([IO.Path]::IsPathRooted($RelativePath) -or !$path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the intended directory: $RelativePath"
    }
    # Do not follow a junction/symlink into another site's files.
    $check = $path
    while ($check.Length -ge $base.Length) {
        try {
            if ([IO.File]::GetAttributes($check) -band [IO.FileAttributes]::ReparsePoint) { throw "Linked paths are not supported: $check" }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $check = [IO.Path]::GetDirectoryName($check)
    }
    return $path
}

function Get-TextHash([string] $Path) {
    # Umbraco rewrites BOMs/newlines and strips the final line break when registering templates.
    $value = [IO.File]::ReadAllText($Path).TrimStart([char]0xFEFF).Replace("`r`n", "`n").TrimEnd([char]10)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant()
}

function Get-AppFileHash([string] $Path) {
    if ([IO.Path]::GetExtension($Path) -eq '.cshtml') { return Get-TextHash $Path }
    return Get-BinaryHash $Path
}

function Get-BinaryHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Copy-Tree([string] $Source, [string] $Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $sourcePath = Get-SafePath $Source $relative
        $target = Get-SafePath $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($sourcePath, $target, $true)
    }
}

function Get-Build([string] $Path) {
    $root = (Resolve-Path -LiteralPath $Path).Path
    $manifestPath = Join-Path $root build.json
    if (!(Test-Path -LiteralPath $manifestPath)) { throw "Not a published build (build.json is missing): $root" }
    $manifest = Read-JsonFile $manifestPath
    if ($manifest.schemaVersion -ne 3) { throw "This build was published by different scripts. Publish it again from the reference checkout: $root" }
    if (!(Test-Path -LiteralPath (Join-Path $root 'app/uSync/usync.config'))) {
        throw "This build does not contain the saved uSync export at app/uSync. Publish it again from the reference checkout: $root"
    }
    if (!$manifest.Contains('assembly')) { $manifest.project = [IO.Path]::GetFileName($script:ReferenceProject); $manifest.assembly = $script:ReferenceAssembly }
    if (!(Test-Path -LiteralPath (Get-SafePath (Join-Path $root app) $manifest.assembly) -PathType Leaf)) {
        throw "This build does not contain the application it records, app/$($manifest.assembly): $root"
    }
    return @{ Root = $root; Manifest = $manifest }
}

function Resolve-Build([string] $ScriptRoot, [string] $Build, [string] $Project, [string] $Assembly, [string[]] $MsBuildProperties) {
    # An explicit folder, the build folder this script was published into, or a fresh publish of the project.
    $published = if ($Build) { $Build } elseif (Test-Path -LiteralPath (Join-Path $ScriptRoot build.json)) { $ScriptRoot }
    if ($published -and ($Project -or $Assembly)) { throw "-Project and -Assembly choose what to publish; the build in $published was published already." }
    if ($published) { return Get-Build $published }
    return Publish-Reference $ScriptRoot $Project $Assembly $MsBuildProperties
}

function Publish-Reference([string] $RepositoryRoot, [string] $Project, [string] $Assembly, [string[]] $MsBuildProperties) {
    if (!$Project) {
        $Project = Join-Path $RepositoryRoot $script:ReferenceProject
        if (!(Test-Path -LiteralPath $Project)) { throw 'Run this script from the UmbracoAppHost checkout or from a published build folder, or pass -Build <folder>.' }
    } elseif (!(Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Project not found: $Project" }
    $Project = (Resolve-Path -LiteralPath $Project).Path
    if (!$Assembly) { $Assembly = [IO.Path]::GetFileNameWithoutExtension($Project) + '.dll' }
    $sourceRoot = Split-Path -Parent $Project
    $revision = 'nogit'; $dirty = $false
    try {
        $revision = (& git -C $sourceRoot rev-parse --short HEAD 2>$null) ?? 'nogit'
        $dirty = @(& git -C $sourceRoot status --porcelain 2>$null).Count -gt 0
    } catch { }
    $id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $revision
    $root = Get-SafePath (Join-Path $RepositoryRoot 'artifacts/builds') $id
    if (Test-Path -LiteralPath $root) { throw "Build directory already exists: $root" }
    Write-Host "Publishing $([IO.Path]::GetFileName($Project)) to $root"
    # Build output goes to the console, not into this function's return value. The caller's properties come last so
    # that they win, the configuration included.
    Invoke-Checked dotnet (@('publish', $Project, '-p:Configuration=Release', '-o', (Join-Path $root app), '--nologo') + @($MsBuildProperties | Where-Object { $_ })) | Out-Host
    if (!(Test-Path -LiteralPath (Join-Path $root "app/$Assembly") -PathType Leaf)) {
        throw "The published application has no $Assembly. Pass -Assembly with the file name of the project's output assembly."
    }
    # Every start rewrites the views the library ships when the content root's copies differ, and an update refuses
    # a shipped file that changed, so the build carries the library's views whatever the project folder held.
    $libraryViews = Join-Path $RepositoryRoot 'src/UmbracoAppHost/Views'
    foreach ($view in @(Get-ChildItem -LiteralPath $libraryViews -File -Filter '*.cshtml' -ErrorAction SilentlyContinue)) {
        $target = Get-SafePath (Join-Path $root app) "Views/$($view.Name)"
        # As at startup, a host may add its own usings to the imports.
        if ((Test-Path -LiteralPath $target) -and ($view.Name -eq '_ViewImports.cshtml' -or (Get-TextHash $target) -eq (Get-TextHash $view.FullName))) { continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $view.FullName -Destination $target -Force
    }
    # The build folder is self-contained: the scripts inside it create and update sites without this checkout.
    foreach ($file in @('new-site.ps1', 'update-site.ps1', 'refresh-definitions.ps1')) { Copy-Item (Join-Path $RepositoryRoot $file) $root }
    Copy-Tree (Join-Path $RepositoryRoot deployment) (Join-Path $root deployment)
    Copy-Item (Join-Path $RepositoryRoot 'docs/deployment.md') (Join-Path $root README.md)
    [xml] $versions = Get-Content (Join-Path $RepositoryRoot Directory.Packages.props) -Raw
    $umbraco = @($versions.Project.ItemGroup.PackageVersion | Where-Object Include -EQ 'Umbraco.Cms')[0].Version
    $app = Join-Path $root app
    $appFiles = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File | Sort-Object FullName) {
        $appFiles[[IO.Path]::GetRelativePath($app, $file.FullName).Replace('\', '/')] = Get-AppFileHash $file.FullName
    }
    Write-JsonFile (Join-Path $root build.json) ([ordered]@{
        schemaVersion = 3; id = $id; createdUtc = [DateTime]::UtcNow.ToString('O'); umbraco = $umbraco
        project = [IO.Path]::GetFileName($Project); assembly = $Assembly
        source = [ordered]@{ revision = $revision; workingTreeChanges = $dirty }
        appFiles = $appFiles
    })
    Write-Host "Published build $id."
    return Get-Build $root
}

function Copy-SyncDefinitions([string] $Source, [string] $Destination, [switch] $IncludeContent, [switch] $ListOnly) {
    # Copies the saved uSync export into a working uSync folder and returns the paths that were added or
    # changed; -ListOnly returns them without copying. Files the site added are left in place, and so are files the
    # reference no longer ships, except the ones Remove-AppBlocksDefinitions deals with.
    $folders = if ($IncludeContent) { @(Get-ChildItem -LiteralPath $Source -Directory | ForEach-Object Name) } else { $script:SharedDefinitionFolders }
    $changed = @()
    foreach ($folder in $folders) {
        $from = Join-Path $Source $folder
        if (!(Test-Path -LiteralPath $from)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File -Filter '*.config') {
            $relative = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
            $target = Get-SafePath $Destination $relative
            if ((Test-Path -LiteralPath $target) -and (Get-BinaryHash $target) -eq (Get-BinaryHash $file.FullName)) { continue }
            $changed += $relative
            if ($ListOnly) { continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    $marker = Join-Path $Destination usync.config
    if (!(Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath (Join-Path $Source usync.config))) {
        if (!$ListOnly) { Copy-Item -LiteralPath (Join-Path $Source usync.config) -Destination $marker }
        $changed += 'usync.config'
    }
    return $changed
}

function Remove-AppBlocksDefinitions([string] $Working, [switch] $ListOnly) {
    # Removes the copies of AppBlocks' definitions from a working uSync folder and returns the paths; -ListOnly returns
    # them without removing anything. Any other file stays, whoever added it. This is housekeeping, not protection: the
    # import refuses the copies anyway, and the site's database keeps the definitions.
    $removed = @()
    foreach ($relative in $script:AppBlocksDefinitions) {
        $target = Get-SafePath $Working $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $removed += $relative
        if (!$ListOnly) { Remove-Item -LiteralPath $target }
    }
    return $removed
}

function Get-SyncExportFiles([string] $Root, [string] $Skip) {
    # The files of a uSync folder by relative path, without its marker, the definitions the base rewrites and AppBlocks'
    # definitions. $Skip leaves out a folder nested inside it: uSync's default working folder sits inside a host's export.
    $files = @{}
    $skipped = if ($Skip) { [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Skip)) + [IO.Path]::DirectorySeparatorChar }
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.config') {
        if ($skipped -and $file.FullName.StartsWith($skipped, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -eq 'usync.config' -or $relative -in $script:RewrittenDefinitions -or $relative -in $script:AppBlocksDefinitions) { continue }
        $files[$relative] = $file.FullName
    }
    return $files
}

function Compare-SyncFolders([string] $Working, [string] $Baseline) {
    # Lists what a working uSync folder has that its saved export does not: changed, added and removed
    # files. Byte-order marks and line endings are ignored.
    $differences = @()
    # A folder without usync.config was never seeded, so nothing has been exported there yet.
    if (!(Test-Path -LiteralPath (Join-Path $Working usync.config))) { return $differences }
    $baselineFiles = Get-SyncExportFiles $Baseline
    foreach ($entry in (Get-SyncExportFiles $Working).GetEnumerator() | Sort-Object Key) {
        if (!$baselineFiles.ContainsKey($entry.Key)) { $differences += "added    $($entry.Key)"; continue }
        if ((Get-TextHash $entry.Value) -ne (Get-TextHash $baselineFiles[$entry.Key])) { $differences += "changed  $($entry.Key)" }
        $baselineFiles.Remove($entry.Key)
    }
    foreach ($relative in @($baselineFiles.Keys | Sort-Object)) { $differences += "removed  $relative" }
    return $differences
}

function Compare-HostSyncFolders([string] $Working, [string] $LibraryExport, [string] $HostExport) {
    # The same for a host outside this checkout, whose first start fills the working folder from two exports: a
    # file is compared with the host's export at the paths the host ships and with the library's at the others,
    # and one that matches neither is listed. A library Content file may be a sample the host leaves out, so its
    # absence is not.
    $differences = @()
    if (!(Test-Path -LiteralPath (Join-Path $Working usync.config))) { return $differences }
    $libraryFiles = Get-SyncExportFiles $LibraryExport
    $hostFiles = if ($HostExport) { Get-SyncExportFiles $HostExport $Working } else { @{} }
    $workingFiles = Get-SyncExportFiles $Working
    foreach ($entry in $workingFiles.GetEnumerator() | Sort-Object Key) {
        $saved = @($hostFiles[$entry.Key], $libraryFiles[$entry.Key] | Where-Object { $_ })
        if ($saved.Count -eq 0) { $differences += "added    $($entry.Key)"; continue }
        $hash = Get-TextHash $entry.Value
        if (!@($saved | Where-Object { (Get-TextHash $_) -eq $hash })) { $differences += "changed  $($entry.Key)" }
    }
    $removed = @($hostFiles.Keys) + @($libraryFiles.Keys | Where-Object { $_ -notlike 'Content/*' })
    foreach ($relative in @($removed | Where-Object { !$workingFiles.ContainsKey($_) } | Sort-Object -Unique)) { $differences += "removed  $relative" }
    return $differences
}

function Test-SamplesEnabled($AppSettings) {
    # UmbracoAppHost:Samples, default true, as a host's appsettings.json sets it.
    if (!$AppSettings.Contains('UmbracoAppHost') -or !$AppSettings.UmbracoAppHost.Contains('Samples')) { return $true }
    return [string]$AppSettings.UmbracoAppHost.Samples -ne 'false'
}

function New-InstanceSettings([string] $Root, [string] $Name) {
    $settingsPath = Get-SafePath $Root instance.json
    if (Test-Path -LiteralPath $settingsPath) { throw "Instance settings already exist: $settingsPath" }
    foreach ($folder in @('data', 'media', 'uSync', '.apphost')) { New-Item -ItemType Directory -Force (Get-SafePath $Root $folder) | Out-Null }
    Write-JsonFile $settingsPath ([ordered]@{
        Urls = 'https://localhost:' + (Get-Random -Minimum 44400 -Maximum 44500)
        Umbraco = @{ CMS = @{
            Global = @{ Id = [guid]::NewGuid().ToString() }
            Imaging = @{ HMACSecretKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64)) }
            Hosting = @{ Debug = $false }
            Unattended = @{
                InstallUnattended = $true; UpgradeUnattended = $true; UnattendedTelemetryLevel = 'Minimal'
                UnattendedUserName = 'Administrator'; UnattendedUserEmail = "admin@$($Name.ToLowerInvariant()).local"
                UnattendedUserPassword = 'Start!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(20)) + 'a9'
            }
        } }
        Accounts = @{ EnsureMemberGroups = @('Report Users', 'Users'); SeedCurrentWindowsUserAsAdministrator = $true }
    })
}
