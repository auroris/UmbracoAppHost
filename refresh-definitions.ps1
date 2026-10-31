# Copies the library's definition folders over a host's working uSync folder after the host was rebuilt, for an
# import from the backoffice, and removes its copies of what AppBlocks installs itself. The source is the export in the
# host's build output, which runtime.json names; the instance's content and the host's own files are left alone. The
# host may be running.
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)] [string] $Instance)
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
$Instance = [IO.Path]::GetFullPath($Instance)
$runtimePath = Join-Path $Instance '.apphost/runtime.json'
if (!(Test-Path -LiteralPath $runtimePath)) { throw "$runtimePath is missing. Start the host once on this instance, then run this again." }
$source = (Read-JsonFile $runtimePath).uSyncRoot
if (!(Test-Path -LiteralPath (Join-Path $source usync.config))) { throw "The library's export is missing from the host's build output: $source. Build the host, then run this again." }
$working = Join-Path $Instance uSync
# Without usync.config the next start fills the folder from the library's export; seeding it here would stop that.
if (!(Test-Path -LiteralPath (Join-Path $working usync.config))) { throw "The working uSync folder has not been filled yet: $working. The host's next start fills it from the current export." }
$copy = $PSCmdlet.ShouldProcess($working, "Copy the library's definition folders from $source")
$changed = @(Copy-SyncDefinitions $source $working -ListOnly:(!$copy))
$removed = @(Remove-AppBlocksDefinitions $working -ListOnly:(!$copy))
if ($removed.Count -gt 0) {
    Write-Host "$(if ($copy) { 'Removed' } else { 'Would remove' }) $($removed.Count) definition(s) that AppBlocks installs from ${working}:"
    $removed | ForEach-Object { Write-Host "  $_" }
}
if ($changed.Count -eq 0) { Write-Host "The definitions in $working match the library's export in $source."; return }
Write-Host "$(if ($copy) { 'Updated' } else { 'Would update' }) $($changed.Count) definition(s) in ${working}:"
$changed | ForEach-Object { Write-Host "  $_" }
if ($copy) { Write-Host 'Start the host if it is not running, open Settings > uSync in its backoffice, use Report to review the changes, then Import to apply them.' }
