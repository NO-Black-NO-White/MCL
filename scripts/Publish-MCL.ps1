param(
    [string] $RelayDotnet = 'dotnet',
    [string] $OutputDirectory,
    [string] $CloudflaredPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$relaySdk = (Get-Command $RelayDotnet -CommandType Application).Source
$clientSdk = (Get-Command dotnet -CommandType Application).Source
$publishDefault = !$OutputDirectory
if ($publishDefault) {
    $project = [xml](Get-Content -LiteralPath (Join-Path $root 'src/MCL/MCL.csproj') -Raw)
    $version = $project.Project.PropertyGroup.Version
    $OutputDirectory = Join-Path $root "outputs/MCL-$version"
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

# The desktop solution pins SDK 9. Give the public relay a separate SDK selection.
$stage = Join-Path $root 'work/publish-mcl'
$relaySource = Join-Path $stage 'src/MCL.Relay'
$sharedSource = Join-Path $stage 'src/Multiplayer'
$relayOutput = Join-Path $stage 'relay'
New-Item -ItemType Directory -Path $relaySource, $sharedSource -Force | Out-Null
$cloudflaredVersion = '2026.11.2'
$cloudflaredHash = '555B46C85582461D189CAC30D14AD05480D612613801BE9F110B8CE08C5B2113'
$cloudflaredBundle = Join-Path $stage 'cloudflared.exe'
if ($CloudflaredPath) {
    $cloudflaredBundle = (Resolve-Path -LiteralPath $CloudflaredPath).Path
} elseif (!(Test-Path -LiteralPath $cloudflaredBundle -PathType Leaf)) {
    $cached = Join-Path $env:LOCALAPPDATA "MCL/multiplayer/cloudflared/$cloudflaredVersion/cloudflared.exe"
    if ((Test-Path -LiteralPath $cached -PathType Leaf) -and (Get-FileHash -LiteralPath $cached).Hash -eq $cloudflaredHash) {
        $cloudflaredBundle = $cached
    } else {
        $temporary = "$cloudflaredBundle.$([Guid]::NewGuid().ToString('N')).part"
        try {
            $url = "https://github.com/cloudflare/cloudflared/releases/download/$cloudflaredVersion/cloudflared-windows-amd64.exe"
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temporary
            if ((Get-FileHash -LiteralPath $temporary).Hash -ne $cloudflaredHash) { throw '下载的 cloudflared 校验失败。' }
            Move-Item -LiteralPath $temporary -Destination $cloudflaredBundle -Force
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
}
if ((Get-FileHash -LiteralPath $cloudflaredBundle).Hash -ne $cloudflaredHash) {
    throw "cloudflared 校验失败。请用 -CloudflaredPath 指定 Cloudflare 官方发布的 Windows x64 $cloudflaredVersion 文件。"
}
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
        -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:MclRelayBundle=$bundle" `
        "-p:MclCloudflaredBundle=$cloudflaredBundle" -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw '发布 MCL 失败。' }
} finally { Pop-Location }
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'licenses/cloudflared-LICENSE.txt') -Destination $licenses

if ($publishDefault) {
    $shortcutPath = Join-Path $root '启动 MCL.lnk'
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $OutputDirectory 'MCL.exe'
        $shortcut.WorkingDirectory = $OutputDirectory
        $shortcut.IconLocation = (Join-Path $root 'src/MCL/Assets/mcl.ico') + ',0'
        $shortcut.Save()
    }
}
