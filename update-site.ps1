# Updates an existing site from the reference: publishes the application (or reuses a build with -Build),
# swaps it in while preserving the site's settings, data, media and content, and refreshes the shared
# uSync definitions in the site's uSync folder, removing its copies of what AppBlocks now installs itself. Import them
# from the backoffice afterwards. A site created with -Project is updated with the same -Project, or with a build of it.
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Site,
    [string] $Build,
    [string] $Project,
    [string] $Assembly,
    [string[]] $MsBuildProperties = @()
)
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
$siteRoot = (Resolve-Path -LiteralPath $Site).Path
$statePath = Get-SafePath $siteRoot '.apphost/instance.json'
if (!(Test-Path -LiteralPath $statePath)) { throw "Not a site created by new-site.ps1: $siteRoot" }
$state = Read-JsonFile $statePath
if ($state.schemaVersion -notin @(2, 3)) { throw 'This site was created by different scripts and cannot be updated by this one.' }
$app = Get-SafePath $siteRoot app
# The shipped files must be unmodified so nothing made through the backoffice is lost. The operator may edit appsettings.json.
foreach ($entry in $state.appFiles.GetEnumerator()) {
    if ($entry.Key -eq 'appsettings.json') { continue }
    $file = Get-SafePath $app $entry.Key
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-AppFileHash $file) -ne $entry.Value) {
        throw "The installed application was changed: $($entry.Key). Make the change in the reference and publish it, or restore the file, then update again."
    }
}
if (!$PSCmdlet.ShouldProcess($siteRoot, 'Install the current reference build and refresh the shared uSync definitions')) { return }
$published = Resolve-Build $PSScriptRoot $Build $Project $Assembly $MsBuildProperties
$incoming = $published.Manifest
$installed = if ($state.Contains('assembly')) { $state.assembly } else { $script:ReferenceAssembly }
if ($incoming.assembly -ne $installed) { throw "This build runs $($incoming.assembly) and the site runs $installed. Update it with a build of its own project: -Project <its project file> or -Build <folder>." }
if ([version]$incoming.umbraco -lt [version]$state.umbraco) { throw 'This build runs an older Umbraco than the site. Restore a matching database backup instead of downgrading.' }
if (([version]$incoming.umbraco).Major -ne ([version]$state.umbraco).Major) { throw 'A major Umbraco upgrade needs its own migration plan before it is installed.' }
$lockPath = Get-SafePath $siteRoot '.apphost/run.lock'
try { $running = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { throw 'Stop the site before updating it.' }
try {
    # IIS may run the site without our launcher; make sure its binaries are not in use.
    foreach ($path in @($state.appFiles.Keys | Where-Object { $_ -match '\.(dll|exe)$' })) {
        $probe = [IO.File]::Open((Get-SafePath $app $path), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    }
    $id = [guid]::NewGuid().ToString('N')
    $previous = if ($state.Contains('build')) { $state.build } else { $state.release }
    $stage = Get-SafePath $siteRoot ('.apphost/staging/' + $id)
    $backup = Get-SafePath $siteRoot ('.apphost/backups/' + $previous + '-' + $id)
    Copy-Tree (Join-Path $published.Root app) $stage
    # The operator's settings, including the optional administrator password, carry forward.
    $appSettings = Get-SafePath $app 'appsettings.json'
    if (Test-Path -LiteralPath $appSettings -PathType Leaf) {
        Copy-Item -LiteralPath $appSettings -Destination (Get-SafePath $stage 'appsettings.json')
    }
    # Files created through Umbraco (templates, plugin files) carry forward; logs and caches stay in the backup.
    foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($app, $file.FullName).Replace('\', '/')
        if ($relative -eq 'appsettings.json' -or $state.appFiles.Contains($relative)) { continue }
        if ($relative -like 'umbraco/Logs/*' -or $relative -like 'umbraco/Data/TEMP/*') { continue }
        $target = Get-SafePath $stage $relative
        if (Test-Path -LiteralPath $target) { throw "A file created in this site conflicts with a new shared file: $relative" }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $changed = @(Copy-SyncDefinitions (Get-SafePath $stage 'uSync') (Get-SafePath $siteRoot uSync))
    $removed = @(Remove-AppBlocksDefinitions (Get-SafePath $siteRoot uSync))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
    Move-Item -LiteralPath $app -Destination $backup
    try { Move-Item -LiteralPath $stage -Destination $app }
    catch { Move-Item -LiteralPath $backup -Destination $app; throw }
    $state.schemaVersion = 3
    $state.build = $incoming.id; $state.umbraco = $incoming.umbraco; $state.assembly = $incoming.assembly; $state.appFiles = $incoming.appFiles
    $state.Remove('release')
    Write-JsonFile $statePath $state
    Write-Host "Installed build $($incoming.id). The previous application is in $backup"
    if ($removed.Count -gt 0) {
        Write-Host "Removed $($removed.Count) uSync definition(s) that AppBlocks installs from $siteRoot/uSync:"
        $removed | ForEach-Object { Write-Host "  $_" }
    }
    if ($changed.Count -eq 0) { Write-Host 'The shared uSync definitions are unchanged.' }
    else {
        Write-Host "Updated $($changed.Count) shared uSync definition(s) in $siteRoot/uSync:"
        $changed | ForEach-Object { Write-Host "  $_" }
        Write-Host 'Start the site, open Settings > uSync in the backoffice, use Report to review the changes, then Import to apply them.'
    }
} finally { $running.Dispose() }
