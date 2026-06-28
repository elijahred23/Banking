using Banking.Infrastructure;
using Banking.WireService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBankingInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
