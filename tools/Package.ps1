param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\bin'),
    [string]$DestinationPath = (Join-Path $PSScriptRoot '..\JeekWindowsOptimizer.zip')
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $SourcePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'JeekWindowsOptimizerMcp.exe') -PathType Leaf)) {
    throw 'Published package is missing JeekWindowsOptimizerMcp.exe'
}

Add-Type -AssemblyName System.IO.Compression.ZipFile
$archive = [System.IO.Compression.ZipArchive]::new(
    [System.IO.File]::Create($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DestinationPath)),
    [System.IO.Compression.ZipArchiveMode]::Create
)
try {
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
        if ($relativePath.StartsWith('Tools/Activator/', [StringComparison]::OrdinalIgnoreCase) -or $file.Extension -ieq '.pdb') {
            continue
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $relativePath, [System.IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
