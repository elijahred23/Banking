using Banking.Infrastructure;
using Banking.SwiftSimulator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBankingInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
