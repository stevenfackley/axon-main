#Requires -Version 7
<#
.SYNOPSIS
  PostToolUse — runs `dotnet format` on edited .NET source files. Scoped to the nearest .csproj.
#>
$ErrorActionPreference = 'SilentlyContinue'

try { $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { exit 0 }

$path = $payload.tool_input.file_path
if (-not $path) { exit 0 }
if ($path -notmatch '\.(cs|csproj|fsproj|vbproj|razor|cshtml)$') { exit 0 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { exit 0 }
if (-not (Test-Path -LiteralPath $path)) { exit 0 }

$dir = Split-Path -Parent $path
while ($dir -and (Test-Path $dir)) {
  $proj = Get-ChildItem -LiteralPath $dir -Filter *.csproj -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($proj) {
    & dotnet format $proj.FullName --include $path --no-restore 2>&1 | Out-Null
    break
  }
  $parent = Split-Path -Parent $dir
  if ($parent -eq $dir) { break }
  $dir = $parent
}

exit 0
