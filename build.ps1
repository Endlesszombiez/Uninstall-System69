[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$Wix = 'wix',
    [switch]$SkipTests,
    [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$artifactsPath = Join-Path $repoRoot 'artifacts'
$publishPath = Join-Path $artifactsPath 'publish'
New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null
function Assert-Exit([string]$Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE" }
}
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & $Dotnet run --project tests/System69.Tests -c Release
        Assert-Exit 'Safety tests'
    }
    & $Dotnet publish src/System69.App -c Release -r win-x64 --self-contained true -o $publishPath --source https://api.nuget.org/v3/index.json
    Assert-Exit 'Publish'
    if ($SmokeTest) {
        $process = Start-Process -FilePath (Join-Path $publishPath 'System69.exe') -ArgumentList '--smoke-test' -WorkingDirectory $repoRoot -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Desktop smoke test failed. See artifacts/smoke-error.txt' }
    }
    $xmlSettings = [System.Xml.XmlWriterSettings]::new()
    $xmlSettings.Indent = $true
    $xmlSettings.NewLineChars = "`r`n"
    $xmlSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create((Join-Path $artifactsPath 'PublishedFiles.wxs'), $xmlSettings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs')
        $writer.WriteStartElement('Fragment')
        $writer.WriteStartElement('ComponentGroup')
        $writer.WriteAttributeString('Id', 'PublishedFiles')
        $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')
        $index = 0
        foreach ($file in (Get-ChildItem -LiteralPath $publishPath -File -Recurse | Sort-Object FullName)) {
            if ($file.Extension -eq '.pdb') { continue }
            $index++
            $relative = [System.IO.Path]::GetRelativePath($publishPath, $file.FullName)
            $writer.WriteStartElement('Component')
            $writer.WriteAttributeString('Id', "Published$index")
            $writer.WriteAttributeString('Guid', '*')
            $subdirectory = [System.IO.Path]::GetDirectoryName($relative)
            if ($subdirectory) { $writer.WriteAttributeString('Subdirectory', $subdirectory) }
            $writer.WriteStartElement('File')
            $writer.WriteAttributeString('Id', "File$index")
            $writer.WriteAttributeString('Source', $file.FullName)
            $writer.WriteAttributeString('KeyPath', 'yes')
            $writer.WriteEndElement()
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally { $writer.Dispose() }
    & $Wix build installer/Package.wxs (Join-Path $artifactsPath 'PublishedFiles.wxs') -arch x64 -d "RepoRoot=$repoRoot" -o (Join-Path $artifactsPath 'System69-0.1.0-win-x64.msi')
    Assert-Exit 'MSI packaging'
    $zipPath = Join-Path $artifactsPath 'System69-0.1.0-win-x64-portable.zip'
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $artifactsPath 'System69-0.1.0-win-x64.msi'), $zipPath |
        Select-Object Algorithm, Hash, @{Name='File';Expression={[System.IO.Path]::GetFileName($_.Path)}} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactsPath 'checksums.json') -Encoding utf8
    Write-Host "Release ready: $artifactsPath"
}
finally { Pop-Location }
