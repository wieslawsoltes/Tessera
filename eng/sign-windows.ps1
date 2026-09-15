param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
foreach ($name in @('WINDOWS_PFX_BASE64','WINDOWS_PFX_PASSWORD')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Required signing secret is missing: $name" }
}
$pfx = Join-Path $env:RUNNER_TEMP ('tessera-' + [Guid]::NewGuid().ToString('N') + '.pfx')
$cert = $null
try {
    [IO.File]::WriteAllBytes($pfx,[Convert]::FromBase64String($env:WINDOWS_PFX_BASE64))
    $password = ConvertTo-SecureString $env:WINDOWS_PFX_PASSWORD -AsPlainText -Force
    $cert = Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\CurrentUser\My -Password $password
    if (!$cert.HasPrivateKey -or $cert.NotAfter -le (Get-Date)) { throw 'An unexpired certificate with a private key is required.' }
    if (!($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })) { throw 'Certificate is not authorized for code signing.' }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (!$signtool) { throw 'Windows SDK signtool.exe was not found.' }
    foreach ($name in @('Tessera.exe','Tessera.dll','Tessera.Core.dll')) {
        $file=Join-Path $Directory $name
        & $signtool.FullName sign /sha1 $cert.Thumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $name" }
        & $signtool.FullName verify /pa /all $file
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $name" }
    }
}
finally {
    if ($cert) { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue }
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
    $env:WINDOWS_PFX_BASE64='';$env:WINDOWS_PFX_PASSWORD=''
}
