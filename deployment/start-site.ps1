# Runs this site with Kestrel. Under IIS this file is not used: point the application at app/, whose
# appsettings.json already names this folder as the InstanceRoot.
[CmdletBinding()]
param([string] $Urls)
$ErrorActionPreference = 'Stop'
$site = $PSScriptRoot
if (!$Urls) { $Urls = (Get-Content -LiteralPath (Join-Path $site instance.json) -Raw | ConvertFrom-Json).Urls }
$assembly = (Get-Content -LiteralPath (Join-Path $site '.apphost/instance.json') -Raw | ConvertFrom-Json).assembly
# Sites created before the scripts recorded the assembly run the reference launcher.
if (!$assembly) { $assembly = 'UmbracoAppHost.Web.dll' }
$arguments = @($assembly, '--InstanceRoot', $site)
if ($Urls) { $arguments += @('--urls', $Urls) }
Push-Location (Join-Path $site app)
try { & dotnet @arguments; $code = $LASTEXITCODE }
finally { Pop-Location }
exit $code
