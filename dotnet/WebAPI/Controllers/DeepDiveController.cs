using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Data;
using Dapper;
using Npgsql;
using Hangfire;

namespace WebAPI.Controllers
{
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
            _dbConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
                ?? "Host=localhost;Port=5432;Username=deepdive;Password=deepdivepass;Database=deepdive";
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

        // New endpoint: stream image bytes from Postgres (image_data BYTEA)
        // Returns:
        //  - 200 with image/png when image is ready
        //  - 202 Accepted with { status } if image_data is null (still queued/processing)
        //  - 404 if deep_dive id not found
        [HttpGet("{id}/image")]
        public async Task<IActionResult> GetImage(int id)
        {
            using IDbConnection db = new NpgsqlConnection(_dbConnectionString);
            db.Open();

            // Query image_data and status
            var row = await db.QueryFirstOrDefaultAsync("SELECT image_data, status FROM deep_dives WHERE id = @Id", new { Id = id });

            if (row == null)
                return NotFound(new { error = "deep_dive not found" });

            // Dapper returns a dynamic row; extract fields carefully
            byte[] imageBytes = null;
            string status = null;

            try
            {
                // dynamic access may differ; use dictionary-style extraction
                var dict = row as System.Collections.IDictionary;
                if (dict != null)
                {
                    if (dict.Contains("image_data"))
                        imageBytes = dict["image_data"] as byte[];
                    if (dict.Contains("status"))
                        status = dict["status"]?.ToString();
                }
                else
                {
                    // fallback
                    imageBytes = (byte[])row.image_data;
                    status = row.status;
                }
            }
            catch
            {
                // best-effort fallback
                try { imageBytes = (byte[])((object)row).GetType().GetProperty("image_data")?.GetValue(row); } catch { imageBytes = null; }
                try { status = ((object)row).GetType().GetProperty("status")?.GetValue(row)?.ToString(); } catch { status = null; }
            }

            if (imageBytes == null || imageBytes.Length == 0)
            {
                // Not ready yet: return 202 Accepted with current status so client can poll
                return StatusCode(202, new { status = status ?? "queued" });
            }

            // Return image bytes as PNG
            return File(imageBytes, "image/png");
        }
    }
}