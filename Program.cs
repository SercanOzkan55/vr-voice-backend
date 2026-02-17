using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Unity için CORS
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway/Docker: PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

string Normalize(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return "";
    s = s.Trim().ToLowerInvariant();
    while (s.Contains("  ")) s = s.Replace("  ", " ");
    return s;
}

bool IsTimeSensitive(string q)
{
    var x = q.ToLowerInvariant();
    string[] keywords =
    {
        "bugün","yarın","şu an","hava","dolar","euro","kur","haber","son dakika",
        "today","tomorrow","now","weather","price","news","latest"
    };
    return keywords.Any(k => x.Contains(k));
}

string GetPgConn()
{
    // Railway’de genelde bunlar var:
    var url = Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL");

    if (!string.IsNullOrWhiteSpace(url))
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');

        // Railway Postgres çoğu zaman SSL ister
        return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    // Alternatif: PG* env’ler
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var pgPort = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");
    var db2 = Environment.GetEnvironmentVariable("PGDATABASE");

    if (!string.IsNullOrWhiteSpace(host) &&
        !string.IsNullOrWhiteSpace(user2) &&
        !string.IsNullOrWhiteSpace(db2))
    {
        return $"Host={host};Port={pgPort};Database={db2};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    throw new Exception("Postgres env bulunamadı (DATABASE_URL / DATABASE_PUBLIC_URL veya PG* yok).");
}

var pgConn = "";
try
{
    pgConn = GetPgConn();
}
catch
{
    // db yoksa bile servis ayakta kalsın diye boş bırakıyoruz.
    // /dbcheck endpointi zaten net söyleyecek.
    pgConn = "";
}

async Task EnsureTables()
{
    if (string.IsNullOrWhiteSpace(pgConn)) return;

    await using var conn = new NpgsqlConnection(pgConn);
    await conn.OpenAsync();

    var sql = @"
CREATE TABLE IF NOT EXISTS qa_cache (
  id BIGSERIAL PRIMARY KEY,
  qnorm TEXT NOT NULL,
  question TEXT NOT NULL,
  answer TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
";
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

async Task<string?> TryCachePg(string qnorm)
{
    if (string.IsNullOrWhiteSpace(pgConn)) return null;

    await using var conn = new NpgsqlConnection(pgConn);
    await conn.OpenAsync();

    var sql = @"
SELECT answer, expires_at
FROM qa_cache
WHERE qnorm = @q
ORDER BY id DESC
LIMIT 1;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@q", qnorm);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return null;

    var answer = reader.GetString(0);
    var expires = reader.GetDateTime(1);

    if (expires.ToUniversalTime() < DateTime.UtcNow) return null;
    return answer;
}

async Task SaveCachePg(string qnorm, string question, string answer, int ttlMinutes)
{
    if (string.IsNullOrWhiteSpace(pgConn)) return;

    await using var conn = new NpgsqlConnection(pgConn);
    await conn.OpenAsync();

    var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

    var sql = @"
INSERT INTO qa_cache (qnorm, question, answer, expires_at)
VALUES (@qnorm, @question, @answer, @expires_at);";

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@qnorm", qnorm);
    cmd.Parameters.AddWithValue("@question", question);
    cmd.Parameters.AddWithValue("@answer", answer);
    cmd.Parameters.AddWithValue("@expires_at", expires);

    await cmd.ExecuteNonQueryAsync();
}

async Task<string> AskOpenAI(string question)
{
    var key = Environment.GetEnvironmentVariable("OPENAI_KEY");
    if (string.IsNullOrWhiteSpace(key))
        throw new Exception("OPENAI_KEY boş. Railway Variables içine OPENAI_KEY ekle.");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);

    // Not: Burada web_search YOK. O yüzden 500 yemiyoruz.
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
                    "Kısa, net ve yardımsever ol. " +
                    "Eğer internetten canlı veri gerektiren bir şey sorulursa (kur, hava, haber) bunu açıkça söyle ve kullanıcıdan değer/kanıt iste."
            },
            new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = question
            }
        }
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

    // fallback
    return doc.RootElement.GetProperty("output")[0]
        .GetProperty("content")[0]
        .GetProperty("text")
        .GetString() ?? "";
}

// ---- ENDPOINTS ----

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/dbcheck", async () =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(pgConn))
            return Results.Ok(new { ok = false, message = "Postgres bağlantı env yok (DATABASE_URL/PG* yok)." });

        await EnsureTables();

        await using var conn = new NpgsqlConnection(pgConn);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
        var x = await cmd.ExecuteScalarAsync();

        return Results.Ok(new { ok = true, result = x });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/ask", async (HttpRequest request) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
        var question = req != null && req.TryGetValue("question", out var q) ? q : null;

        if (string.IsNullOrWhiteSpace(question))
            return Results.BadRequest(new { error = "question boş" });

        await EnsureTables();

        var qnorm = Normalize(question);
        var timeSensitive = IsTimeSensitive(question);

        // Time-sensitive sorularda: cache okuma yok (yanlış kur/hava istemiyoruz)
        // Ayrıca web yok -> 500 patlamasın diye direkt düzgün mesaj döndürelim.
        if (timeSensitive)
        {
            // İstersen yine OpenAI'ye sorabilir ama canlı veri uydurmasın diye.
            var answerTs = await AskOpenAI(question);
            return Results.Ok(new { answer = answerTs, cached = false, timeSensitive = true });
        }

        // timeSensitive değilse cache bak
        var cached = await TryCachePg(qnorm);
        if (cached != null)
            return Results.Ok(new { answer = cached, cached = true, timeSensitive = false });

        var answer = await AskOpenAI(question);

        // TTL: 30 gün
        var ttlMinutes = 60 * 24 * 30;
        await SaveCachePg(qnorm, question, answer, ttlMinutes);

        return Results.Ok(new { answer, cached = false, timeSensitive = false });
    }
    catch (Exception ex)
    {
        // 500 yerine 200 de dönebiliriz; ama debug için mesajı JSON veriyoruz.
        return Results.StatusCode(500, new { error = ex.Message });
    }
});

app.Run();
