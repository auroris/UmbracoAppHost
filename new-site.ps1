# Creates an independent site from the reference: publishes the application (or reuses a build with
# -Build), copies it, generates the site's identity and credentials, and copies the saved uSync export
# into the site's uSync folder. The site's first start installs Umbraco and imports that export.
# -Project publishes another host that references the library instead of the reference launcher.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z_][A-Za-z0-9_-]*$')] [string] $Name,
    [string] $Destination = (Join-Path (Get-Location) $Name),
    [string] $Build,
    [string] $Project,
    [string] $Assembly,
    [string[]] $MsBuildProperties = @()
)
. (Join-Path $PSScriptRoot 'deployment/SiteTools.ps1')
$siteRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $siteRoot) { throw "Destination already exists: $siteRoot" }
$published = Resolve-Build $PSScriptRoot $Build $Project $Assembly $MsBuildProperties
Copy-Tree (Join-Path $published.Root app) (Get-SafePath $siteRoot app)
# The published settings point at the reference checkout; a site's data lives beside its app folder.
$appSettingsPath = Get-SafePath $siteRoot 'app/appsettings.json'
$appSettings = Read-JsonFile $appSettingsPath
$appSettings.InstanceRoot = '..'
Write-JsonFile $appSettingsPath $appSettings
New-InstanceSettings $siteRoot $Name
# The script cannot tell the samples from the other content, so a host that leaves them out fills its uSync
# folder at its first start: the library's export without them, then the host's own files.
$copied = @()
if (Test-SamplesEnabled $appSettings) { $copied = @(Copy-SyncDefinitions (Get-SafePath $siteRoot 'app/uSync') (Get-SafePath $siteRoot uSync) -IncludeContent) }
Write-JsonFile (Get-SafePath $siteRoot '.apphost/instance.json') ([ordered]@{
    schemaVersion = 3; name = $Name; build = $published.Manifest.id; umbraco = $published.Manifest.umbraco
    assembly = $published.Manifest.assembly
    appFiles = $published.Manifest.appFiles
})
Copy-Item (Join-Path $PSScriptRoot 'deployment/start-site.ps1') $siteRoot
if ($copied.Count -gt 0) { Write-Host "Created $siteRoot from build $($published.Manifest.id); $($copied.Count) uSync files are in its uSync folder." }
else { Write-Host "Created $siteRoot from build $($published.Manifest.id). Its application turns the samples off, so its first start copies the library's export without them and the application's own export into its uSync folder." }
Write-Host "Start it with: pwsh '$siteRoot/start-site.ps1'. The first start installs Umbraco and imports the uSync folder."
Write-Host "Administrator credentials: $siteRoot/instance.json"
