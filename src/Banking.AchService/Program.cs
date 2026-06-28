using Banking.AchService;
using Banking.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBankingInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
builder.Build().Run();
