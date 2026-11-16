using System.Text;
using FalconFX.TradeProcessor;

Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);
builder.AddTradeProcessorServices();

var host = builder.Build();
await host.RunAsync();
