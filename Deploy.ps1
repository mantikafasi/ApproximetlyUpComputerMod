param([string]$GamePath = $env:APPROXIMATELY_UP_GAME_PATH)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GamePath)) { $GamePath = 'G:\SteamLibrary\steamapps\common\Approximately Up' }
$plugins = Join-Path $GamePath 'BepInEx\plugins'
if (!(Test-Path -LiteralPath $plugins -PathType Container)) { throw 'Working BepInEx plugins directory missing.' }
if (Get-Process -Name ApproximatelyUp -ErrorAction SilentlyContinue) { throw 'Close the game before deploying.' }
dotnet build "$PSScriptRoot\src\ComputerMod.csproj" --configuration Release "-p:GamePath=$GamePath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing deployed.' }
$source = Join-Path $PSScriptRoot 'src\bin\Release\net6.0'
$destination = Join-Path $plugins 'ApproximatelyUpComputer'
$files = [ordered]@{
    'ApproximatelyUp.ComputerMod.dll' = Join-Path $source 'ApproximatelyUp.ComputerMod.dll'
    'MoonSharp.Interpreter.dll' = Join-Path $source 'MoonSharp.Interpreter.dll'
    'LICENSE' = Join-Path $PSScriptRoot 'LICENSE'
    'THIRD_PARTY_NOTICES.md' = Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md'
}
$assets = @('computer.mesh.json', 'computer-icon.png', 'computer-icon.rgba')
foreach ($name in $files.Keys) {
    if (!(Test-Path -LiteralPath $files[$name] -PathType Leaf)) { throw "Missing deployment file: $name" }
}
foreach ($name in $assets) {
    if (!(Test-Path -LiteralPath (Join-Path "$PSScriptRoot\assets" $name) -PathType Leaf)) { throw "Missing computer asset: $name" }
}
if (Get-Process -Name ApproximatelyUp -ErrorAction SilentlyContinue) { throw 'Game started during build; deployment cancelled.' }
[void][IO.Directory]::CreateDirectory($destination)
$backup = Join-Path $PSScriptRoot ('backups\plugin-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
foreach ($name in $files.Keys) {
    $target = Join-Path $destination $name
    if (Test-Path -LiteralPath $target) {
        [void][IO.Directory]::CreateDirectory($backup)
        Copy-Item -LiteralPath $target -Destination (Join-Path $backup $name)
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backup $name)).Hash) { throw 'Backup verification failed.' }
    }
    Copy-Item -LiteralPath $files[$name] -Destination $target
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $files[$name]).Hash) { throw "Deployment verification failed: $name" }
}
# Preserve the user's local script on every redeploy.
if (!(Test-Path -LiteralPath (Join-Path $destination 'computer.lua'))) {
    Copy-Item -LiteralPath "$PSScriptRoot\computer.lua" -Destination (Join-Path $destination 'computer.lua')
}
$assetDestination = Join-Path $destination 'assets'
[void][IO.Directory]::CreateDirectory($assetDestination)
foreach ($name in $assets) {
    $asset = Join-Path "$PSScriptRoot\assets" $name
    if (!(Test-Path -LiteralPath $asset)) { throw "Missing computer asset: $name" }
    $target = Join-Path $assetDestination $name
    if (Test-Path -LiteralPath $target) {
        [void][IO.Directory]::CreateDirectory("$backup\assets")
        Copy-Item -LiteralPath $target -Destination (Join-Path "$backup\assets" $name)
    }
    Copy-Item -LiteralPath $asset -Destination $target
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $asset).Hash) { throw "Asset copy mismatch: $name" }
}
"Deployed to $destination. Loader, patcher and saves were not modified."
