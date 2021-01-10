$version = $args[0]
Write-Host "Set version: $version"

$FullPath = Resolve-Path $PSScriptRoot\source.extension.vsixmanifest
Write-Host $FullPath
[xml]$content = Get-Content $FullPath
$content.PackageManifest.Metadata.Identity.Version = $version
$content.Save($FullPath)

Out-File -FilePath sql4cds-version.txt -InputObject $version