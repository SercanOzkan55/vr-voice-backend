using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway / Docker: dışarıdan gelen PORT'a bind ol
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

// ---------- Postgres bağlantısı ----------
string GetPgConn()
{
    var url = Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL");

    if (!string.IsNullOrWhiteSpace(url))
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');

        return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    var host = Environment.GetEnvironmentVariable("PGHOST");
    var port2 = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");
    var db2 = Environment.GetEnvironmentVariable("PGDATABASE");

    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user2) && !string.IsNullOrWhiteSpace(db2))
        return $"Host={host};Port={port2};Database={db2};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";

    throw new Exception("Postgres env bulunamadı (DATABASE_URL veya PG*).");
}

string pgConn = GetPgConn();

// ---------- Tabloyu oluştur (app.Run() öncesi!) ----------
await using (var conn = new NpgsqlConnection(pgConn))
{
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
CREATE TABLE IF NOT EXISTS qa_cache (
  id BIGSERIAL PRIMARY KEY,
  question TEXT NOT NULL,
  qnorm TEXT NOT NULL,
  answer TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
", conn);
    await cmd.ExecuteNonQueryAsync();
}

// ---------- Helpers ----------
string Normalize(string q)
{
    if (string.IsNullOrWhiteSpace(q)) return "";
    q = q.Trim().ToLowerInvariant();
    while (q.Contains("  ")) q = q.Replace("  ", " ");
    return q;
}

bool IsTimeSensitive(string q)
{
    string x = q.ToLowerInvariant();
    string[] keywords =
    {
        "bugün","yarın","şu an","hava","dolar","euro","kur","haber",
        "today","tomorrow","now","weather","price","news","latest"
    };
    return keywords.Any(k => x.Contains(k));
}

async Task<string?> TryCachePg(string norm)
{
    await using var conn = new NpgsqlConnection(pgConn);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(@"
SELECT answer, expires_at
FROM qa_cache
WHERE qnorm = @q
ORDER BY id DESC
LIMIT 1;", conn);

    cmd.Parameters.AddWithValue("@q", norm);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return null;

    var expiresObj = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTime>(1);

    if (expiresObj != null && expiresObj.Value < DateTime.UtcNow)
        return null;

    return reader.GetString(0);
}

async Task SaveCachePg(string question, string norm, string answer, int ttlMinutes)
{
    var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

    await using var conn = new NpgsqlConnection(pgConn);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(@"
INSERT INTO qa_cache (question, qnorm, answer, expires_at, updated_at)
VALUES (@question, @qnorm, @answer, @expires_at, NOW());", conn);

    cmd.Parameters.AddWithValue("@question", question);
    cmd.Parameters.AddWithValue("@qnorm", norm);
    cmd.Parameters.AddWithValue("@answer", answer);
    cmd.Parameters.AddWithValue("@expires_at", expires);

    await cmd.ExecuteNonQueryAsync();
}

async Task<string> AskOpenAI(string question, bool useWeb)
{
    var key = Environment.GetEnvironmentVariable("OPENAI_KEY");
    if (string.IsNullOrWhiteSpace(key))
        throw new Exception("OPENAI_KEY boş. Railway Variables içine OPENAI_KEY ekle.");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);

    var body = new Dictionary<string, object?>
    {
        ["model"] = "gpt-4.1",
        ["input"] = new object[]
        {
            new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] =
                    "Sen VR içindeki öğretmensin. Cevaplarını HER ZAMAN Türkçe ver. " +
                    "Kısa, net ve yardımsever ol."
            },
            new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = question
            }
        },
        ["tools"] = useWeb ? new object[]
        {
            new Dictionary<string, string> { ["type"] = "web_search" }
        } : null
    };

    var json = JsonSerializer.Serialize(body);
    var res = await http.PostAsync(
        "https://api.openai.com/v1/responses",
        new StringContent(json, Encoding.UTF8, "application/json")
    );

    var txt = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new Exception("OpenAI error: " + (int)res.StatusCode + "\n" + txt);

    using var doc = JsonDocument.Parse(txt);

    if (doc.RootElement.TryGetProperty("output_text", out var outText))
        return outText.GetString() ?? "";

    return doc.RootElement.GetProperty("output")[0]
        .GetProperty("content")[0]
        .GetProperty("text")
        .GetString() ?? "";
}

// ---------- Endpoints ----------
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapPost("/ask", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
    var question = req != null && req.TryGetValue("question", out var q) ? q : null;

    if (string.IsNullOrWhiteSpace(question))
        return Results.BadRequest(new { error = "question boş" });

    var norm = Normalize(question);

    var timeSensitive = IsTimeSensitive(question);

    // Time-sensitive değilse: cache dene
    if (!timeSensitive)
    {
        var cached = await TryCachePg(norm);
        if (cached != null)
            return Results.Ok(new { answer = cached, cached = true, timeSensitive = false });
    }

    // Time-sensitive ise: istersen web tool açık
    var answer = await AskOpenAI(question, useWeb: timeSensitive);

    // TTL: timeSensitive 10dk, değilse 30 gün
    var ttlMinutes = timeSensitive ? 10 : (60 * 24 * 30);
    await SaveCachePg(question, norm, answer, ttlMinutes);

    return Results.Ok(new { answer, cached = false, timeSensitive });
});

app.Run();
