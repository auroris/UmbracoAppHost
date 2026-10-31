# Resets the reference instance. Stop the website in Visual Studio, run this, then press Play: the next
# start installs a fresh database, imports the saved uSync export and creates new administrator credentials.
# Backoffice edits that were exported to reference/uSync but not copied into the saved export are listed,
# and the script asks before discarding them. -Force skips that question.
# -InstanceRoot also resets the instance of a host built outside this checkout. Its working uSync folder is
# deleted and nothing is copied: the host's next start fills it from the library's export and its own, as a
# first boot does.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstanceRoot = (Join-Path $PSScriptRoot reference),
    [switch] $Force
)
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
$InstanceRoot = [IO.Path]::GetFullPath($InstanceRoot)
if (!(Test-Path -LiteralPath $InstanceRoot)) { Write-Host "Nothing to reset: $InstanceRoot does not exist."; return }
$projectRoot = Join-Path $PSScriptRoot 'src/UmbracoAppHost.Web'
$baseline = Join-Path $PSScriptRoot 'src/UmbracoAppHost/uSync'
$downstream = $false
$hostExport = $null
$runtimePath = Join-Path $InstanceRoot '.apphost/runtime.json'
if (Test-Path -LiteralPath $runtimePath) {
    $runtime = Read-JsonFile $runtimePath
    if (!$runtime.managedDatabase) { throw 'This instance uses an external database, which this script does not reset.' }
    $projectRoot = $runtime.projectRoot
    # A host built outside this checkout fills its working folder from the export in its build output and its own.
    $checkout = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PSScriptRoot)) + [IO.Path]::DirectorySeparatorChar
    if (![IO.Path]::GetFullPath($runtime.uSyncRoot).StartsWith($checkout, [StringComparison]::OrdinalIgnoreCase)) {
        $downstream = $true
        $baseline = $runtime.uSyncRoot
        # A published site's content root holds the library's export itself, so it has no export of its own.
        if ($runtime.hostExport -and [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($runtime.hostExport)) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($baseline))) {
            $hostExport = $runtime.hostExport
        }
    }
}
if (!(Test-Path -LiteralPath (Join-Path $baseline usync.config))) {
    if ($downstream) { throw "The library's export is missing from the host's build output: $baseline. Build the host, then run this again." }
    throw "The saved uSync export is missing: $baseline"
}
$working = Join-Path $InstanceRoot uSync
$lockPath = Join-Path $InstanceRoot '.apphost/run.lock'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($lockPath)) | Out-Null
try { $lease = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Stop the website in Visual Studio before resetting it.' }
try {
    # Backoffice edits are exported to the working folder. They survive a reset only once they are copied into the saved export.
    if ($downstream) {
        $unsaved = @(Compare-HostSyncFolders $working $baseline $hostExport)
        $saved = if ($hostExport) { "the host's export ($hostExport) and the library's ($baseline)" } else { "the library's export ($baseline)" }
        $action = 'Delete the database, media, sign-in keys, credentials, working uSync folder and temporary caches'
        $keep = "Keep everything. Copy the files you want into the host's export, then run the reset again."
    } else {
        $unsaved = @(Compare-SyncFolders $working $baseline)
        $saved = "the saved export ($baseline)"
        $action = 'Delete the database, media, sign-in keys, credentials, working uSync folder and temporary caches, then copy the saved uSync export'
        $keep = 'Keep everything. Copy the files you want into the saved export, then run the reset again.'
    }
    if ($unsaved.Count -gt 0) {
        Write-Host "The working uSync folder ($working) differs from ${saved}:"
        $unsaved | ForEach-Object { Write-Host "  $_" }
    }
    if (!$PSCmdlet.ShouldProcess($InstanceRoot, $action)) { return }
    if ($unsaved.Count -gt 0 -and !$Force) {
        $choices = [Management.Automation.Host.ChoiceDescription[]]@(
            [Management.Automation.Host.ChoiceDescription]::new('&Yes', "Discard the listed changes and reset $(if ($downstream) { 'the instance' } else { 'the reference' })."),
            [Management.Automation.Host.ChoiceDescription]::new('&No', $keep))
        $answer = $Host.UI.PromptForChoice('Unsaved uSync changes', "Reset now and lose these $($unsaved.Count) change(s)?", $choices, 1)
        if ($answer -ne 0) { Write-Host 'Reset cancelled. Nothing was changed.'; return }
    }
    $targets = @('data', 'media', 'uSync', 'instance.json', '.apphost/runtime.json', '.apphost/backups', '.apphost/staging') |
        ForEach-Object { Join-Path $InstanceRoot $_ }
    $targets += Join-Path $projectRoot 'umbraco/Data/TEMP'
    foreach ($target in $targets) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force } }
    if ($downstream) {
        Write-Host "Reset complete: $working was deleted and nothing was copied into it."
        Write-Host ("Start the host: like a first boot, it copies the library's export into that folder, without the samples when UmbracoAppHost:Samples is false, " +
            "then the host's own export, installs a fresh database and imports them. New administrator credentials will be in $(Join-Path $InstanceRoot instance.json).")
    } else {
        $copied = @(Copy-SyncDefinitions $baseline $working -IncludeContent)
        Write-Host "Reset complete: $($copied.Count) uSync files copied to $working."
        Write-Host "Press Play to install a fresh database and import them. New administrator credentials will be in $(Join-Path $InstanceRoot instance.json)."
    }
} finally { $lease.Dispose() }
