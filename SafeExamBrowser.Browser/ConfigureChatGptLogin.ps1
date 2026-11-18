$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$provider = (Read-Host 'Login provider: openai or google [openai]').Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($provider)) { $provider = 'openai' }
if ($provider -notin @('openai', 'google')) { throw 'Choose openai or google.' }
$email = (Read-Host 'ChatGPT email').Trim()
if ([string]::IsNullOrWhiteSpace($email)) { throw 'Email is required.' }
$securePassword = Read-Host "$provider password" -AsSecureString
if ($securePassword.Length -eq 0) { throw 'Password is required.' }

$loginFolder = Join-Path $env:LOCALAPPDATA 'SafeExamBrowser\ChatGpt'
$loginPath = Join-Path $loginFolder 'credentials.dat'
$temporaryPath = Join-Path $loginFolder ([guid]::NewGuid().ToString('N') + '.tmp')
$data = $null
$login = $null
try {
    $login = @{
        Email = $email
        Provider = $provider
        Password = [System.Net.NetworkCredential]::new('', $securePassword).Password
    }
    $data = [System.Text.Encoding]::UTF8.GetBytes(($login | ConvertTo-Json -Compress))
    $encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
        $data, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    New-Item -ItemType Directory -Path $loginFolder -Force | Out-Null
    [System.IO.File]::WriteAllBytes($temporaryPath, $encrypted)
    if ([System.IO.File]::Exists($loginPath)) {
        [System.IO.File]::Replace($temporaryPath, $loginPath, $null)
    } else {
        [System.IO.File]::Move($temporaryPath, $loginPath)
    }
    Write-Host 'ChatGPT login saved for this Windows user. Press Ctrl + Shift + U in SEB to use it.'
} finally {
    if ($data) { [Array]::Clear($data, 0, $data.Length) }
    if ($login) { $login.Password = $null }
    $securePassword.Dispose()
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath }
}
