// SampleApi — Lab Modul Level 12 Sesi 2 (Nginx Reverse Proxy + HTTPS)
// Berdasarkan Program.cs di Bagian 4.3 modul, dengan sedikit perbaikan untuk lab.

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// 1. Batas ukuran body request di Kestrel = 10 MiB.
//    HARUS selaras dengan client_max_body_size di Nginx (10m).
//    Kalau Nginx lebih kecil, Nginx yang menolak duluan (HTTP 413).
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
});

// 2. Forwarded Headers: percayai X-Forwarded-For / X-Forwarded-Proto
//    HANYA kalau datang dari proxy tepercaya (Nginx di mesin yang sama = loopback).
//    Header dari IP lain diabaikan -> mencegah spoofing IP & skema.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;

    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);      // 127.0.0.1
    options.KnownProxies.Add(IPAddress.IPv6Loopback);  // ::1
});

var app = builder.Build();

// 3. WAJIB paling awal, sebelum HTTPS redirection / auth / routing.
//    Kalau terbalik: redirect loop (ERR_TOO_MANY_REDIRECTS) di belakang proxy.
app.UseForwardedHeaders();
app.UseHttpsRedirection();

// 4. Endpoint aplikasi
app.MapGet("/", () => Results.Ok(new
{
    application = "SampleApi",
    message = "Nginx reverse proxy is working"
}));

// Endpoint ringan untuk health check (dipakai load balancer & monitoring)
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestampUtc = DateTime.UtcNow
}));

app.MapGet("/products", () =>
{
    var products = new[]
    {
        new { id = 1, name = "Keyboard", price = 350000 },
        new { id = 2, name = "Mouse",    price = 150000 },
        new { id = 3, name = "Monitor",  price = 1800000 }
    };
    return Results.Ok(products);
});

// Endpoint diagnostik: lihat apa yang "dipercaya" aplikasi setelah lewat proxy.
// HANYA UNTUK LAB — di produksi endpoint seperti ini = kebocoran informasi.
//
// Catatan: setelah UseForwardedHeaders() memproses header, nilai aslinya
// dipindah ke X-Original-*, dan X-Forwarded-* yang sudah dipakai dihapus.
// Jadi xForwardedProto kosong = middleware BEKERJA, bukan error.
app.MapGet("/debug-headers", (HttpContext ctx) => Results.Ok(new
{
    detectedScheme  = ctx.Request.Scheme,
    detectedHost    = ctx.Request.Host.Value,
    clientIp        = ctx.Connection.RemoteIpAddress?.ToString(),
    xForwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
    xForwardedFor   = ctx.Request.Headers["X-Forwarded-For"].ToString(),
    xOriginalProto  = ctx.Request.Headers["X-Original-Proto"].ToString(),
    xOriginalFor    = ctx.Request.Headers["X-Original-For"].ToString()
}));

// Endpoint upload untuk simulasi HTTP 413 (Tahap 7)
app.MapPost("/upload", async (HttpRequest request) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    return Results.Ok(new
    {
        message = "Payload berhasil diterima",
        receivedBytes = ms.Length
    });
});

app.Run();
