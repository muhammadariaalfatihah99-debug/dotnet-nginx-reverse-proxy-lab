# ASP.NET Core di Balik Nginx Reverse Proxy + HTTPS — Lab DevSecOps

Lab praktik deployment **ASP.NET Core (.NET 8)** gaya produksi di Linux: Kestrel di loopback, dijaga **systemd**, di belakang **Nginx** sebagai reverse proxy dengan **terminasi TLS**, plus hardening dan troubleshooting.

Lingkungan: Ubuntu 24.04 (WSL2, mirrored networking) · .NET 8 · Nginx 1.24 · OpenSSL 3

## Arsitektur

```
 Client (curl / browser)
    │  https://api.example.test  (port 443, TLS 1.2/1.3, HTTP/2)
    ▼
 ┌─────────────────────────────┐
 │ Nginx                       │  terminasi TLS · virtual host · 301 HTTP→HTTPS
 │  - server_tokens off        │  batas payload 10m · timeout upstream
 └─────────────┬───────────────┘
               │ http://127.0.0.1:5000  (+ X-Forwarded-For / X-Forwarded-Proto)
               ▼
 ┌─────────────────────────────┐
 │ Kestrel (SampleApi.dll)     │  dijalankan systemd sebagai user "sampleapi" (non-root)
 │  - UseForwardedHeaders()    │  KnownProxies = loopback saja
 │  - MaxRequestBodySize 10MiB │  Restart=on-failure
 └─────────────────────────────┘
```

## Isi repo

| Path | Fungsi |
|---|---|
| `SampleApi/Program.cs` | API: `/`, `/health`, `/products`, `/debug-headers` (lab saja), `POST /upload` |
| `deploy/sampleapi.service` | Unit systemd: user non-root, bind loopback, auto-restart |
| `deploy/nginx-sampleapi.conf` | Virtual host HTTP (tahap awal) |
| `deploy/nginx-sampleapi-https.conf` | Virtual host HTTPS + redirect 301 |

> Sertifikat dan private key **sengaja tidak ada di repo** (lihat `.gitignore`). Buat sendiri dengan langkah di bawah.

## Cara deploy

```bash
# 1. Publish sebagai user biasa (bukan sudo)
cd SampleApi && dotnet publish -c Release -o ../publish && cd ..

# 2. User sistem tanpa shell + direktori dengan izin minimum
sudo useradd --system --no-create-home --shell /usr/sbin/nologin sampleapi
sudo mkdir -p /var/www/sampleapi
sudo cp -r publish/. /var/www/sampleapi/
sudo chown -R sampleapi:sampleapi /var/www/sampleapi
sudo chmod -R 750 /var/www/sampleapi

# 3. systemd
sudo cp deploy/sampleapi.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now sampleapi

# 4. Sertifikat self-signed untuk lab (produksi: Let's Encrypt / CA)
sudo mkdir -p /etc/ssl/localcerts
sudo openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout /etc/ssl/localcerts/api.example.test.key \
  -out /etc/ssl/localcerts/api.example.test.crt \
  -subj "/CN=api.example.test" -addext "subjectAltName=DNS:api.example.test"
sudo chmod 600 /etc/ssl/localcerts/api.example.test.key

# 5. Nginx
sudo cp deploy/nginx-sampleapi-https.conf /etc/nginx/sites-available/sampleapi
sudo ln -sf /etc/nginx/sites-available/sampleapi /etc/nginx/sites-enabled/sampleapi
sudo nginx -t && sudo systemctl reload nginx

# 6. Hostname lab
echo '127.0.0.1 api.example.test' | sudo tee -a /etc/hosts
```

## Hasil verifikasi

| # | Uji | Perintah | Hasil |
|---|---|---|---|
| 1 | Kestrel hanya di loopback | `ss -ltnp \| grep :5000` | `127.0.0.1:5000` ✅ |
| 2 | Proses bukan root | `ps -o user,cmd -C dotnet` | `sampleapi` ✅ |
| 3 | Folder app tertutup untuk user lain | `ls /var/www/sampleapi` (tanpa sudo) | `Permission denied` ✅ |
| 4 | Response lewat proxy | `curl -i http://api.example.test/health` | `Server: nginx` (tanpa versi) ✅ |
| 5 | Redirect HTTP→HTTPS | `curl -i http://api.example.test/health` | `301 Moved Permanently` ✅ |
| 6 | Protokol | `curl -sk -w '%{http_version}'` + `openssl s_client` | HTTP/2, TLSv1.3 ✅ |
| 7 | TLS lama ditolak | `openssl s_client -tls1_1` | `alert protocol version` ✅ |
| 8 | Aplikasi tahu skema asli | `curl -sk https://api.example.test/debug-headers` | `detectedScheme: "https"` ✅ |
| 9 | Payload besar ditolak Nginx | POST 11 MiB | `413`, error.log: `client intended to send too large body` ✅ |
| 10 | Lapis kedua di Kestrel | POST 11 MiB langsung ke `:5000` | `413 Payload Too Large` ✅ |
| 11 | Auto-restart saat crash | `kill -9 <MainPID>` | `Failed with result 'signal'` → restart 5 detik ✅ |
| 12 | `stop` manual tidak di-restart | `systemctl stop sampleapi` | `Deactivated successfully` ✅ |

## Pemetaan Tugas Mandiri (Modul Level 12 Sesi 2, Bagian 8.2)

| Tugas | Status | Bukti |
|---|---|---|
| 1. Deployment end-to-end (systemd + Nginx, endpoint via hostname) | ✅ | Verifikasi #1, #2, #4 |
| 2. Audit isolasi jaringan (`ss -ltnp`, firewall) | ✅ sebagian | `ss` ✅ · UFW belum (WSL) |
| 3. Simulasi troubleshooting upstream mati | ✅ | Temuan #3 (504 vs 502 + log `error.log`) |
| 4. HTTPS + redirect 301 + `detectedScheme: https` | ✅ | Verifikasi #5–#8 |
| Bagian 2: Hosting di IIS (Windows) | ⏳ | Belum dikerjakan |

## Temuan & pelajaran

**1. `X-Forwarded-For` bisa dipalsukan dari localhost.**
`KnownProxies` mempercayai `127.0.0.1`, jadi request palsu langsung ke `:5000` dari mesin yang sama diterima:

```bash
curl -H "X-Forwarded-For: 6.6.6.6" http://127.0.0.1:5000/debug-headers
# → "clientIp": "6.6.6.6"
```

Penyerang dari luar tidak bisa karena Kestrel hanya bind ke loopback. Pelajarannya: KnownProxies **dan** isolasi port harus ada dua-duanya, dan IP client jangan dijadikan satu-satunya dasar otorisasi.

**2. `X-Original-Proto` berisi `http`, bukan `https`.**
Setelah `UseForwardedHeaders()` memproses header, `X-Forwarded-*` yang dipakai dihapus dan nilai **sebelum** diubah disimpan di `X-Original-*`. Jadi `xForwardedProto` kosong dan `xOriginalProto: "http"` artinya middleware **bekerja**.

**3. Hasilnya 504, bukan 502, saat aplikasi mati (lab ≠ produksi).**
Di WSL mirrored networking, koneksi ke port loopback yang kosong tidak ditolak tapi menggantung sampai `proxy_connect_timeout` (5s), sehingga hasilnya `504`. Log membedakannya: `timed out ... while connecting to upstream` (tidak bisa dihubungi) vs `... while reading response header` (aplikasi lambat). Di server Linux biasa hasilnya `502 Connection refused`.

**4. `dotnet run` mengabaikan `ASPNETCORE_URLS`.**
`Properties/launchSettings.json` menimpanya (app jalan di port 5030). Solusinya `--no-launch-profile`. File itu tidak ikut `publish`, jadi tidak berpengaruh di server.

**5. Supply chain: repo apt yang tidak ditandatangani.**
Ada sisa repo Kali (`katoolin3.list`) di Ubuntu yang ditolak apt karena kunci GPG tidak ada. Repo itu dinonaktifkan (bukan dipaksa dengan `trusted=yes`), dan tidak ada paket Kali yang terpasang.

**6. Konflik port 80 IIS vs Nginx.**
Dengan mirrored networking, port Linux dan Windows dipakai bersama. `bind() ... Address already in use` → pemiliknya PID 4 (`http.sys`/IIS).

## Checklist DevSecOps

- [x] Isolasi port internal (Kestrel di `127.0.0.1`)
- [x] HTTPS TLS 1.2/1.3 + redirect 301
- [x] Private key `chmod 600`, tidak di repo
- [x] KnownProxies dibatasi ke loopback
- [x] Least privilege (user `sampleapi`, `chmod 750`)
- [ ] Secret management (`EnvironmentFile`): belum dipakai, app belum punya secret
- [x] Batas payload & timeout
- [ ] Sanitasi log & error: belum diuji khusus
- [ ] Firewall (UFW): belum dikonfigurasi di WSL
- [ ] Patching & monitoring sertifikat

## Referensi

- [Host ASP.NET Core on Linux with Nginx](https://learn.microsoft.com/aspnet/core/host-and-deploy/linux-nginx)
- [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
- [Nginx `ngx_http_proxy_module`](https://nginx.org/en/docs/http/ngx_http_proxy_module.html)
