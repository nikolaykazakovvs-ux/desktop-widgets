<# Downloads only pinned official build inputs, checks hashes/signature and never executes them. #>
param([string]$Destination = (Join-Path $PSScriptRoot 'payload'))
$ErrorActionPreference = 'Stop'
$assets = @(
    @{Name='LibreHardwareMonitor.zip'; Url='https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip'; Hash='086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001'},
    @{Name='PawnIO_setup.exe'; Url='https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'; Hash='1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'}
)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($asset in $assets) {
    $file = Join-Path $Destination $asset.Name
    if (-not (Test-Path -LiteralPath $file)) {
        $pending = $file + '.download'
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $asset.Url -OutFile $pending
            if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Checksum mismatch: $($asset.Name)" }
            Move-Item -LiteralPath $pending -Destination $file
        } finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending } }
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Checksum mismatch: $($asset.Name)" }
}
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Destination 'PawnIO_setup.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=namazso\.eu') { throw 'PawnIO signature verification failed.' }
Write-Host 'Pinned build inputs verified. Nothing has been installed or started.'
