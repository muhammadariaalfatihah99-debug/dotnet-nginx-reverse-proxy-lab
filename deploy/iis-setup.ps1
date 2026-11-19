# Deploy SampleApi ke IIS (Windows) — Modul Level 12 Sesi 2, Bagian 2
# Jalankan di PowerShell sebagai Administrator.
# Prasyarat: IIS aktif + ASP.NET Core Hosting Bundle (.NET 8) terpasang.
#
# Contoh:
#   .\iis-setup.ps1 -Artifact "\\wsl.localhost\Ubuntu\home\<user-wsl>\lab-dotnet\publish"

param(
    [Parameter(Mandatory)] [string] $Artifact   # folder hasil "dotnet publish"
)

$ErrorActionPreference = "Stop"
$appcmd   = "$env:windir\System32\inetsrv\appcmd.exe"
$sitePath = "C:\inetpub\SampleApi"
$pool     = "SampleApiPool"
$hostName = "api.example.test"

# 1. Salin artifact (tanpa config Development & executable Linux)
New-Item -ItemType Directory -Path $sitePath -Force | Out-Null
Copy-Item "$Artifact\*" $sitePath -Recurse -Force -Exclude "appsettings.Development.json", "SampleApi"

# 2. App Pool: No Managed Code (runtime dijalankan oleh ANCM, bukan .NET Framework)
& $appcmd add apppool /name:$pool /managedRuntimeVersion:""

# 3. Website + binding HTTP berbasis host name
& $appcmd add site /name:SampleApi /physicalPath:$sitePath /bindings:"http/*:80:$hostName"
& $appcmd set app "SampleApi/" /applicationPool:$pool

# 4. Least privilege: putus warisan (buang BUILTIN\Users), sisakan yang perlu saja
icacls $sitePath /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "IIS AppPool\${pool}:(OI)(CI)RX"

# 5. HTTPS: sertifikat self-signed lab di Certificate Store (produksi: CA tepercaya)
$cert = New-SelfSignedCertificate -DnsName $hostName -CertStoreLocation "cert:\LocalMachine\My" -NotAfter (Get-Date).AddYears(1)
Import-Module WebAdministration
New-WebBinding -Name SampleApi -Protocol https -Port 443 -HostHeader $hostName -SslFlags 1   # 1 = SNI
(Get-WebBinding -Name SampleApi -Protocol https).AddSslCertificate($cert.Thumbprint, "My")

# 6. Hostname lab
Add-Content -Path "$env:windir\System32\drivers\etc\hosts" -Value "`n127.0.0.1 $hostName"

# 7. Verifikasi
curl.exe -sk https://$hostName/debug-headers
