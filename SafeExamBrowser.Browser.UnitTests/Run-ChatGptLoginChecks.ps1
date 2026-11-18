param([string]$RuntimeDirectory, [string]$DesktopDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($RuntimeDirectory)) { $RuntimeDirectory = Join-Path $repoRoot 'SafeExamBrowser.Runtime\bin\x64\Debug' }
$runtimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$testDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'bin\x64\Debug')).Path
if ([string]::IsNullOrEmpty($DesktopDirectory)) { $DesktopDirectory = Join-Path $repoRoot 'SafeExamBrowser.UserInterface.Desktop\bin\x64\Debug' }
$desktopDirectory = (Resolve-Path -LiteralPath $DesktopDirectory).Path
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDirectory = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$references = @('CefSharp', 'CefSharp.Core', 'CefSharp.OffScreen') | ForEach-Object {
    '/r:' + (Join-Path $runtimeDirectory "${_}.dll")
}
$references += @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml') | ForEach-Object {
    '/r:' + (Join-Path $frameworkDirectory "${_}.dll")
}

Push-Location -LiteralPath $repoRoot
try {
    foreach ($fixtureName in @('ChatGptLoginSmoke', 'ChatGptManualLoginSmoke', 'ChatGptResponseSmoke')) {
        $executablePath = Join-Path $testDirectory "${fixtureName}.exe"
        $sourcePath = Join-Path $PSScriptRoot "Fixtures\${fixtureName}.cs"
        & $compilerPath /nologo /target:exe /platform:x64 "/out:$executablePath" @references '/r:System.Drawing.dll' $sourcePath
        if ($LASTEXITCODE -ne 0) { throw "Could not compile $fixtureName." }
        & $executablePath $runtimeDirectory
        if ($LASTEXITCODE -ne 0) { throw "$fixtureName failed." }
    }
    $layoutReferences = @('Moq', 'System.Threading.Tasks.Extensions', 'SafeExamBrowser.I18n.Contracts', 'SafeExamBrowser.Logging.Contracts',
        'SafeExamBrowser.Settings', 'SafeExamBrowser.UserInterface.Contracts') | ForEach-Object {
        '/r:' + (Join-Path $testDirectory "${_}.dll")
    }
    $executablePath = Join-Path $testDirectory 'ChatGptLayoutSmoke.exe'
    & $compilerPath /nologo /target:exe /platform:x64 "/out:$executablePath" @references @layoutReferences `
        '/r:System.Windows.Forms.dll' (Join-Path $PSScriptRoot 'Fixtures\ChatGptLayoutSmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile ChatGptLayoutSmoke.' }
    & $executablePath $desktopDirectory
    if ($LASTEXITCODE -ne 0) { throw 'ChatGptLayoutSmoke failed.' }
} finally { Pop-Location }
