param([switch]$Run, [switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$taskOutput = Join-Path $taskRoot 'bin'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskReferences = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$taskArgs = @('/nologo','/target:winexe','/optimize+','/warn:4',('/out:' + (Join-Path $taskOutput 'PasteHandwriting.exe')),('/resource:' + (Join-Path $taskRoot 'src\MainWindow.xaml') + ',MainWindow.xaml'))
foreach ($taskReference in $taskReferences) { $taskArgs += '/reference:' + (Join-Path $taskFramework $taskReference) }
$taskArgs += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $taskCompiler $taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\PasteHandwriting.exe.config') -Destination $taskOutput -Force
Write-Output ('Built ' + (Join-Path $taskOutput 'PasteHandwriting.exe'))
if ($Test) {
    $taskReport = Join-Path $taskRoot 'test-results.txt'
    $taskProcess = Start-Process -FilePath (Join-Path $taskOutput 'PasteHandwriting.exe') -ArgumentList '--self-test' -WorkingDirectory $taskRoot -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $taskReport
    if ($taskProcess.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
if ($Run) { Start-Process -FilePath (Join-Path $taskOutput 'PasteHandwriting.exe') -WorkingDirectory $taskRoot -WindowStyle Hidden }
