using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Data;
using Dapper;
using Npgsql;
using Hangfire;

[ApiController]
[Route("[controller]")]
public class DeepDiveController : ControllerBase
{
    private readonly IHttpClientFactory _http;
    private readonly string _dbConnectionString;
    private readonly string _chartServiceUrl;

    public DeepDiveController(IHttpClientFactory http)
    {
        _http = http;
        _dbConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Username=deepdive;Password=deepdivepass;Database=deepdive";
        _chartServiceUrl = Environment.GetEnvironmentVariable("CHART_SERVICE_URL") ?? "http://localhost:8001";
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement body)
    {
        // Expect body contains symbol, interval, period/start/end fields
        var symbol = body.GetProperty("symbol").GetString()?.ToUpper() ?? throw new ArgumentException("symbol required");

        using IDbConnection db = new NpgsqlConnection(_dbConnectionString);
        db.Open();

        // Upsert equity and get id
        var equityId = await db.ExecuteScalarAsync<int>(@"
            INSERT INTO equities (symbol)
            VALUES (@Symbol)
            ON CONFLICT (symbol) DO UPDATE SET symbol = EXCLUDED.symbol
            RETURNING id;
        ", new { Symbol = symbol });

        // Insert a deep_dives row with status queued; image_data null for now
        var interval = body.TryGetProperty("interval", out var iv) ? iv.GetString() : "1d";
        var meta = new { requestedPayload = body };
        var metadataJson = JsonSerializer.Serialize(meta);

        var deepDiveId = await db.ExecuteScalarAsync<int>(@"
            INSERT INTO deep_dives (equity_id, interval, status, metadata)
            VALUES (@EqId, @Interval, @Status, @Meta)
            RETURNING id;
        ", new { EqId = equityId, Interval = interval, Status = "queued", Meta = metadataJson });

        // Enqueue background job that will perform the deep dive and persist results
        // The job will be resolved via DI (Jobs.DeepDiveJob) and receives (deepDiveId, requestBodyJson)
        var requestJson = body.GetRawText();
        var jobId = BackgroundJob.Enqueue<Jobs.DeepDiveJob>(job => job.Run(deepDiveId, requestJson));

        // Update deep_dives with job id
        await db.ExecuteAsync("UPDATE deep_dives SET job_id = @JobId WHERE id = @Id", new { JobId = jobId, Id = deepDiveId });

        var result = new
        {
            deep_dive_id = deepDiveId,
            job_id = jobId,
            status = "queued"
        };
        return new JsonResult(result);
    }
}
