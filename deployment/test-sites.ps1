#Requires -Version 7.0
# Integration checks for the site scripts: creates two disposable sites from one build, boots both, then
# updates one and verifies what an update preserves and what it refreshes. Run from the checkout to
# publish a fresh build, or pass -Build.
[CmdletBinding()]
param(
    [string] $Build,
    [string] $OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('apphost-site-test-' + [guid]::NewGuid().ToString('N'))),
    [string[]] $MsBuildProperties = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot SiteTools.ps1)
$scripts = Split-Path -Parent $PSScriptRoot
$published = Resolve-Build $scripts $Build '' '' $MsBuildProperties
function Assert-True($Condition, [string] $Message) { if (!$Condition) { throw $Message } }
New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$sites = @{}
foreach ($name in @('first', 'second')) {
    $site = Get-SafePath $OutputRoot $name
    & (Join-Path $scripts new-site.ps1) -Build $published.Root -Name ('Test' + $name) -Destination $site
    $sites[$name] = $site
    Assert-True (Test-Path (Join-Path $site 'uSync/usync.config')) 'The saved uSync export was not copied into the site.'
    Assert-True (Test-Path (Join-Path $site 'uSync/Content')) 'The starter content was not copied into the site.'
    $assembly = (Read-JsonFile (Join-Path $site '.apphost/instance.json')).assembly
    Assert-True ($assembly -eq 'UmbracoAppHost.Web.dll') "The site records $assembly as its application, not the reference launcher."
    $settings = Read-JsonFile (Join-Path $site instance.json)
    # Without a sign-in provider an anonymous request sees the Sample Application's own sign-in page instead of a challenge.
    $settings.IdentityServer = @{ Enabled = $false }
    $settings.Accounts.SeedCurrentWindowsUserAsAdministrator = $false
    $settings.Umbraco.CMS.Global.UseHttps = $false
    Write-JsonFile (Join-Path $site instance.json) $settings
    $port = Get-Random -Minimum 47000 -Maximum 49000
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = Join-Path $site app
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($arg in @($assembly, '--InstanceRoot', $site, '--urls', "http://127.0.0.1:$port")) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    try {
        # A first start installs Umbraco and imports the whole export, which a busy machine takes minutes over.
        $deadline = [DateTime]::UtcNow.AddSeconds(300)
        $response = $null
        # The saved export's first root is the Sample Application, whose sign-in page names it.
        $expectedTitle = 'Sample Application'
        do {
            if ($process.HasExited) { throw "Published host exited: $($stderr.GetAwaiter().GetResult())" }
            try { $response = Invoke-WebRequest "http://127.0.0.1:$port/" -SkipHttpErrorCheck -TimeoutSec 5 } catch { }
            if ($response -and $response.StatusCode -eq 200 -and $response.Content -match $expectedTitle) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-True ($response -and $response.StatusCode -eq 200) "$name home did not render. Logs: $site/app/umbraco/Logs"
        Assert-True ($response.Content -match $expectedTitle) "$name imported content missing."
        Assert-True ($response.Content -match 'loginModel.Username') "$name did not offer the member sign-in form."
        foreach ($path in @('/dist/scripts/site.js', '/dist/css/app.css', '/api/reports/ui/ir.js')) {
            $asset = Invoke-WebRequest "http://127.0.0.1:$port$path" -SkipHttpErrorCheck
            Assert-True ($asset.StatusCode -eq 200 -and $asset.RawContentLength -gt 100) "Published asset missing: $path"
        }
        Set-Content (Join-Path $site media/preserved.txt) 'instance media'
        $media = Invoke-WebRequest "http://127.0.0.1:$port/media/preserved.txt" -SkipHttpErrorCheck
        Assert-True ($media.StatusCode -eq 200) 'External media folder is not served.'
        Assert-True (Test-Path (Join-Path $site data/Umbraco.sqlite.db)) 'Database was not created outside app/.'
        # The first boot's import publishes the Orders data source, whose validation opens the sample database; a host
        # without the samples creates none, which test-downstream.ps1 checks.
        Assert-True (Test-Path (Join-Path $site data/App_Data/ReportsSample.sqlite.db)) 'The import did not validate the Orders data source against its sample database.'
        $locked = $false
        try { $probe = [IO.File]::Open((Join-Path $site '.apphost/run.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
        catch [IO.IOException] { $locked = $true }
        Assert-True $locked 'The host did not hold its instance lock.'
        Write-Host "PASS: $name boots and serves its shell, both frontend bundles, reports and instance media."
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $site stdout.log)
        $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $site stderr.log)
        $process.Dispose()
    }
}
$firstState = Read-JsonFile (Join-Path $sites.first '.apphost/instance.json')
$secondState = Read-JsonFile (Join-Path $sites.second '.apphost/instance.json')
Assert-True (($firstState.appFiles | ConvertTo-Json -Compress) -eq ($secondState.appFiles | ConvertTo-Json -Compress)) 'Sites did not install identical builds.'
$firstSettings = Read-JsonFile (Join-Path $sites.first 'instance.json')
$secondSettings = Read-JsonFile (Join-Path $sites.second 'instance.json')
Assert-True ($firstSettings.Umbraco.CMS.Global.Id -ne $secondSettings.Umbraco.CMS.Global.Id) 'Sites reused an identity.'
$site = $sites.first
$preserved = @{}
foreach ($path in @('instance.json', 'data/Umbraco.sqlite.db', 'media/preserved.txt')) { $preserved[$path] = Get-AppFileHash (Join-Path $site $path) }
$appSettingsPath = Join-Path $site app/appsettings.json
$appSettings = Read-JsonFile $appSettingsPath
if (!$appSettings.Contains('UmbracoAppHost')) { $appSettings.UmbracoAppHost = @{} }
$appSettings.UmbracoAppHost.AdminPassword = 'Release-test-admin-password91!'
Write-JsonFile $appSettingsPath $appSettings
$preserved['app/appsettings.json'] = Get-AppFileHash $appSettingsPath
$unknown = Join-Path $site app/Views/InstanceCreated.cshtml
Set-Content $unknown '@* authored in Umbraco *@'
# A shared definition edited on the site is refreshed; site content and site-added definitions are kept.
$sharedDefinition = Join-Path $site 'uSync/ContentTypes/apppage.config'
Add-Content $sharedDefinition '<!-- edited on the site -->'
$siteContent = Join-Path $site 'uSync/Content/sample-application.config'
Add-Content $siteContent '<!-- edited on the site -->'
$siteDefinition = Join-Path $site 'uSync/ContentTypes/siteowned.config'
Set-Content $siteDefinition '<ContentType Alias="siteOwned" />'
# A copy of a definition AppBlocks installs, as an older build or uSync's export on save leaves it, is tidied away.
$staleDefinitions = @('uSync/ContentTypes/appreport.config', 'uSync/ContentTypes/reportdefinition.config', 'uSync/DataTypes/ApplicationText.config') | ForEach-Object { Join-Path $site $_ }
foreach ($stale in $staleDefinitions) { Set-Content $stale '<!-- the export of an older build -->' }
# Updating to the same build exercises staging, preservation and the directory switch.
& (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root -WhatIf
foreach ($stale in $staleDefinitions) { Assert-True (Test-Path $stale) 'Update -WhatIf removed a definition.' }
& (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root
foreach ($path in $preserved.Keys) { Assert-True ((Get-AppFileHash (Join-Path $site $path)) -eq $preserved[$path]) "Update changed $path" }
Assert-True (Test-Path $unknown) 'Update lost an Umbraco-authored template.'
Assert-True ((Get-BinaryHash $sharedDefinition) -eq (Get-BinaryHash (Join-Path $published.Root 'app/uSync/ContentTypes/apppage.config'))) 'Update did not refresh a shared uSync definition.'
Assert-True ((Get-Content $siteContent -Raw) -like '*edited on the site*') 'Update overwrote site content.'
Assert-True (Test-Path $siteDefinition) 'Update removed a site-added definition.'
foreach ($stale in $staleDefinitions) { Assert-True (!(Test-Path $stale)) "Update left a stale copy of an AppBlocks definition: $stale" }
Assert-True (Test-Path (Join-Path $site 'uSync/DataTypes/ApplicationApplicationBlocks.config')) 'Update removed a definition the export keeps on purpose.'
foreach ($relative in $script:AppBlocksDefinitions) { Assert-True (!(Test-Path (Join-Path $published.Root "app/uSync/$relative"))) "The build still ships $relative, which AppBlocks installs." }
$updatedState = Read-JsonFile (Join-Path $site '.apphost/instance.json')
Assert-True ($updatedState.build -eq $published.Manifest.id -and $updatedState.assembly -eq $published.Manifest.assembly) 'Update did not record the installed build and its application.'
$shared = Join-Path $site app/Views/WebsiteHome.cshtml
Add-Content $shared '@* local modification *@'
$rejected = $false
try { & (Join-Path $scripts update-site.ps1) -Site $site -Build $published.Root } catch { $rejected = $_.Exception.Message -like '*installed application was changed*' }
Assert-True $rejected 'Update overwrote a modified shared template.'
Write-Host "PASS: sites share identical application files; update preserves data, settings, media and content, refreshes shared definitions, removes stale copies of AppBlocks' definitions, and rejects conflicting edits. Test sites: $OutputRoot"
