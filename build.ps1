param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$outputDirectory = Join-Path $taskRoot '首版'
dotnet build (Join-Path $taskRoot 'ScreenTime\ScreenTime.csproj') -c $Configuration -o $outputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw '编译失败，输出未作为可用版本发布。' }
Write-Output "可执行文件：$outputDirectory\ScreenTime.exe"
