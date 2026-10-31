# Exercises a built host over HTTP. Use a disposable ContentRoot and InstanceRoot with -Reset.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Project,
    [string] $ContentRoot = (Split-Path -Parent $Project),
    [string] $InstanceRoot = (Join-Path $ContentRoot 'App_Data/UmbracoAppHost'),
    [switch] $Reset
)
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$ContentRoot = [IO.Path]::GetFullPath($ContentRoot)
$InstanceRoot = [IO.Path]::GetFullPath($InstanceRoot)
$resetScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'reset.ps1'
[IO.Directory]::CreateDirectory($ContentRoot) | Out-Null
function Assert-Startup($Condition, [string] $Message) { if (!$Condition) { throw $Message } }
function Read-Identity {
    $settings = Get-Content -LiteralPath (Join-Path $InstanceRoot instance.json) -Raw | ConvertFrom-Json
    return $settings.Umbraco.CMS.Global.Id
}
function Test-RunningHost {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = $ContentRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Without a sign-in provider an anonymous request sees the Sample Application's own sign-in page instead of a challenge.
    foreach ($argument in @('run', '--project', $Project, '--no-build', '--no-launch-profile', '--',
        '--contentRoot', $ContentRoot, '--InstanceRoot', $InstanceRoot, '--urls', "http://127.0.0.1:$port", '--IdentityServer:Enabled=false')) {
        $info.ArgumentList.Add($argument)
    }
    $worker = [Diagnostics.Process]::Start($info)
    $stdout = $worker.StandardOutput.ReadToEndAsync()
    $stderr = $worker.StandardError.ReadToEndAsync()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            if ($worker.HasExited) { throw ('Host exited: ' + $stderr.GetAwaiter().GetResult()) }
            try { $response = Invoke-WebRequest "http://127.0.0.1:$port/" -SkipHttpErrorCheck -TimeoutSec 3 }
            catch { $response = $null }
            if ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Sample Application') { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        # The saved export's first root is the Sample Application, whose sign-in page names it.
        Assert-Startup ($response -and $response.StatusCode -eq 200 -and $response.Content -match 'Sample Application') "The Sample Application did not render. See $ContentRoot/startup.stderr.log."
        Assert-Startup ($response.Content -match 'loginModel.Username') 'The Sample Application did not offer the member sign-in form.'
        foreach ($url in @('/dist/scripts/site.js', '/dist/css/site.css', '/dist/css/app.css',
            '/App_Plugins/ApplicationBlocks/runtime.js', '/api/reports/ui/ir.js', '/umbraco/')) {
            $asset = Invoke-WebRequest "http://127.0.0.1:$port$url" -SkipHttpErrorCheck -TimeoutSec 10
            Assert-Startup ($asset.StatusCode -eq 200 -and $asset.RawContentLength -gt 100) "Missing endpoint $url ($($asset.StatusCode))."
        }
        Assert-Startup (Test-Path -LiteralPath (Join-Path $InstanceRoot 'data/Umbraco.sqlite.db')) 'The host did not create its own database.'
        if ($Reset) {
            $rejected = $false
            try { & $resetScript -InstanceRoot $InstanceRoot -Force }
            catch { $rejected = $_.Exception.Message -like '*Stop the website*' }
            Assert-Startup $rejected 'Reset did not reject a running instance.'
        }
        Write-Host 'PASS: welcome page, website/application assets, reports, block assets and backoffice.'
    } finally {
        if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $ContentRoot 'startup.stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $ContentRoot 'startup.stderr.log'), $stderr.GetAwaiter().GetResult())
        $worker.Dispose()
    }
}
Test-RunningHost
$originalIdentity = Read-Identity
Test-RunningHost
Assert-Startup ((Read-Identity) -eq $originalIdentity) 'An ordinary restart changed the instance identity.'
Write-Host 'PASS: a second start preserves the existing instance.'
if ($Reset) {
    $baselineRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/UmbracoAppHost/uSync'
    $baselineHashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $baselineRoot -Recurse -File -Filter '*.config') {
        $baselineHashes[[IO.Path]::GetRelativePath($baselineRoot, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    $database = Join-Path $InstanceRoot 'data/Umbraco.sqlite.db'
    $originalHash = (Get-FileHash -LiteralPath $database).Hash
    & $resetScript -InstanceRoot $InstanceRoot -WhatIf
    Assert-Startup ((Get-FileHash -LiteralPath $database).Hash -eq $originalHash) 'WhatIf changed the database.'
    & $resetScript -InstanceRoot $InstanceRoot -Force
    Assert-Startup (!(Test-Path -LiteralPath $database)) 'Reset left the database in place.'
    Assert-Startup (!(Test-Path -LiteralPath (Join-Path $InstanceRoot instance.json))) 'Reset retained the previous identity and credentials.'
    Assert-Startup (Test-Path -LiteralPath $Project) 'Reset removed the source project.'
    $restoredRoot = Join-Path $InstanceRoot 'uSync'
    Assert-Startup (@(Get-ChildItem -LiteralPath $restoredRoot -Recurse -File).Count -eq $baselineHashes.Count) 'Reset did not restore exactly the saved export.'
    foreach ($relative in $baselineHashes.Keys) {
        Assert-Startup ((Get-FileHash -LiteralPath (Join-Path $restoredRoot $relative)).Hash -eq $baselineHashes[$relative]) "Reset changed the export: $relative"
        Assert-Startup ((Get-FileHash -LiteralPath (Join-Path $baselineRoot $relative)).Hash -eq $baselineHashes[$relative]) "Reset changed the saved source: $relative"
    }
    Test-RunningHost
    Assert-Startup ((Read-Identity) -ne $originalIdentity) 'Reset did not create an independent new instance.'
    Write-Host 'PASS: reset protects a running instance, supports WhatIf, clears data and creates a new identity.'
}
