using System;
using System.Data;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Jobs
{
    // This class is instantiated by DI when Hangfire runs a job.
    public class DeepDiveJob
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly string _dbConnectionString;
        private readonly string _chartServiceUrl;

        public DeepDiveJob(IHttpClientFactory httpFactory)
        {
            _httpFactory = httpFactory;
            _dbConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Username=deepdive;Password=deepdivepass;Database=deepdive";
            _chartServiceUrl = Environment.GetEnvironmentVariable("CHART_SERVICE_URL") ?? "http://localhost:8001";
        }

        // Hangfire calls this method. deepDiveId is the DB row we will update.
        public async Task Run(int deepDiveId, string requestJson)
        {
            using IDbConnection db = new NpgsqlConnection(_dbConnectionString);
            db.Open();

            try
            {
                // Mark processing
                await db.ExecuteAsync("UPDATE deep_dives SET status = @Status WHERE id = @Id", new { Status = "processing", Id = deepDiveId });

                // Forward request to Python chart service (/generate)
                var client = _httpFactory.CreateClient();
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync($"{_chartServiceUrl}/generate", content);
                var s = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    // mark failed and store error in metadata
                    await db.ExecuteAsync("UPDATE deep_dives SET status = @Status, metadata = metadata || @Err::jsonb WHERE id = @Id",
                        new { Status = "failed", Err = JsonSerializer.Serialize(new { error = s }), Id = deepDiveId });
                    return;
                }

                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;

                var imageB64 = root.GetProperty("image_base64").GetString();
                var candles = root.GetProperty("candles").EnumerateArray();
                var start = root.GetProperty("start").GetString();
                var end = root.GetProperty("end").GetString();
                var rows = root.GetProperty("rows").GetInt32();
                var interval = root.GetProperty("interval").GetString();

                // Convert image base64 to bytes
                var imageBytes = Convert.FromBase64String(imageB64);

                // Update deep_dives row: image_data, start_time, end_time, status, metadata
                var meta = JsonSerializer.Serialize(new { rows = rows, interval = interval, generated_at = DateTime.UtcNow });
                await db.ExecuteAsync(@"
                    UPDATE deep_dives
                    SET image_data = @Image, start_time = @Start, end_time = @End, status = @Status, metadata = @Meta
                    WHERE id = @Id
                ", new { Image = imageBytes, Start = start, End = end, Status = "done", Meta = meta, Id = deepDiveId });

                // Insert candles (replace any existing for this deep_dive)
                await db.ExecuteAsync("DELETE FROM candles WHERE deep_dive_id = @Id", new { Id = deepDiveId });

                var insertSql = @"
                    INSERT INTO candles (deep_dive_id, ts, open, high, low, close, volume, pct_move)
                    VALUES (@DeepDiveId, @Ts, @Open, @High, @Low, @Close, @Volume, @PctMove);
                ";

                foreach (var c in root.GetProperty("candles").EnumerateArray())
                {
                    var ts = DateTime.Parse(c.GetProperty("ts").GetString());
                    var open = c.GetProperty("open").GetDouble();
                    var high = c.GetProperty("high").GetDouble();
                    var low = c.GetProperty("low").GetDouble();
                    var close = c.GetProperty("close").GetDouble();
                    var volume = c.GetProperty("volume").GetDouble();
                    var pct_move = c.GetProperty("pct_move").GetDouble();

                    await db.ExecuteAsync(insertSql, new
                    {
                        DeepDiveId = deepDiveId,
                        Ts = ts,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        PctMove = pct_move
                    });
                }
            }
            catch (Exception ex)
            {
                // On exception, mark failed and append error message to metadata
                var err = JsonSerializer.Serialize(new { error = ex.Message, stack = ex.StackTrace });
                await db.ExecuteAsync("UPDATE deep_dives SET status = @Status, metadata = metadata || @Err::jsonb WHERE id = @Id",
                    new { Status = "failed", Err = err, Id = deepDiveId });
                throw; // rethrow so Hangfire records failure
            }
        }
    }
}