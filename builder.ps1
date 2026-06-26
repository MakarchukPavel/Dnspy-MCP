<#
    builder.ps1

    One-shot build for the whole repo.  Builds the small subset of dnSpy
    projects whose output DLLs the agent references, copies them into
    ./lib/, then builds dnspymcp + dnspymcpagent and stages a distributable
    layout under ./dist/.

    Usage (PowerShell 7+ recommended):
        pwsh -File builder.ps1
        pwsh -File builder.ps1 -Configuration Release -Zip
        pwsh -File builder.ps1 -SkipDnSpy            # lib/ already populated
        pwsh -File builder.ps1 -Clean

    Output layout:
        lib/                                 referenced DLLs for dnspymcpagent
        dist/dnspymcp/                       net9 MCP server (publish output)
        dist/dnspymcpagent/                  net48 agent   (build  output)
        dist/dnspymcp-<ver>-win-x64.zip      if -Zip (mcp server only)
        dist/dnspymcpagent-<ver>-win-x64.zip if -Zip (agent only, lib/ already inlined)
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipDnSpy,
    [switch]$Clean,
    [switch]$Zip,
    [string]$Version = $env:GITHUB_REF_NAME
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$libDir = Join-Path $root 'lib'
$distDir = Join-Path $root 'dist'

if ($Clean) {
    foreach ($d in @($libDir, $distDir)) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    Write-Host "==> cleaned"
    return
}

# ---------- 1. ensure dnspy submodule is checked out ------------------------
$dnspyDir = Join-Path $root 'dnspy'
if (-not (Test-Path (Join-Path $dnspyDir 'dnSpy.sln'))) {
    Write-Host "==> initialising dnspy submodule"
    git -C $root submodule update --init --recursive
}

# ---------- 2. build dnspy subset into ./lib/ -------------------------------
$corDebugProj  = Join-Path $dnspyDir 'Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug/dnSpy.Debugger.DotNet.CorDebug.csproj'
# dnSpy.Analyzer.x.dll carries the ScopedWhereUsedAnalyzer engine (cross-DLL
# where-used xref) used by reverse_xref_to_method. It's a WPF-built DLL but
# the analyzer classes themselves don't touch WPF types at runtime, so we
# reference it via Krafs.Publicizer (matching the CorDebug.x.dll pattern).
$analyzerProj  = Join-Path $dnspyDir 'Extensions/dnSpy.Analyzer/dnSpy.Analyzer.csproj'
$corDebugOutDir = Join-Path $dnspyDir "dnSpy/dnSpy/bin/$Configuration"

$needed = @(
    'dnSpy.Debugger.DotNet.CorDebug.x.dll',
    'dnSpy.Analyzer.x.dll',
    'dnSpy.Contracts.Debugger.DotNet.CorDebug.dll',
    'dnSpy.Contracts.Debugger.DotNet.dll',
    'dnSpy.Contracts.Debugger.dll',
    'dnSpy.Contracts.DnSpy.dll',
    'dnSpy.Contracts.Logic.dll',
    'dnSpy.Debugger.DotNet.Metadata.dll',
    'dnlib.dll'
)

function Copy-Libs {
    param([string]$From, [string]$To)
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($n in $needed) {
        $hit = Get-ChildItem -Path $From -Filter $n -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $hit) { throw "not found in dnspy build output: $n (looked under $From)" }
        Copy-Item $hit.FullName (Join-Path $To $n) -Force
        foreach ($ext in '.pdb','.xml') {
            $side = [IO.Path]::ChangeExtension($hit.FullName, $ext)
            if (Test-Path $side) { Copy-Item $side (Join-Path $To ([IO.Path]::GetFileName($side))) -Force }
        }
    }
}

if (-not $SkipDnSpy) {
    Write-Host "==> building dnspy CorDebug project ($Configuration)"
    & dotnet build $corDebugProj -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dnspy CorDebug build failed" }

    Write-Host "==> building dnspy Analyzer project ($Configuration)"
    & dotnet build $analyzerProj -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dnspy Analyzer build failed" }

    Write-Host "==> staging ./lib/"
    Copy-Libs -From $corDebugOutDir -To $libDir
}
elseif (-not (Test-Path $libDir)) {
    throw "-SkipDnSpy was passed but ./lib/ is missing. Run without -SkipDnSpy once."
}

# ---------- 3. build the two projects --------------------------------------
Write-Host "==> publishing dnspymcp (net9)"
& dotnet publish (Join-Path $root 'dnspymcp/dnspymcp.csproj') -c $Configuration --self-contained false -o (Join-Path $distDir 'dnspymcp')
if ($LASTEXITCODE -ne 0) { throw "dnspymcp build failed" }

# The debugger bitness must match the debuggee's, so we ship both an x64 and an
# x86 agent. x64 -> dist/dnspymcpagent (default), x86 -> dist/dnspymcpagent-x86.
# (lib/ is AnyCPU managed dnSpy, reused by both; only dbgshim is per-arch.)
Write-Host "==> building dnspymcpagent x64 (net48)"
& dotnet build (Join-Path $root 'dnspymcpagent/dnspymcpagent.csproj') -c $Configuration -p:PlatformTarget=x64 -o (Join-Path $distDir 'dnspymcpagent')
if ($LASTEXITCODE -ne 0) { throw "dnspymcpagent x64 build failed" }

Write-Host "==> building dnspymcpagent x86 (net48)"
& dotnet build (Join-Path $root 'dnspymcpagent/dnspymcpagent.csproj') -c $Configuration -p:PlatformTarget=x86 -o (Join-Path $distDir 'dnspymcpagent-x86')
if ($LASTEXITCODE -ne 0) { throw "dnspymcpagent x86 build failed" }

# dnspymcptest is the integration test target: an idle .NET process the test
# fixture spawns and the agent attaches to. Built unconditionally Debug so
# pytest always sees the latest type/method shape.
Write-Host "==> building dnspymcptest (net48 Debug)"
& dotnet build (Join-Path $root 'dnspymcptest/dnspymcptest.csproj') -c Debug -v minimal
if ($LASTEXITCODE -ne 0) { throw "dnspymcptest build failed" }

# ---------- 4. stage docs into each component -----------------------------
# Each zip is self-contained, so README + LICENSE land in both.
$docs = @('README.md','LICENSE') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ }
foreach ($sub in 'dnspymcp','dnspymcpagent') {
    foreach ($d in $docs) { Copy-Item $d (Join-Path $distDir $sub) -Force }
}

# ---------- 5. optional zips ------------------------------------------------
if ($Zip) {
    $tag = if ($Version) { $Version } else { "dev-" + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    foreach ($sub in 'dnspymcp','dnspymcpagent') {
        $zipPath = Join-Path $distDir "$sub-$tag-win-x64.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Write-Host "==> zipping -> $zipPath"
        Compress-Archive -Path (Join-Path $distDir "$sub/*") -DestinationPath $zipPath -Force
    }
}

Write-Host "==> done"
