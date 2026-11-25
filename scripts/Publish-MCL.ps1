param(
    [string] $RelayDotnet = 'dotnet',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$relaySdk = (Get-Command $RelayDotnet -CommandType Application).Source
$clientSdk = (Get-Command dotnet -CommandType Application).Source
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'outputs/MCL' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

# The desktop solution pins SDK 9. Give the public relay a separate SDK selection.
$stage = Join-Path $root 'work/publish-mcl'
$relaySource = Join-Path $stage 'src/MCL.Relay'
$sharedSource = Join-Path $stage 'src/Multiplayer'
$relayOutput = Join-Path $stage 'relay'
New-Item -ItemType Directory -Path $relaySource, $sharedSource -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src/MCL.Relay/MCL.Relay.csproj'), (Join-Path $root 'src/MCL.Relay/Program.cs') -Destination $relaySource
Copy-Item -LiteralPath (Join-Path $root 'src/Multiplayer/RoomProtocol.cs') -Destination $sharedSource
[IO.File]::WriteAllText((Join-Path $stage 'global.json'), '{"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}')

Push-Location -LiteralPath $stage
try {
    & $relaySdk publish src/MCL.Relay/MCL.Relay.csproj -c Release -r win-x64 --self-contained true `
        -p:MclPublicRelay=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $relayOutput
    if ($LASTEXITCODE -ne 0) { throw '发布中转程序失败。请确认 RelayDotnet 指向装有 .NET 10 SDK 的 dotnet.exe。' }
} finally { Pop-Location }

$bundle = Join-Path $relayOutput 'MCL.Relay.exe'
Push-Location -LiteralPath $root
try {
    & $clientSdk publish src/MCL/MCL.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:MclRelayBundle=$bundle" -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw '发布 MCL 失败。' }
} finally { Pop-Location }
