# Builds a single AIUsageCounter.exe into .\publish
#   .\publish.ps1              self-contained: runs on any Windows 10/11 x64, no .NET needed (~70 MB)
#   .\publish.ps1 -Small       framework-dependent: needs the .NET 8 Desktop Runtime (~0.3 MB)
param([switch]$Small)

$ErrorActionPreference = 'Stop'
$selfContained = if ($Small) { 'false' } else { 'true' }

dotnet test "$PSScriptRoot\AIUsageCounter.sln"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

dotnet publish "$PSScriptRoot\src\AIUsageCounter\AIUsageCounter.csproj" -c Release -r win-x64 `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$selfContained `
    -o "$PSScriptRoot\publish"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Get-Item "$PSScriptRoot\publish\AIUsageCounter.exe" | Select-Object FullName, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 1) } }
