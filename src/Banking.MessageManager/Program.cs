using Banking.Infrastructure;
using Banking.MessageManager;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBankingInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
