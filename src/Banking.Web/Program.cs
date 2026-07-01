using Banking.Infrastructure;
using Banking.Web;
using Banking.Web.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddSingleton<WireUpdateTracker>();
builder.Services.AddHostedService<WireUpdateBroadcaster>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(8);
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => { options.LoginPath = "/auth/login"; options.Cookie.Name = "FedwireLab.Auth.v2"; });
builder.Services.AddAuthorization();
builder.Services.AddBankingInfrastructure(builder.Configuration);

var app = builder.Build();
await app.Services.InitializeDatabaseAsync(seed: true);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHub<WireUpdatesHub>("/hubs/wires");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
