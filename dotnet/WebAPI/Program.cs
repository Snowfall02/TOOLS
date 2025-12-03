using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Hangfire.Redis;
using System;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Hangfire + Redis setup
var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
builder.Services.AddHangfire(config =>
{
    config.UseRedisStorage(redisUrl);
});

// Global retry policy for Hangfire jobs (3 attempts)
GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 3 });

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

// API Key protection middleware: requires X-Api-Key header for sensitive endpoints when API_KEY is set
var apiKey = Environment.GetEnvironmentVariable("API_KEY");
app.Use(async (context, next) =>
{
    if (!string.IsNullOrEmpty(apiKey))
    {
        var path = context.Request.Path;
        // Protect Hangfire dashboard and DeepDive endpoints
        if (path.StartsWithSegments("/hangfire") || path.StartsWithSegments("/DeepDive"))
        {
            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) || providedKey != apiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
    }
    await next();
});

app.UseRouting();
app.UseAuthorization();
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapHangfireDashboard("/hangfire");
});

app.Run();