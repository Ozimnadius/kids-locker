# Независимый загрузчик. Точки подстановки используются только тестами при dot-source.
$ErrorActionPreference = 'Stop'

function Read-KLState([string]$Directory) {
    $manifest = Join-Path $Directory 'enabled.sha256'
    if (-not [IO.File]::Exists($manifest)) { return $null }
    $hash = [IO.File]::ReadAllText($manifest).Trim().ToUpperInvariant()
    if ($hash -notmatch '^[0-9A-F]{64}$') { throw 'Повреждён сохранённый SHA-256' }
    $metadata = [IO.File]::ReadAllText((Join-Path $Directory 'github.json')) | ConvertFrom-Json
    $version = [string]$metadata.Version
    if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z') { throw 'Некорректная версия релиза' }
    return [pscustomobject]@{ Hash = $hash; Version = $version }
}

function Test-KLFile([string]$Path, [string]$Hash) {
    if (-not [IO.File]::Exists($Path)) { return $false }
    $stream = $null; $sha = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        if ($stream.Length -gt 125829120) { return $false }
        $sha = [Security.Cryptography.SHA256]::Create()
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') -eq $Hash
    } catch { return $false }
    finally { if ($stream) { $stream.Dispose() }; if ($sha) { $sha.Dispose() } }
}

function Enter-KLMutex([string]$Name) {
    $security = New-Object Security.AccessControl.MutexSecurity
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.MutexAccessRule($identity, 'FullControl', 'Allow')
        $security.AddAccessRule($rule)
    }
    $created = $false
    $mutex = New-Object Threading.Mutex($false, $Name, [ref]$created, $security)
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if ($held) { return $mutex }
        $mutex.Dispose(); return $null
    } catch { $mutex.Dispose(); throw }
}

function Exit-KLMutex($Mutex) { $Mutex.ReleaseMutex(); $Mutex.Dispose() }

function Test-KLDownloadUri([Uri]$Uri) {
    return $Uri.IsAbsoluteUri -and $Uri.Scheme -eq 'https' -and $Uri.Port -eq 443 -and -not $Uri.UserInfo -and
        $Uri.Host -in @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')
}

function Receive-KLAsset([string]$Url, [string]$Destination) {
    Add-Type -AssemblyName System.Net.Http
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false; $handler.UseProxy = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.MaxResponseContentBufferSize = 125829120
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $budget = New-Object Threading.CancellationTokenSource
    $budget.CancelAfter(60000)
    try {
        $uri = [Uri]$Url
        for ($redirect = 0; $redirect -lt 6; $redirect++) {
            if (-not (Test-KLDownloadUri $uri)) {
                throw 'Недопустимый адрес перенаправления GitHub'
            }
            # Обычный GetAsync буферизует тело: общий токен ограничивает и заголовки, и загрузку.
            $response = $client.GetAsync($uri, $budget.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    if (-not $response.Headers.Location) { throw 'Нет адреса перенаправления' }
                    $uri = New-Object Uri($uri, $response.Headers.Location)
                    continue
                }
                $response.EnsureSuccessStatusCode() | Out-Null
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                [IO.File]::WriteAllBytes($Destination, $bytes)
                return
            } finally { $response.Dispose() }
        }
        throw 'Слишком много перенаправлений GitHub'
    } finally { $budget.Dispose(); $client.Dispose() }
}

function Start-KLRepair([string]$Exe, [string]$Hash) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.Arguments = '--repair-from-github ' + $Hash
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($start)
    try {
        # Не прерываем установщик посреди изменения служб, если он задержался.
        if (-not $process.WaitForExit(180000)) { throw 'Восстановление ещё выполняется' }
        if ($process.ExitCode -ne 0) { throw 'Установщик сообщил об ошибке восстановления' }
    } finally { $process.Dispose() }
}

function Invoke-KLRecovery {
    param([string]$RecoveryDirectory, [string]$InstalledPath, [scriptblock]$Download,
          [scriptblock]$Launch, [scriptblock]$Enter, [scriptblock]$Leave)
    $stage = $null; $state = $null; $local = $false
    try {
        $gate = & $Enter
        if ($null -eq $gate) { return }
        try {
            $state = Read-KLState $RecoveryDirectory
            if ($null -eq $state) { return }
            $backup = Join-Path $RecoveryDirectory 'KidsLocker.Recovery.exe'
            $mainValid = Test-KLFile $InstalledPath $state.Hash
            $backupValid = Test-KLFile $backup $state.Hash
            if ($mainValid -and $backupValid) { return }
            # Существующий другой exe не откатываем старой копией или старым релизом.
            if ([IO.File]::Exists($InstalledPath) -and -not $mainValid) { return }
            $stage = Join-Path $RecoveryDirectory ('.github-' + [Guid]::NewGuid().ToString('N') + '.exe')
            if ($mainValid -or $backupValid) {
                $source = if ($mainValid) { $InstalledPath } else { $backup }
                [IO.File]::Copy($source, $stage)
                $local = $true
            }
        } finally { & $Leave $gate }

        if (-not $local) {
            $url = 'https://github.com/Ozimnadius/kids-locker/releases/download/v' + $state.Version + '/KidsLocker.exe'
            & $Download $url $stage
        }
        if (-not (Test-KLFile $stage $state.Hash)) { throw 'Скачанный файл не совпал с сохранённым SHA-256' }

        $gate = & $Enter
        if ($null -eq $gate) { return }
        try {
            $current = Read-KLState $RecoveryDirectory
            if ($null -eq $current -or $current.Hash -ne $state.Hash -or $current.Version -ne $state.Version) { return }
        } finally { & $Leave $gate }
        # Установщик повторяет проверку под mutex: удаление или обновление в этом окне безопасны.
        & $Launch $stage $state.Hash
    } finally { if ($stage) { try { [IO.File]::Delete($stage) } catch { } } }
}

if ($MyInvocation.InvocationName -ne '.') {
    $networkGate = $null
    try {
        $networkGate = Enter-KLMutex 'Global\KidsLocker.GitHubRecovery.v1'
        if ($null -eq $networkGate) { exit 0 }
        $installed = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'KidsLocker\KidsLocker.exe'
        Invoke-KLRecovery $PSScriptRoot $installed ${function:Receive-KLAsset} ${function:Start-KLRepair} `
            { Enter-KLMutex 'Global\KidsLocker.Installation.v1' } { param($gate) Exit-KLMutex $gate }
    } catch {
        # Не создаём каталоги: после удаления программы журнал не должен возвращать папку данных.
        try {
            if ([IO.File]::Exists((Join-Path $PSScriptRoot 'enabled.sha256'))) {
                $log = Join-Path $PSScriptRoot 'github.log'
                if ([IO.File]::Exists($log) -and (Get-Item -LiteralPath $log).Length -gt 131072) { [IO.File]::Delete($log) }
                [IO.File]::AppendAllText($log, [DateTimeOffset]::UtcNow.ToString('O') + ' ' + $_.Exception.Message + [Environment]::NewLine)
            }
        } catch { }
        exit 1
    } finally { if ($networkGate) { Exit-KLMutex $networkGate } }
}
