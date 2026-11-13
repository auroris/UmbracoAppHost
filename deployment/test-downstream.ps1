#Requires -Version 7.0
# Acceptance test of a downstream host: creates a disposable web project that references this checkout's library
# and commits its own uSync export, boots it without the samples, downloads its own paper report, delivers a
# changed upstream view and definition with a rebuild and refresh-definitions.ps1, and resets its instance. Pass
# -MsBuildProperties for the sibling checkouts, as for the site scripts.
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('apphost-downstream-test-' + [guid]::NewGuid().ToString('N'))),
    [string[]] $MsBuildProperties = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot SiteTools.ps1)
function Assert-True($Condition, [string] $Message) { if (!$Condition) { throw $Message } }
$repository = Split-Path -Parent $PSScriptRoot
$library = Join-Path $repository 'src/UmbracoAppHost'
$libraryProject = Join-Path $library 'UmbracoAppHost.csproj'
$reset = Join-Path $repository reset.ps1
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Output folder already exists: $OutputRoot" }
$projectRoot = Join-Path $OutputRoot DownstreamHost
$projectFile = Join-Path $projectRoot DownstreamHost.csproj
$export = Join-Path $projectRoot uSync
$instance = Join-Path $OutputRoot instance
$working = Join-Path $instance uSync
$homeKey = 'fef29d91-3a84-4f26-a2f5-036c471d5ca3'
$menuItemKey = '2f25bc57-ca13-43c8-bf1f-e28fecc2c75b'
$templateKey = '834d8d82-d814-4d8f-967d-a46579b707a4'
$rowsKey = '0aeb140f-8faf-4c7b-97b2-f691eb0eeb7a'
$paperKey = '1b8d4ebe-1d6b-4313-b212-222f5c9b56b5'
$dataSourcesKey = ([xml](Get-Content -LiteralPath (Join-Path $library 'uSync/Content/data-sources.config') -Raw)).DocumentElement.GetAttribute('Key')
$hostContent = @('downstream-home.config', 'downstream-paper-report.config', 'downstream-rows.config')

foreach ($folder in @('uSync/Content', 'uSync/Templates', 'Views')) { New-Item -ItemType Directory -Force (Join-Path $projectRoot $folder) | Out-Null }
Initialize-ViewImports $repository $projectRoot
Set-Content -LiteralPath (Join-Path $OutputRoot .gitignore) -Encoding utf8NoBOM -Value @'
bin/
obj/
.vs/
instance/
umbraco/
appsettings-schema*.json
umbraco-package-schema.json
'@
Set-Content -LiteralPath $projectFile -Encoding utf8NoBOM -Value @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CompressionEnabled>false</CompressionEnabled>
    <RazorCompileOnBuild>false</RazorCompileOnBuild>
    <RazorCompileOnPublish>false</RazorCompileOnPublish>
    <CopyRazorGenerateFilesToPublishDirectory>true</CopyRazorGenerateFilesToPublishDirectory>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$libraryProject" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $projectRoot Program.cs) -Encoding utf8NoBOM -Value @'
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.RunAsync();
'@
Write-JsonFile (Join-Path $projectRoot appsettings.json) ([ordered]@{
    InstanceRoot = '../instance'
    Umbraco = @{ CMS = @{ Global = @{ UseHttps = $false } } }
    UmbracoAppHost = @{ Samples = $false }
    IdentityServer = @{ Enabled = $false }
    Accounts = @{ MembersManagedBy = 'Umbraco' }
})
Set-Content -LiteralPath (Join-Path $projectRoot 'Views/DownstreamReport.cshtml') -Encoding utf8NoBOM -Value @'
@using AppBlocks.Documents.PaperReports
@model PaperReportModel
@{
    Layout = null;
}
<!doctype html>
<html lang="@Model.Culture.Name">
<head>
    <meta charset="utf-8" />
    <title>Downstream report</title>
</head>
<body class="paper-report">
    <h1>Downstream report</h1>
    @foreach (var row in Model.Rows)
    {
        <p class="one">@row["ONE"]</p>
    }
</body>
</html>
'@

# The host's committed export. Its root is the Website theme demo's root renamed; the demo's menu links the
# demo's pages, so this one links itself only.
$homePage = [xml]::new()
$homePage.PreserveWhitespace = $true
$homePage.Load((Join-Path $library 'uSync/Content/theme-website.config'))
$homePage.DocumentElement.SetAttribute('Key', $homeKey)
$homePage.DocumentElement.SetAttribute('Alias', 'Downstream home')
$homePage.SelectSingleNode('/Content/Info/Path').InnerText = '/DownstreamHome'
# Before the Data sources root, so / serves it.
$homePage.SelectSingleNode('/Content/Info/SortOrder').InnerText = '0'
$homePage.SelectSingleNode('/Content/Info/NodeName').SetAttribute('Default', 'Downstream home')
$homePage.SelectSingleNode('/Content/Info/NodeName/Name').InnerText = 'Downstream home'
foreach ($property in @('siteTitle', 'title')) { $homePage.SelectSingleNode("/Content/Properties/$property/Value").InnerText = 'Downstream home' }
$homePage.SelectSingleNode('/Content/Properties/introduction/Value').InnerText = "<p>A downstream host's own root page.</p>"
$homePage.SelectSingleNode('/Content/Properties/topNavigationMenu/Value').InnerText = ConvertTo-Json -Compress -InputObject @(
    [ordered]@{ key = $menuItemKey; name = 'Downstream home'; itemType = 'Document'; contentKey = $homeKey; published = $true; children = @() })
$homePage.Save((Join-Path $export 'Content/downstream-home.config'))
Set-Content -LiteralPath (Join-Path $export 'Templates/downstreamreport.config') -Encoding utf8NoBOM -Value @"
<?xml version="1.0" encoding="utf-8"?>
<Template Key="$templateKey" Alias="DownstreamReport" Level="1"><Name>Downstream report</Name><Parent /></Template>
"@
Set-Content -LiteralPath (Join-Path $export 'Content/downstream-rows.config') -Encoding utf8NoBOM -Value @"
<?xml version="1.0" encoding="utf-8"?>
<Content Key="$rowsKey" Alias="Downstream rows" Level="2">
  <Info>
    <Parent Key="$dataSourcesKey">Data sources</Parent>
    <Path>/DataSources/DownstreamRows</Path>
    <Trashed>false</Trashed>
    <ContentType>sqlDataSource</ContentType>
    <CreateDate>2026-10-29T12:00:00</CreateDate>
    <NodeName Default="Downstream rows" />
    <SortOrder>10</SortOrder>
    <Published Default="true" />
    <Schedule />
    <Template />
  </Info>
  <Properties>
    <alias>
      <Value><![CDATA[downstream.rows]]></Value>
    </alias>
    <connection>
      <Value><![CDATA[UmbracoDb]]></Value>
    </connection>
    <keyColumns>
      <Value><![CDATA[ONE]]></Value>
    </keyColumns>
    <source>
      <Value><![CDATA[SELECT 1 AS ONE]]></Value>
    </source>
  </Properties>
</Content>
"@
Set-Content -LiteralPath (Join-Path $export 'Content/downstream-paper-report.config') -Encoding utf8NoBOM -Value @"
<?xml version="1.0" encoding="utf-8"?>
<Content Key="$paperKey" Alias="Downstream paper report" Level="2">
  <Info>
    <Parent Key="$homeKey">Downstream home</Parent>
    <Path>/DownstreamHome/downstream-paper-report</Path>
    <Trashed>false</Trashed>
    <ContentType>paperReport</ContentType>
    <CreateDate>2026-10-29T12:00:00</CreateDate>
    <NodeName Default="Downstream paper report" />
    <SortOrder>20</SortOrder>
    <Published Default="true" />
    <Schedule />
    <Template Key="$templateKey">DownstreamReport</Template>
  </Info>
  <Properties>
    <dataSource>
      <Value><![CDATA[$rowsKey]]></Value>
    </dataSource>
    <fileName>
      <Value><![CDATA[downstream-{date}.pdf]]></Value>
    </fileName>
    <culture>
      <Value><![CDATA[en-CA]]></Value>
    </culture>
  </Properties>
</Content>
"@

function Build-Downstream {
    # The library first: with BuildProjectReferences=false the project would not rebuild it.
    foreach ($target in @($libraryProject, $projectFile)) {
        Invoke-Checked dotnet (@('build', $target, '--nologo', '-v', 'minimal') + @($MsBuildProperties | Where-Object { $_ })) | Out-Host
    }
}

function Start-DownstreamHost([string] $Phase) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = $projectRoot
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in @('run', '--project', $projectFile, '--no-build', '--no-launch-profile', '--', '--urls', "http://127.0.0.1:$port")) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    $script:running = @{
        Phase = $Phase; Process = $process; Base = "http://127.0.0.1:$port"
        Stdout = $process.StandardOutput.ReadToEndAsync(); Stderr = $process.StandardError.ReadToEndAsync()
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(300)
    $response = $null
    do {
        if ($process.HasExited) { throw "The host exited ($Phase). See $OutputRoot/$Phase.stderr.log." }
        try { $response = Invoke-WebRequest "$($script:running.Base)/" -SkipHttpErrorCheck -TimeoutSec 5 } catch { $response = $null }
        if ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Downstream home') { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-True ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Downstream home') "/ did not render Downstream home ($Phase). See $OutputRoot/$Phase.stdout.log."
    return $script:running.Base
}

function Stop-DownstreamHost {
    if (!$script:running) { return }
    $process = $script:running.Process
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $OutputRoot "$($script:running.Phase).stdout.log"), $script:running.Stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $OutputRoot "$($script:running.Phase).stderr.log"), $script:running.Stderr.GetAwaiter().GetResult())
    $process.Dispose()
    $script:running = $null
    # dotnet run has exited; the host it started may hold the instance lock a moment longer.
    $lock = Join-Path $instance '.apphost/run.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Test-Path -LiteralPath $lock) {
        try { [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None).Dispose(); break } catch [IO.IOException] { }
        if ([DateTime]::UtcNow -gt $deadline) { throw "The stopped host still holds $lock." }
        Start-Sleep -Milliseconds 200
    }
}

$running = $null
$view = Join-Path $library 'Views/ReportPage.cshtml'
$definition = Join-Path $library 'uSync/Dictionary/search.config'
$originalView = $null
$originalDefinition = $null
try {
    Build-Downstream
    $base = Start-DownstreamHost 'first-start'
    foreach ($path in @('/sample-application/', '/orders-form/')) {
        $sample = Invoke-WebRequest "$base$path" -SkipHttpErrorCheck -TimeoutSec 30
        Assert-True ($sample.StatusCode -eq 404) "$path answered $($sample.StatusCode): the sample content was imported."
    }
    Assert-True (!(Test-Path -LiteralPath (Join-Path $instance 'data/App_Data/ReportsSample.sqlite.db'))) 'The sample orders database was created.'
    foreach ($file in $hostContent) { Assert-True (Test-Path -LiteralPath (Join-Path $working "Content/$file")) "The host's Content/$file is not in the working uSync folder." }
    Assert-True (Test-Path -LiteralPath (Join-Path $working 'Templates/downstreamreport.config')) "The host's template registration is not in the working uSync folder."
    Assert-True (!(Test-Path -LiteralPath (Join-Path $working 'Content/sample-application.config'))) 'The working uSync folder holds the sample content.'
    $runtime = Read-JsonFile (Join-Path $instance '.apphost/runtime.json')
    Assert-True ($runtime.samples -eq $false -and $runtime.hostExport -eq $export) "runtime.json does not record the samples off and the host's export: $($runtime | ConvertTo-Json -Compress)"
    Write-Host 'PASS: the downstream host builds and boots without the samples, and its working uSync folder holds its own export beside the library''s.'

    $password = (Read-JsonFile (Join-Path $instance instance.json)).Umbraco.CMS.Unattended.UnattendedUserPassword
    $credentials = @{ username = 'admin'; password = $password } | ConvertTo-Json
    # The startup handlers rename the installed administrator to admin after the first boot's import.
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $login = Invoke-WebRequest "$base/umbraco/management/api/v1/security/back-office/login" -Method Post -ContentType 'application/json' -Body $credentials -SessionVariable session -SkipHttpErrorCheck
        if ($login.StatusCode -lt 300) { break }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-True ($login.StatusCode -lt 300) "Backoffice sign-in answered $($login.StatusCode): $($login.Content)"
    $paper = Invoke-WebRequest "$base/api/paper-reports/$($paperKey)?format=html" -WebSession $session -SkipHttpErrorCheck
    Assert-True ($paper.StatusCode -eq 200) "The paper report answered $($paper.StatusCode): $($paper.Content)"
    Assert-True ("$($paper.Headers['Content-Type'])" -like 'text/html*') "The paper report is $($paper.Headers['Content-Type']), not HTML."
    Assert-True ($paper.Content.Contains('<h1>Downstream report</h1>') -and $paper.Content.Contains('<p class="one">1</p>')) "The paper report did not render the host's template over its data source."
    Write-Host 'PASS: the paper report downloads as HTML through the host''s own template and data source.'

    $shippedView = Join-Path $projectRoot 'Views/ReportPage.cshtml'
    $ownView = Join-Path $projectRoot 'Views/DownstreamReport.cshtml'
    Assert-True (Test-Path -LiteralPath $shippedView) 'The first start did not write the views the library ships into the host.'
    $ownViewHash = Get-BinaryHash $ownView
    Stop-DownstreamHost
    $marker = '@* Changed upstream by test-downstream.ps1. *@'
    $changedValue = 'Search (changed upstream)'
    $originalView = [IO.File]::ReadAllBytes($view)
    $originalDefinition = [IO.File]::ReadAllBytes($definition)
    [IO.File]::AppendAllText($view, "`n$marker`n")
    $text = [IO.File]::ReadAllText($definition)
    Assert-True ($text.Contains('<Translation Language="en">Search</Translation>')) "$definition no longer has the value this test changes."
    [IO.File]::WriteAllText($definition, $text.Replace('<Translation Language="en">Search</Translation>', "<Translation Language=`"en`">$changedValue</Translation>"))
    Build-Downstream
    $refreshed = Join-Path $working 'Dictionary/search.config'
    & (Join-Path $repository refresh-definitions.ps1) -Instance $instance -WhatIf
    Assert-True (!(Get-Content -LiteralPath $refreshed -Raw).Contains($changedValue)) 'refresh-definitions.ps1 -WhatIf copied a definition.'
    & (Join-Path $repository refresh-definitions.ps1) -Instance $instance
    Assert-True ((Get-Content -LiteralPath $refreshed -Raw).Contains($changedValue)) 'refresh-definitions.ps1 did not deliver the changed upstream definition.'
    $base = Start-DownstreamHost 'refreshed'
    Assert-True ((Get-Content -LiteralPath $shippedView -Raw).Contains($marker)) 'The host did not rewrite the view the library ships after the rebuild.'
    Assert-True ((Get-BinaryHash $ownView) -eq $ownViewHash) "The host's own view was changed."
    Write-Host 'PASS: a rebuild and refresh-definitions.ps1 deliver a changed upstream view and definition, and the host''s own view is untouched.'

    Stop-DownstreamHost
    # Backoffice edits the reset would discard are listed against both exports; the samples the host leaves out and
    # the paper report type the base rewrites are not.
    Add-Content -LiteralPath (Join-Path $working 'Content/downstream-rows.config') -Value '<!-- edited in the backoffice -->'
    Set-Content -LiteralPath (Join-Path $working 'Content/added-in-backoffice.config') -Value '<Content />'
    Remove-Item -LiteralPath (Join-Path $working 'Templates/downstreamreport.config')
    $listed = @(& $reset -InstanceRoot $instance -WhatIf 6>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match '^\s+(changed|added|removed)\s' } | ForEach-Object { $_.Trim() -replace '\s+', ' ' })
    $expected = @('added Content/added-in-backoffice.config', 'changed Content/downstream-rows.config', 'removed Templates/downstreamreport.config')
    Assert-True (($listed -join '; ') -eq ($expected -join '; ')) "reset.ps1 listed: $($listed -join '; '). Expected: $($expected -join '; ')."
    & $reset -InstanceRoot $instance -Force
    Assert-True (!(Test-Path -LiteralPath (Join-Path $instance 'data/Umbraco.sqlite.db'))) 'Reset left the database in place.'
    Assert-True (!(Test-Path -LiteralPath $working)) 'Reset left the working uSync folder in place.'
    $base = Start-DownstreamHost 'after-reset'
    foreach ($file in $hostContent) { Assert-True (Test-Path -LiteralPath (Join-Path $working "Content/$file")) "After the reset the host's Content/$file is not in the working uSync folder." }
    Assert-True (!(Test-Path -LiteralPath (Join-Path $working 'Content/added-in-backoffice.config'))) 'After the reset the working uSync folder kept a discarded file.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $working 'Content/sample-application.config'))) 'After the reset the working uSync folder holds the sample content.'
    Write-Host 'PASS: reset lists unsaved changes against both exports, clears the instance, and the next start imports the host''s content again.'
} finally {
    Stop-DownstreamHost
    if ($null -ne $originalView) { [IO.File]::WriteAllBytes($view, $originalView) }
    if ($null -ne $originalDefinition) { [IO.File]::WriteAllBytes($definition, $originalDefinition) }
}
# The library's build output still carries the changed view and definition.
Invoke-Checked dotnet (@('build', $libraryProject, '--nologo', '-v', 'minimal') + @($MsBuildProperties | Where-Object { $_ })) | Out-Host
Write-Host "PASS: the downstream story works end to end. Test project: $OutputRoot"
