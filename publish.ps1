<#
  Builds a self-contained, single-file StorageIt.exe (no .NET install needed on the target PC).
  Usage:  .\publish.ps1            -> artifacts\StorageIt.exe for 64-bit Windows
          .\publish.ps1 -Arch arm64
#>
param([ValidateSet('x64', 'arm64')][string]$Arch = 'x64')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts'

dotnet publish "$root\StorageIt\StorageIt.csproj" -c Release -r "win-$Arch" --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $out 'StorageIt.exe'
"Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
