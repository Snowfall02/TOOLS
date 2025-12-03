using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Hangfire.Redis;
using System;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Hangfire + Redis setup
var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
builder.Services.AddHangfire(config =>
{
    // Configure Hangfire to use Redis (Hangfire.Redis.StackExchange)
    config.UseRedisStorage(redisUrl);
});
builder.Services.AddHangfireServer();

// Register background job class so Hangfire can activate it
builder.Services.AddTransient<Jobs.DeepDiveJob>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Add Hangfire dashboard (protect in prod)
app.UseRouting();
app.UseAuthorization();
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapHangfireDashboard("/hangfire");
});

app.Run();
