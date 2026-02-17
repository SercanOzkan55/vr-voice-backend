using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// CORS (Unity için)
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway/Docker PORT bind
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

// ---------- Helpers ----------
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
    // Railway DB env: genelde DATABASE_URL veya DATABASE_PUBLIC_URL
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

        // Railway’de çoğu zaman SSL gerekiyor
        return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    // Alternatif: PG* env’ler
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");
    var db2 = Environment.GetEnvironmentVariable("PGDATABASE");

    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user2) && !string.IsNullOrWhiteSpace(db2))
        return $"Host={host};Port={port};Database={db2};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";

    throw new Exception("Postgres env bulunamadı (DATABASE_URL veya PG*).");
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
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string?> TryCachePg(string connString, string qnorm)
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var sql = @"
SELECT answer, expires_at
FROM qa_cache
WHERE qnorm = @q
ORDER BY id DESC
LIMIT 1;
";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@q", qnorm);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return null;

    var answer = reader.GetString(0);
    var expiresAt = reader.GetDateTime(1); // timestamptz -> DateTime

    if (expiresAt.ToUniversalTime() < DateTime.UtcNow) return null;
    return answer;
}

static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes)
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

    var sql = @"
INSERT INTO qa_cache (qnorm, question, answer, expires_at)
VALUES (@qnorm, @question, @answer, @expires);
";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@qnorm", qnorm);
    cmd.Parameters.AddWithValue("@question", question);
    cmd.Parameters.AddWithValue("@answer", answer);
    cmd.Parameters.AddWithValue("@expires", expires);

    await cmd.ExecuteNonQueryAsync();
}

static async Task<string> AskOpenAI(string question)
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_KEY"); // sende eski isim varsa

    if (string.IsNullOrWhiteSpace(apiKey))
        return "OPENAI_API_KEY (veya OPENAI_KEY) tanımlı değil.";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    // Chat Completions (basit ve stabil)
    var payload = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = "Sen VR içindeki öğretmensin. Cevapları HER ZAMAN Türkçe ver. Kısa, net ve yardımsever ol." },
            new { role = "user", content = question }
        }
    };

    var res = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
    var jsonString = await res.Content.ReadAsStringAsync();

    if (!res.IsSuccessStatusCode)
        return $"OpenAI hata: {(int)res.StatusCode} - {jsonString}";

    using var doc = JsonDocument.Parse(jsonString);
    var root = doc.RootElement;

    // Güvenli parse
    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        return $"OpenAI format beklenmedik: {jsonString}";

    var first = choices[0];
    if (!first.TryGetProperty("message", out var msg))
        return $"OpenAI message yok: {jsonString}";

    if (!msg.TryGetProperty("content", out var contentEl))
        return $"OpenAI content yok: {jsonString}";

    var content = contentEl.GetString();
    return string.IsNullOrWhiteSpace(content) ? "Boş cevap geldi." : content;
}

// ---------- DB init ----------
var pgConn = GetPgConn();
await EnsureDbAsync(pgConn);

// ---------- Endpoints ----------
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/dbcheck", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(pgConn);
        await conn.OpenAsync();

        // tablo var mı diye kontrol
        var sql = "SELECT to_regclass('public.qa_cache') IS NOT NULL;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        var exists = (bool)(await cmd.ExecuteScalarAsync() ?? false);

        return Results.Ok(new { ok = true, qa_cache_table = exists });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/ask", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    Dictionary<string, string>? req;
    try
    {
        req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
    }
    catch
    {
        return Results.BadRequest(new { error = "JSON parse edilemedi. Body: {\"question\":\"...\"} olmalı." });
    }

    if (req == null || !req.TryGetValue("question", out var question) || string.IsNullOrWhiteSpace(question))
        return Results.BadRequest(new { error = "question boş" });

    var qnorm = NormalizeQ(question);
    var timeSensitive = IsTimeSensitive(question);

    // timeSensitive değilse cache’den oku
    if (!timeSensitive)
    {
        var cached = await TryCachePg(pgConn, qnorm);
        if (cached != null)
            return Results.Ok(new { answer = cached, cached = true, timeSensitive = false });
    }

    var answer = await AskOpenAI(question);

    // TTL: timeSensitive 10dk, değilse 30 gün
    var ttlMinutes = timeSensitive ? 10 : (60 * 24 * 30);
    await SaveCachePg(pgConn, qnorm, question, answer, ttlMinutes);

    return Results.Ok(new { answer, cached = false, timeSensitive });
});

app.Run();
