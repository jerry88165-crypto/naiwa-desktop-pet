param([string]$OutputExe)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputExe)) {
    $OutputExe = Join-Path $repoRoot 'dist\NaiwaDesktopPet.exe'
}
$targetExe = [System.IO.Path]::GetFullPath($OutputExe)
$frameArchive = Join-Path $repoRoot 'assets\laugh_frames.zip'
if (-not (Test-Path -LiteralPath $frameArchive)) { throw 'Missing assets\laugh_frames.zip.' }
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.build'))
$stageRoot = Join-Path $buildRoot ([Guid]::NewGuid().ToString('N'))
$stageAssets = Join-Path $stageRoot 'assets'
New-Item -ItemType Directory -Force -Path $stageAssets | Out-Null
try {
    for ($pose = 1; $pose -le 8; $pose++) {
        $name = 'pose' + $pose + '.png'
        Copy-Item -LiteralPath (Join-Path $repoRoot ('assets\' + $name)) -Destination (Join-Path $stageAssets $name)
    }
    Expand-Archive -LiteralPath $frameArchive -DestinationPath (Join-Path $stageAssets 'laugh_frames')
    $optionalSound = Join-Path $repoRoot 'assets\laugh_sound.mp4'
    if (Test-Path -LiteralPath $optionalSound) {
        Copy-Item -LiteralPath $optionalSound -Destination (Join-Path $stageAssets 'laugh_sound.mp4')
    }
    & (Join-Path $repoRoot 'src\build.ps1') -AssetsDir $stageAssets -OutputExe $targetExe
}
finally {
    # Remove only this invocation's checked staging directory under .build.
    $resolvedStage = [System.IO.Path]::GetFullPath($stageRoot)
    $allowedPrefix = $buildRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedStage.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
