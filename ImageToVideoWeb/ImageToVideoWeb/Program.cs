using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using Amazon;
using Amazon.S3;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Đăng ký HttpClient
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    return new AmazonS3Client(RegionEndpoint.GetBySystemName("ap-southeast-1"));
});
// Đăng ký OcrVideoService với Dependency Injection
builder.Services.AddSingleton<OcrVideoService>();

// Configure logging
builder.Logging.ClearProviders(); // Xóa các provider mặc định
builder.Logging.AddConsole();     // Thêm logging ra console
builder.Logging.AddDebug();       // Thêm logging ra cửa sổ Output của Visual Studio

// Tăng giới hạn kích thước file upload và timeout
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2L * 1024L * 1024L * 1024L;  // 100 MB
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 2L * 1024L * 1024L * 1024L;  // 100 MB
    /* serverOptions.Limits.RequestTimeout = TimeSpan.FromMinutes(10); */// Tăng timeout lên 10 phút
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Phục vụ file tĩnh từ thư mục wwwroot (mặc định)
app.UseStaticFiles();

// Phục vụ file tĩnh từ thư mục outputs (nơi lưu video)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.WebRootPath, "outputs")),
    RequestPath = "/outputs"
});

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();