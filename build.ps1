# Builds Kova into bin\<arch>, even while it is running.
#
#   build.bat              quick: this PC's own arch only, no trimming or compression (seconds)
#   build.bat arm64 / x64  quick, that arch
#   build.bat full         both architectures, exactly as a release is built
#
# A running exe cannot be overwritten, but it can be moved: the running copy is
# moved to %LOCALAPPDATA%\Kova\old (emptied right after), the new one is
# copied into its place, the old copy is closed (it saves whether it was
# hosting or connected) and the new one starts with --resume to carry on.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args2)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$full = $Args2 -contains 'full'
$Arch = @($Args2 | Where-Object { $_ -in 'arm64', 'x64' })
$cpu = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$native = if ($cpu -eq 'ARM64') { 'arm64' } else { 'x64' }
if ($Arch.Count -eq 0) { $Arch = if ($full) { @('arm64', 'x64') } else { @($native) } }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$oldDir = Join-Path $env:LOCALAPPDATA 'Kova\old'
$quick = if ($full) { @() } else { @('-p:PublishTrimmed=false', '-p:EnableCompressionInSingleFile=false') }

foreach ($a in $Arch) {
    $pub = "obj\publish\$a"
    & $dotnet publish kova.csproj -c Release -r "win-$a" -o $pub -p:BaseOutputPath=obj\pubout\ --nologo -v:q @quick
    if ($LASTEXITCODE) { exit $LASTEXITCODE }

    $dest = Join-Path $PSScriptRoot "bin\$a"
    $exe = Join-Path $dest 'Kova.exe'
    New-Item -ItemType Directory -Force $dest | Out-Null

    $running = @(Get-Process Kova -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if (Test-Path $exe) {
        New-Item -ItemType Directory -Force $oldDir | Out-Null
        Move-Item $exe (Join-Path $oldDir ("Kova-$a-" + [DateTime]::Now.Ticks + '.exe'))
    }
    Copy-Item "$pub\Kova.exe" $exe

    foreach ($p in $running) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(5000)) { $p.Kill(); $p.WaitForExit() }
    }
    if ($running.Count -gt 0) {
        Start-Process $exe -ArgumentList '--resume'
        "$a built, installed and restarted: $exe"
    }
    else { "$a built and installed: $exe" }

    # The replaced copy has exited by now: delete it, and the folders once empty.
    if (Test-Path $oldDir) {
        Get-ChildItem $oldDir -File | ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }
        if (-not (Get-ChildItem $oldDir)) { Remove-Item $oldDir -Force }
        $parent = Split-Path $oldDir
        if ((Test-Path $parent) -and -not (Get-ChildItem $parent)) { Remove-Item $parent -Force }
    }
}
