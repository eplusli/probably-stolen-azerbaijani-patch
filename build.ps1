param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'dist\ProbablyStolen-Azerbaijani-Setup-v1.0.0.exe')
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler was not found.' }
$source = Join-Path $PSScriptRoot 'src\Installer.cs'
$manifest = Join-Path $PSScriptRoot 'patch\manifest.json'
$payload = Join-Path $PSScriptRoot 'patch\payload.bin'
foreach ($inputFile in @($source, $manifest, $payload)) {
    if (!(Test-Path -LiteralPath $inputFile)) { throw "Required build input is missing: $inputFile" }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$resourceFolder = Join-Path $PSScriptRoot 'obj'
[IO.Directory]::CreateDirectory($resourceFolder) | Out-Null
function Compress-Resource([string]$Source, [string]$Destination) {
    $inputStream = [IO.File]::OpenRead($Source)
    try {
        $outputStream = [IO.File]::Create($Destination)
        try {
            $gzipStream = New-Object IO.Compression.GZipStream($outputStream, [IO.Compression.CompressionMode]::Compress, $true)
            try { $inputStream.CopyTo($gzipStream) } finally { $gzipStream.Dispose() }
            $outputStream.Flush($true)
        } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}
$manifestResource = Join-Path $resourceFolder 'manifest.json.gz'
$payloadResource = Join-Path $resourceFolder 'payload.bin.gz'
Compress-Resource $manifest $manifestResource
Compress-Resource $payload $payloadResource
$applicationManifest = Join-Path $resourceFolder 'Installer.manifest'
$applicationManifestText = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="ProbablyStolen.Azerbaijani.Setup" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings><longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware></windowsSettings>
  </application>
</assembly>
'@
[IO.File]::WriteAllText($applicationManifest, $applicationManifestText, (New-Object Text.UTF8Encoding($false)))
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', "/win32manifest:$applicationManifest",
    "/resource:$manifestResource,manifest.json.gz", "/resource:$payloadResource,payload.bin.gz", "/out:$OutputPath", $source
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
Write-Host "Built $OutputPath"
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
