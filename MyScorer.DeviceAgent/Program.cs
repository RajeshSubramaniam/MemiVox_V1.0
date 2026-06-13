using MyScorer.DeviceAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<BackendApiClient>();
builder.Services.AddSingleton<AtemDetector>();
builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
host.Run();
