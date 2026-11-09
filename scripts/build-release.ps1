[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
  [string]$Version,

  [Parameter(Mandatory)]
  [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.0$')]
  [string]$MsixVersion
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Crimson.WinUI\Crimson.WinUI.csproj'
$manifestPath = Join-Path $repo 'Crimson.WinUI\Package.appxmanifest'
$dist = Join-Path $repo 'dist'
$staging = Join-Path $repo 'artifacts\release-staging'
$signedOutput = Join-Path $repo 'artifacts\msix-test'
$storeOutput = Join-Path $repo 'artifacts\msix'
$publisher = 'CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F'
$packageName = 'Foxtrot47.CrimsonLauncher'
$temporaryDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfxPath = Join-Path $temporaryDirectory 'Crimson-signing.pfx'
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
$signingThumbprint = $null
$importedCertificateThumbprints = @()
$trustedRootThumbprint = $null
$removeTrustedRootCertificate = $false

function Invoke-Checked {
  param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string[]]$ArgumentList
  )

  & $FilePath @ArgumentList | Out-Host
  if ($LASTEXITCODE -ne 0) {
    throw "$FilePath failed with exit code $LASTEXITCODE."
  }
}

function Find-WindowsSdkTool {
  param([Parameter(Mandatory)][string]$Name)

  $root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $tool = Get-ChildItem $root -Recurse -Filter $Name -File |
    Where-Object { $_.DirectoryName -match '\\x64$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
  if (-not $tool) { throw "Could not find $Name in the Windows SDK." }
  return $tool.FullName
}

function Clear-ProjectOutput {
  Remove-Item (Join-Path $repo 'Crimson.WinUI\bin'), (Join-Path $repo 'Crimson.WinUI\obj') -Recurse -Force -ErrorAction SilentlyContinue
}

function Assert-ReleaseInputs {
  if ([string]::IsNullOrWhiteSpace($env:MSIX_PFX_BASE64) -or
      [string]::IsNullOrWhiteSpace($env:MSIX_PFX_PASSWORD)) {
    throw 'MSIX_PFX_BASE64 and MSIX_PFX_PASSWORD must be configured in the release environment.'
  }

  $versionComponents = $MsixVersion.Split('.') | ForEach-Object { [int64]$_ }
  if ($versionComponents.Count -ne 4 -or
      $versionComponents[3] -ne 0 -or
      ($versionComponents | Where-Object { $_ -gt 65535 })) {
    throw "Invalid MSIX version '$MsixVersion'."
  }
  if ($MsixVersion -cne "$Version.0") {
    throw "MSIX version '$MsixVersion' does not correspond to product version '$Version'."
  }
}

function Initialize-ReleaseDirectories {
  Remove-Item $dist, $staging, $signedOutput, $storeOutput -Recurse -Force -ErrorAction SilentlyContinue
  New-Item $dist, $staging -ItemType Directory | Out-Null
}

function Set-PackageVersion {
  [xml]$packageManifest = [IO.File]::ReadAllText($manifestPath)
  $identity = $packageManifest.Package.Identity
  if ($identity.Name -cne $packageName -or $identity.Publisher -cne $publisher) {
    throw 'Package.appxmanifest does not contain the expected Partner Center identity.'
  }

  $identity.SetAttribute('Version', $MsixVersion)
  $writerSettings = [Xml.XmlWriterSettings]::new()
  $writerSettings.Encoding = [Text.UTF8Encoding]::new($true)
  $writerSettings.Indent = $true
  $writer = [Xml.XmlWriter]::Create($manifestPath, $writerSettings)
  try { $packageManifest.Save($writer) } finally { $writer.Dispose() }
}

function Invoke-ReleaseTests {
  Invoke-Checked 'dotnet' @(
    'test', (Join-Path $repo 'Crimson.Core.Tests\Crimson.Core.Tests.csproj'),
    '-c', 'Release', '--nologo', '-v', 'minimal'
  )
  Invoke-Checked 'dotnet' @(
    'restore', (Join-Path $repo 'Crimson.sln'),
    '-r', 'win-x64', '-p:Platform=x64', '-p:PublishReadyToRun=true',
    '--nologo', '-v', 'minimal'
  )
  Invoke-Checked 'dotnet' @(
    'test', (Join-Path $repo 'Crimson.Tests\Crimson.Tests.csproj'),
    '--no-restore', '-c', 'Release', '-p:Platform=x64',
    '--nologo', '-v', 'minimal'
  )
}

function Assert-LicenseFile {
  param([Parameter(Mandatory)][string]$Directory)

  $licensePath = Join-Path $Directory 'LICENSE.txt'
  if (-not (Test-Path $licensePath -PathType Leaf)) {
    throw "Release output is missing LICENSE.txt: $Directory"
  }
  $expected = (Get-FileHash (Join-Path $repo 'LICENSE.txt') -Algorithm SHA256).Hash
  if ((Get-FileHash $licensePath -Algorithm SHA256).Hash -cne $expected) {
    throw "Release output contains a different LICENSE.txt: $Directory"
  }
}

function Publish-PortableAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'publish', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '--self-contained', 'true',
    "-p:Version=$Version", "-p:InformationalVersion=$Version",
    '-p:PublishReadyToRun=true',
    '-o', (Join-Path $staging 'unpackaged'),
    '--nologo', '-v', 'minimal'
  )

  $portableExe = Join-Path $staging 'unpackaged\Crimson.exe'
  if (-not (Test-Path $portableExe)) { throw 'Portable publish did not produce Crimson.exe.' }
  Assert-LicenseFile (Join-Path $staging 'unpackaged')
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $staging 'unpackaged'),
    (Join-Path $dist "Crimson-$Version-win-x64.zip"),
    [IO.Compression.CompressionLevel]::SmallestSize,
    $false)
}

function Read-SigningPfx {
  try {
    return [Convert]::FromBase64String($env:MSIX_PFX_BASE64)
  }
  catch {
    throw 'MSIX_PFX_BASE64 is not valid base64.'
  }
}

function Assert-SigningCertificate {
  param([Parameter(Mandatory)]$Certificate)

  if (-not $Certificate.HasPrivateKey) {
    throw 'The imported signing certificate does not contain a private key.'
  }
  if ($Certificate.Subject -cne $publisher) {
    throw "Signing certificate subject '$($Certificate.Subject)' does not match '$publisher'."
  }

  $now = Get-Date
  if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) {
    throw 'The signing certificate is not currently valid.'
  }
  $codeSigningEku = $Certificate.EnhancedKeyUsageList |
    Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }
  if (-not $codeSigningEku) {
    throw 'The signing certificate does not permit code signing.'
  }
}

function Import-SigningCertificate {
  $pfxBytes = Read-SigningPfx
  [IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
  [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)

  $securePassword = ConvertTo-SecureString $env:MSIX_PFX_PASSWORD -AsPlainText -Force
  $previewCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $pfxPath,
    $env:MSIX_PFX_PASSWORD,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
  $script:signingThumbprint = $previewCertificate.Thumbprint
  $existingCertificateThumbprints = @(Get-ChildItem 'Cert:\CurrentUser\My' | Select-Object -ExpandProperty Thumbprint)
  $previewCertificate.Dispose()

  $imported = Import-PfxCertificate `
    -FilePath $pfxPath `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -Password $securePassword
  $script:importedCertificateThumbprints = @($imported |
    Where-Object { $_.Thumbprint -notin $existingCertificateThumbprints } |
    Select-Object -ExpandProperty Thumbprint)
  Remove-Item $pfxPath -Force

  $signingCertificate = $imported |
    Where-Object { $_.Thumbprint -eq $signingThumbprint } |
    Select-Object -First 1
  Assert-SigningCertificate $signingCertificate
  return $signingCertificate
}

function Export-PublicCertificate {
  param([Parameter(Mandatory)]$Certificate)

  $cerPath = Join-Path $dist "Crimson-$Version.cer"
  Export-Certificate -Cert $Certificate -FilePath $cerPath -Type CERT | Out-Null
  $exportedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
  try {
    if ($exportedCertificate.Thumbprint -ne $signingThumbprint -or $exportedCertificate.HasPrivateKey) {
      throw 'The exported CER does not match the signing certificate.'
    }
  }
  finally {
    $exportedCertificate.Dispose()
  }
  return $cerPath
}

function Build-SignedAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'build', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '-p:EnablePackaging=true', '-p:EnableTestSigning=true',
    '-p:UapAppxPackageBuildMode=SideloadOnly',
    "-p:PackageCertificateThumbprint=$signingThumbprint",
    "-p:AppxPackageVersion=$MsixVersion", "-p:Version=$Version",
    '--nologo', '-v', 'minimal'
  )

  $signedPackages = @(Get-ChildItem $signedOutput -Recurse -Filter *.msix -File)
  if ($signedPackages.Count -ne 1) {
    throw "Expected one signed MSIX, found $($signedPackages.Count)."
  }
  $signedPath = Join-Path $dist "Crimson-$Version-win-x64.msix"
  Copy-Item $signedPackages[0].FullName $signedPath
  return $signedPath
}

function Trust-ReleaseCertificate {
  param([Parameter(Mandatory)][string]$CerPath)

  $script:removeTrustedRootCertificate = -not (Test-Path "Cert:\CurrentUser\Root\$signingThumbprint")
  $trustedRootCertificate = Import-Certificate `
    -FilePath $CerPath `
    -CertStoreLocation 'Cert:\CurrentUser\Root'
  $script:trustedRootThumbprint = $trustedRootCertificate.Thumbprint
}

function Assert-PackageIdentity {
  param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$MakeAppx
  )

  Remove-Item $Destination -Recurse -Force -ErrorAction SilentlyContinue
  Invoke-Checked $MakeAppx @('unpack', '/p', $PackagePath, '/d', $Destination, '/o')
  Assert-LicenseFile $Destination
  [xml]$packageManifest = Get-Content (Join-Path $Destination 'AppxManifest.xml') -Raw
  $identity = $packageManifest.Package.Identity
  if ($identity.Name -cne $packageName) {
    throw "Package identity '$($identity.Name)' does not match '$packageName'."
  }
  if ($identity.Publisher -cne $publisher) {
    throw "Package publisher '$($identity.Publisher)' does not match '$publisher'."
  }
  if ($identity.Version -cne $MsixVersion) {
    throw "Package version '$($identity.Version)' does not match '$MsixVersion'."
  }
}

function Confirm-SignedAsset {
  param(
    [Parameter(Mandatory)][string]$SignedPath,
    [Parameter(Mandatory)][string]$CerPath
  )

  $signTool = Find-WindowsSdkTool 'signtool.exe'
  Invoke-Checked $signTool @(
    'sign', '/fd', 'SHA256', '/sha1', $signingThumbprint,
    '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $SignedPath)
  Trust-ReleaseCertificate $CerPath
  Invoke-Checked $signTool @('verify', '/pa', '/v', $SignedPath)

  $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
  Assert-PackageIdentity $SignedPath (Join-Path $staging 'signed-package') $makeAppx
}

function Build-StoreAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'build', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '-p:EnablePackaging=true', '-p:EnableTestSigning=false',
    '-p:UapAppxPackageBuildMode=StoreUpload',
    "-p:AppxPackageVersion=$MsixVersion", "-p:Version=$Version",
    '--nologo', '-v', 'minimal'
  )

  $storePackages = @(Get-ChildItem $storeOutput -Recurse -Filter *.msixupload -File)
  if ($storePackages.Count -ne 1) {
    throw "Expected one Store .msixupload, found $($storePackages.Count)."
  }
  $storePath = Join-Path $dist "Crimson-$Version-partner-center.msixupload"
  Copy-Item $storePackages[0].FullName $storePath
  return $storePath
}

function Confirm-StoreAsset {
  param([Parameter(Mandatory)][string]$StorePath)

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $uploadContents = Join-Path $staging 'store-upload'
  New-Item $uploadContents -ItemType Directory | Out-Null
  [IO.Compression.ZipFile]::ExtractToDirectory($StorePath, $uploadContents)

  $innerPackages = @(Get-ChildItem $uploadContents -Recurse -Filter *.msix -File)
  if ($innerPackages.Count -ne 1) {
    throw "Expected one MSIX inside the Store upload, found $($innerPackages.Count)."
  }

  $storePackageContents = Join-Path $staging 'store-package'
  $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
  Assert-PackageIdentity $innerPackages[0].FullName $storePackageContents $makeAppx
  if (Test-Path (Join-Path $storePackageContents 'AppxSignature.p7x')) {
    throw 'Partner Center package unexpectedly contains a local signature.'
  }
}

function Write-ReleaseChecksums {
  $payloads = @(
    Get-Item (Join-Path $dist "Crimson-$Version-win-x64.zip")
    Get-Item (Join-Path $dist "Crimson-$Version-win-x64.msix")
    Get-Item (Join-Path $dist "Crimson-$Version.cer")
    Get-Item (Join-Path $dist "Crimson-$Version-partner-center.msixupload")
  )

  $checksums = foreach ($asset in $payloads) {
    $hash = (Get-FileHash $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
  }
  $checksums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

  $releaseFiles = @(Get-ChildItem $dist -File)
  if ($releaseFiles.Count -ne 5) {
    throw "Expected five release files, found $($releaseFiles.Count)."
  }
  Write-Host "Prepared and verified $($releaseFiles.Count) release files for $Version."
}

function Restore-ReleaseEnvironment {
  [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
  Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue

  if ($removeTrustedRootCertificate -and $trustedRootThumbprint) {
    & certutil.exe -user -delstore Root $trustedRootThumbprint | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "Failed to remove temporary trusted root certificate $trustedRootThumbprint."
    }
  }
  foreach ($thumbprint in $importedCertificateThumbprints) {
    Remove-Item "Cert:\CurrentUser\My\$thumbprint" -Force -ErrorAction SilentlyContinue
  }
}

try {
  Assert-ReleaseInputs
  Initialize-ReleaseDirectories
  Set-PackageVersion
  Invoke-ReleaseTests
  Publish-PortableAsset
  $signingCertificate = Import-SigningCertificate
  $cerPath = Export-PublicCertificate $signingCertificate
  $signedPath = Build-SignedAsset
  Confirm-SignedAsset $signedPath $cerPath
  $storePath = Build-StoreAsset
  Confirm-StoreAsset $storePath
  Write-ReleaseChecksums
}
finally {
  Restore-ReleaseEnvironment
}
