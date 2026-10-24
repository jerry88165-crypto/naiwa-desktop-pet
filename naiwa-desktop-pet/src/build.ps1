param(
    [Parameter(Mandatory=$true)][string]$AssetsDir,
    [Parameter(Mandatory=$true)][string]$OutputExe
)
$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$assetRoot = (Resolve-Path -LiteralPath $AssetsDir).Path
$targetPath = [System.IO.Path]::GetFullPath($OutputExe)
$targetParent = Split-Path -Parent $targetPath
New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
$frameworkRoot = Split-Path -Parent $compiler
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\System.Xaml.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',('/out:' + $targetPath))
foreach ($reference in $references) {
    $referencePath = Join-Path $frameworkRoot $reference
    if (-not (Test-Path -LiteralPath $referencePath)) {
        $referencePath = Join-Path $frameworkRoot (Split-Path -Leaf $reference)
    }
    $arguments += ('/reference:' + $referencePath)
}
for ($pose=1; $pose -le 8; $pose++) {
    $name = 'pose' + $pose + '.png'
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path)) { throw ('Missing sprite: ' + $path) }
    $arguments += ('/resource:' + $path + ',Naiwa.Assets.' + $name)
}
$hasLaugh = $false
foreach ($name in @('laugh.png','laugh_strip.png')) {
    $path = Join-Path $assetRoot $name
    if (Test-Path -LiteralPath $path) { $arguments += ('/resource:' + $path + ',Naiwa.Assets.' + $name); $hasLaugh = $true }
}
$laughFramesFolder = Join-Path $assetRoot 'laugh_frames'
if (Test-Path -LiteralPath $laughFramesFolder) {
    $laughFrames = @(Get-ChildItem -LiteralPath $laughFramesFolder -File -Filter 'frame*.png' | Sort-Object Name)
    foreach ($frame in $laughFrames) {
        $arguments += ('/resource:' + $frame.FullName + ',Naiwa.Assets.laugh_frames.' + $frame.Name)
        $hasLaugh = $true
    }
}
foreach ($metadata in @('laugh_animation.txt','laugh_frames\animation.txt')) {
    $metadataPath = Join-Path $assetRoot $metadata
    if (Test-Path -LiteralPath $metadataPath) {
        $resourceName = 'Naiwa.Assets.' + $metadata.Replace('\','.')
        $arguments += ('/resource:' + $metadataPath + ',' + $resourceName)
    }
}
$soundPath = Join-Path $assetRoot 'laugh_sound.mp4'
if (Test-Path -LiteralPath $soundPath) { $arguments += ('/resource:' + $soundPath + ',Naiwa.Assets.laugh_sound.mp4') }
if (-not $hasLaugh) { throw 'A laugh_frames frame sequence, laugh.png, or laugh_strip.png is required.' }
$arguments += (Join-Path $PSScriptRoot 'Naiwa.cs')
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw ('Build failed with exit code ' + $LASTEXITCODE) }
Get-Item -LiteralPath $targetPath | Select-Object FullName,Length,LastWriteTime
