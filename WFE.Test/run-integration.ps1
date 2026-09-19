param(
    [Parameter(Mandatory = $true)]
    [string] $MariaDbBin
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$binRoot = (Resolve-Path -LiteralPath $MariaDbBin).Path
$installer = Join-Path $binRoot 'mariadb-install-db.exe'
$serverExe = Join-Path $binRoot 'mariadbd.exe'
$adminExe = Join-Path $binRoot 'mariadb-admin.exe'
if (-not (Test-Path -LiteralPath $adminExe)) { $adminExe = Join-Path $binRoot 'mysqladmin.exe' }
foreach ($executable in @($installer, $serverExe, $adminExe)) {
    if (-not (Test-Path -LiteralPath $executable)) { throw "Required MariaDB executable is missing: $executable" }
}

$runRoot = Join-Path $repoRoot ('.buildverify/integration-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
New-Item -ItemType Directory -Path $runRoot | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$priorConnection = $env:HALEYFLOW_TEST_CONNECTION
$priorPassword = $env:MYSQL_PWD
$serverProcess = $null
$testExit = 1
try {
    & $installer "--datadir=$dataRoot" "--password=$password" "--port=$port" --silent > (Join-Path $runRoot 'install.log') 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MariaDB initialization failed. See install.log in $runRoot." }

    $serverConfig = Join-Path $runRoot 'server.ini'
    $dataSetting = $dataRoot.Replace('\', '/')
    $errorSetting = (Join-Path $runRoot 'server-error.log').Replace('\', '/')
    @"
[mysqld]
datadir=$dataSetting
port=$port
bind-address=127.0.0.1
default-time-zone=+00:00
log-error=$errorSetting
"@ | Set-Content -LiteralPath $serverConfig -Encoding ascii
    $serverProcess = Start-Process -FilePath $serverExe -ArgumentList ('--defaults-file="' + $serverConfig + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'server.log') -RedirectStandardError (Join-Path $runRoot 'server-stderr.log')
    $env:MYSQL_PWD = $password
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($serverProcess.HasExited) { throw "Disposable MariaDB server exited. See logs in $runRoot." }
        & $adminExe --protocol=tcp --host=127.0.0.1 "--port=$port" --user=root --connect-timeout=1 ping *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "Disposable MariaDB server did not become ready. See logs in $runRoot." }
    $env:HALEYFLOW_TEST_CONNECTION = "Server=127.0.0.1;Port=$port;User ID=root;Password=$password;SslMode=None;Allow User Variables=True"
    & dotnet test (Join-Path $PSScriptRoot 'WFE.Test.csproj') -m:1 -nr:false -v:q -nologo --logger 'trx;LogFileName=protocol-regressions.trx' --results-directory $runRoot > (Join-Path $runRoot 'tests.log') 2>&1
    $testExit = $LASTEXITCODE
    if ($testExit -eq 0) {
        [xml] $trx = Get-Content -LiteralPath (Join-Path $runRoot 'protocol-regressions.trx') -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        Write-Output "Tests: $($counts.passed) passed, $($counts.failed) failed, $($counts.notExecuted) skipped."
        if ([int]$counts.notExecuted -gt 0) { throw 'Complete integration run unexpectedly skipped tests.' }
    } else {
        Write-Output "Tests failed. See tests.log in $runRoot."
        Get-Content -LiteralPath (Join-Path $runRoot 'tests.log') -Tail 30
    }
} finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        & $adminExe --protocol=tcp --host=127.0.0.1 "--port=$port" --user=root --connect-timeout=2 shutdown *> $null
        if (-not $serverProcess.WaitForExit(10000)) { Stop-Process -Id $serverProcess.Id -Force }
    }
    $env:HALEYFLOW_TEST_CONNECTION = $priorConnection
    $env:MYSQL_PWD = $priorPassword
}
exit $testExit
