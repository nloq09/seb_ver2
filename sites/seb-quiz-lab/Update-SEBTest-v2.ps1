$ErrorActionPreference = 'Stop'
$sharedDebug = '\\vmware-host\Shared Folders\Debug'
$sharedLab = '\\vmware-host\Shared Folders\lab'
$bundleRoot = Join-Path $sharedLab 'updates'
$manifestFile = if (Test-Path -LiteralPath $bundleRoot) {
    Get-ChildItem -LiteralPath $bundleRoot -Filter manifest.json -File -Recurse |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
}
if ($manifestFile) {
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or @($manifest.Files).Count -ne 3) { throw 'Invalid update manifest.' }
    $required = @('SafeExamBrowser.Browser.dll', 'SafeExamBrowser.UserInterface.Contracts.dll', 'SafeExamBrowser.UserInterface.Desktop.dll')
    $patches = @(foreach ($entry in $manifest.Files) {
        if ($entry.Name -notin $required -or $entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid update file.' }
        [PSCustomObject]@{ Name = $entry.Name; Source = (Join-Path $manifestFile.DirectoryName $entry.Name); Hash = $entry.Sha256 }
    })
    if (@($patches.Name | Select-Object -Unique).Count -ne 3) { throw 'Missing update file.' }
    $patchHash = (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash
} else {
    $latestPatch = Get-ChildItem -LiteralPath $sharedLab -Filter 'SafeExamBrowser.Browser*.update' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $latestPatch) { throw 'The updated Browser DLL is not available on the lab share.' }
    $patchHash = (Get-FileHash -LiteralPath $latestPatch.FullName -Algorithm SHA256).Hash
    $patches = @([PSCustomObject]@{ Name = 'SafeExamBrowser.Browser.dll'; Source = $latestPatch.FullName; Hash = $patchHash })
}
# Validate the complete bundle before stopping the current test instance.
foreach ($patch in $patches) {
    if ((Get-FileHash -LiteralPath $patch.Source -Algorithm SHA256).Hash -ne $patch.Hash) { throw ('Invalid update hash: ' + $patch.Name) }
}
$updateReport = Join-Path $sharedLab 'chatgpt-update-diagnostics.json'
@{ Updater = 2; Phase = 'Preparing'; PatchHash = $patchHash; Utc = [DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath $updateReport -Encoding UTF8
$localTestRoot = Join-Path $env:LOCALAPPDATA 'SEB-Test'
$destinationDirectory = Join-Path $localTestRoot ('Builds\' + $patchHash.Substring(0,12))
$oldDebug = 'C:\SEB-Test\Debug'

# Preserve credentials locally without printing their contents.
$savedEnvironment = Join-Path $localTestRoot 'login.env'
New-Item -ItemType Directory -Path $localTestRoot -Force | Out-Null
$runningTest = Get-Process -Name SafeExamBrowser,SafeExamBrowser.Client -ErrorAction SilentlyContinue | Where-Object {
    $_.MainWindowTitle -like '*SEB Lab*' -or
    ($_.Path -and ($_.Path.StartsWith($oldDebug + '\',[StringComparison]::OrdinalIgnoreCase) -or
                  $_.Path.StartsWith($localTestRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
                  $_.Path.StartsWith($sharedDebug + '\',[StringComparison]::OrdinalIgnoreCase)))
}
foreach ($testProcess in $runningTest) {
    if ($testProcess.Path) {
        $activeEnvironment = Join-Path (Split-Path -Parent $testProcess.Path) '.env'
        if (Test-Path -LiteralPath $activeEnvironment) { Copy-Item -LiteralPath $activeEnvironment -Destination $savedEnvironment -Force }
    }
}
if (-not (Test-Path -LiteralPath $savedEnvironment)) {
    $oldEnvironment = Join-Path $oldDebug '.env'
    if (Test-Path -LiteralPath $oldEnvironment) { Copy-Item -LiteralPath $oldEnvironment -Destination $savedEnvironment -Force }
}
$runningTest | Stop-Process -Force
foreach ($testProcess in $runningTest) { $testProcess.WaitForExit(10000) | Out-Null }
$remaining = @(Get-Process -Name SafeExamBrowser,SafeExamBrowser.Client -ErrorAction SilentlyContinue)
if ($remaining.Count -gt 0) {
    @{ Updater = 2; Phase = 'OldInstanceStillRunning'; RemainingProcessCount = $remaining.Count; PatchHash = $patchHash } |
        ConvertTo-Json | Set-Content -LiteralPath $updateReport -Encoding UTF8
    throw 'SEB is still running. Quit SEB inside the VM, then run this updater again. If access was denied, use an administrator PowerShell in the VM.'
}

# Use a fresh local build folder so HGFS or a previous process cannot retain an old DLL.
$excluded = @('.env', 'ChatGptDiagnostics.target') + @($patches.Name)
& robocopy $sharedDebug $destinationDirectory /E /R:1 /W:1 /XF @excluded
if ($LASTEXITCODE -ge 8) { throw 'Copy failed. Close the test SEB instance and retry.' }
foreach ($patch in $patches) {
    $destinationDll = Join-Path $destinationDirectory $patch.Name
    Copy-Item -LiteralPath $patch.Source -Destination $destinationDll -Force
    if ((Get-FileHash -LiteralPath $destinationDll -Algorithm SHA256).Hash -ne $patch.Hash) {
        throw ('The copied DLL does not match the update: ' + $patch.Name)
    }
}
if (Test-Path -LiteralPath $savedEnvironment) { Copy-Item -LiteralPath $savedEnvironment -Destination (Join-Path $destinationDirectory '.env') -Force }
Set-Content -LiteralPath (Join-Path $destinationDirectory 'ChatGptDiagnostics.target') -Value (Join-Path $sharedLab 'chatgpt-reply-diagnostics.json') -Encoding UTF8
Write-Host ('Verified build: ' + $patchHash.Substring(0,12))
@{ Updater = 2; Phase = 'CopyVerified'; PatchHash = $patchHash; Utc = [DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath $updateReport -Encoding UTF8
Start-Process -FilePath (Join-Path $destinationDirectory 'SafeExamBrowser.exe') -ArgumentList 'C:\SEB-Test\lab\lab-vm.seb' -WindowStyle Normal
