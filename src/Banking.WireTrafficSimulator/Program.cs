using Banking.Infrastructure;
using Banking.WireTrafficSimulator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBankingInfrastructure(builder.Configuration);
builder.Services.AddSingleton<WireTrafficService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
