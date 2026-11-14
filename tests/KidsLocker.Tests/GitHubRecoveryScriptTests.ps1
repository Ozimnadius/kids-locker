param([string]$Source)
$ErrorActionPreference = 'Stop'
. $Source
$root = Join-Path ([IO.Path]::GetTempPath()) ('kl-ps-' + [Guid]::NewGuid().ToString('N'))
$script:bytes = [Text.Encoding]::UTF8.GetBytes('проверенный exe')
$sha = [Security.Cryptography.SHA256]::Create()
$script:hash = ([BitConverter]::ToString($sha.ComputeHash($script:bytes))).Replace('-', '')
$sha.Dispose()
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
try {
    foreach ($url in @('http://github.com/file', 'https://github.com.evil/file', 'https://evil.github.com/file', 'https://github.com:8443/file', 'https://user@github.com/file')) {
        Check (-not (Test-KLDownloadUri ([Uri]$url))) 'Принят посторонний адрес'
    }
    foreach ($url in @('https://github.com/file', 'https://release-assets.githubusercontent.com/file', 'https://objects.githubusercontent.com/file')) {
        Check (Test-KLDownloadUri ([Uri]$url)) 'Отклонён адрес GitHub'
    }
    $gate = Enter-KLMutex ('Local\KidsLocker-ScriptTest-' + [Guid]::NewGuid().ToString('N'))
    Check ($null -ne $gate) 'Не создана блокировка PowerShell'
    Exit-KLMutex $gate
    foreach ($case in @('healthy', 'local', 'main_only', 'network', 'bad_hash', 'offline', 'uninstall', 'update', 'invalid_version', 'disabled', 'other_version')) {
        $dir = Join-Path $root $case
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        $installed = Join-Path $dir 'main.exe'
        $backup = Join-Path $dir 'KidsLocker.Recovery.exe'
        $manifest = Join-Path $dir 'enabled.sha256'
        $metadata = Join-Path $dir 'github.json'
        [IO.File]::WriteAllText($manifest, $script:hash)
        [IO.File]::WriteAllText($metadata, '{"Version":"0.5.0"}')
        if ($case -in @('healthy','local')) { [IO.File]::WriteAllBytes($backup, $script:bytes) }
        if ($case -in @('healthy','main_only')) { [IO.File]::WriteAllBytes($installed, $script:bytes) }
        if ($case -eq 'other_version') { [IO.File]::WriteAllText($installed, 'другая версия') }
        if ($case -eq 'invalid_version') { [IO.File]::WriteAllText($metadata, '{"Version":"../evil"}') }
        if ($case -eq 'disabled') { [IO.File]::Delete($manifest) }
        $script:downloads = 0; $script:launches = 0; $failed = $false
        $download = {
            param($url, $destination)
            $script:downloads++
            Check ($url -eq 'https://github.com/Ozimnadius/kids-locker/releases/download/v0.5.0/KidsLocker.exe') 'Адрес не закреплён'
            if ($case -eq 'offline') { throw 'Нет сети' }
            if ($case -eq 'uninstall') { [IO.File]::Delete($manifest) }
            if ($case -eq 'update') { [IO.File]::WriteAllText($metadata, '{"Version":"0.6.0"}') }
            if ($case -eq 'bad_hash') { [IO.File]::WriteAllText($destination, 'подмена') }
            else { [IO.File]::WriteAllBytes($destination, $script:bytes) }
        }
        $launch = {
            param($exe, $expected)
            Check ([IO.File]::Exists($exe)) 'Нет временного exe'
            Check ($expected -eq $script:hash) 'Изменён доверенный хеш'
            $script:launches++
        }
        try { Invoke-KLRecovery $dir $installed $download $launch { New-Object object } { param($gate) } | Out-Null }
        catch { $failed = $true }
        $expectedLaunches = if ($case -in @('local','main_only','network')) { 1 } else { 0 }
        Check ($script:launches -eq $expectedLaunches) "Неверный запуск: $case"
        Check ($failed -eq ($case -in @('bad_hash','offline','invalid_version'))) "Неверный результат: $case"
        if ($case -in @('healthy','local','main_only','disabled','invalid_version','other_version')) { Check ($script:downloads -eq 0) "Лишняя сеть: $case" }
        Check (([IO.Directory]::GetFiles($dir, '.github-*.exe')).Length -eq 0) "Остался временный файл: $case"
    }
    Write-Output 'Все сценарии загрузчика прошли'
} finally {
    # Каталог создан тестом с уникальным именем в Temp.
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
}
