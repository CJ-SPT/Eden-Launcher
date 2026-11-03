[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot 'artifacts/publish/win-x64'
$archive = Join-Path $repositoryRoot 'artifacts/Eden-Launcher-win-x64.zip'
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet publish src/Eden.Launcher/Eden.Launcher.csproj -c Release -r win-x64 --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) {
        throw 'Launcher publishing failed.'
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination $output -Recurse -Force
    $files = Get-ChildItem -LiteralPath $output | Select-Object -ExpandProperty FullName
    Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
    Write-Output $archive
}
finally {
    Pop-Location
}
