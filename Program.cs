//ol artık yeter
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

string? dbConn = null;
string? startupError = null;

static string NormalizeQ(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return "";
    s = s.Trim().ToLowerInvariant();
    while (s.Contains("  ")) s = s.Replace("  ", " ");
    return s;
}

static bool IsTimeSensitive(string q)
{
    var x = q.ToLowerInvariant();

    string[] keywords =
    {
        "bugün","yarın","şu an","hava","dolar","euro","kur","haber",
        "today","tomorrow","now","weather","price","news","latest"
    };

    return keywords.Any(k => x.Contains(k));
}

static string GetPgConn()
{
    var url = Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PRIVATE_URL");

    if (!string.IsNullOrWhiteSpace(url))
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);

        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');

        return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    throw new Exception("DATABASE_URL bulunamadı.");
}

static async Task EnsureDbAsync(string connString)
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var sql = @"
CREATE TABLE IF NOT EXISTS qa_cache (
  id BIGSERIAL PRIMARY KEY,
  qnorm TEXT NOT NULL,
  question TEXT NOT NULL,
  answer TEXT NOT NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string?> TryCachePg(string connString, string qnorm)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var sql = @"SELECT answer, expires_at FROM qa_cache WHERE qnorm=@q ORDER BY id DESC LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@q", qnorm);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var expires = reader.GetDateTime(1);
        if (expires.ToUniversalTime() < DateTime.UtcNow) return null;

        return reader.GetString(0);
    }
    catch
    {
        return null;
    }
}

static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

        var sql = @"INSERT INTO qa_cache (qnorm, question, answer, expires_at)
                    VALUES (@qnorm,@question,@answer,@expires);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qnorm", qnorm);
        cmd.Parameters.AddWithValue("@question", question);
        cmd.Parameters.AddWithValue("@answer", answer);
        cmd.Parameters.AddWithValue("@expires", expires);

        await cmd.ExecuteNonQueryAsync();
    }
    catch { }
}

static async Task<string> AskOpenAI(string question)
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_KEY");

    if (string.IsNullOrWhiteSpace(apiKey))
        return "OPENAI_API_KEY tanımlı değil.";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = "Sen VR içindeki öğretmensin. Cevapları HER ZAMAN Türkçe ver. Kısa ve net ol." },
            new { role = "user", content = question }
        }
    };

    try
    {
        var res = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        var jsonString = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            return jsonString;

        using var doc = JsonDocument.Parse(jsonString);

        if (!doc.RootElement.TryGetProperty("choices", out var choices))
            return "choices yok";

        var content = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? "boş cevap";
    }
    catch (Exception ex)
    {
        return ex.Message;
    }
}

/* ---------- STARTUP ---------- */

try
{
    dbConn = GetPgConn();
    await EnsureDbAsync(dbConn);
    Console.WriteLine("DB OK");
}
catch (Exception ex)
{
    startupError = ex.Message;
    Console.WriteLine(startupError);
}

/* ---------- ENDPOINTS ---------- */

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/dbcheck", async () =>
{
    if (startupError != null)
        return Results.Problem(startupError);

    try
    {
        await using var conn = new NpgsqlConnection(dbConn);
        await conn.OpenAsync();

        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/ask", async (AskRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest();

    var question = req.Question;
    var qnorm = NormalizeQ(question);
    var timeSensitive = IsTimeSensitive(question);

    if (!timeSensitive && dbConn != null)
    {
        var cached = await TryCachePg(dbConn, qnorm);
        if (cached != null)
            return Results.Ok(new { answer = cached, cached = true });
    }

    var answer = await AskOpenAI(question);

    if (dbConn != null)
    {
        var ttl = timeSensitive ? 10 : (60 * 24 * 30);
        _ = SaveCachePg(dbConn, qnorm, question, answer, ttl);
    }

    return Results.Ok(new { answer, cached = false });
});

app.Run();

public record AskRequest(
    [property: JsonPropertyName("question")] string Question
);
