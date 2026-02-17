using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// CORS (Unity ve Web erişimi için)
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway/Docker PORT bind
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

// ---------- Global Değişkenler ----------
string? _dbConnectionString = null;
string? _startupError = null;

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
    // 1. Railway DATABASE_URL (Genellikle bu dolu gelir)
    var url = Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL")
           ?? Environment.GetEnvironmentVariable("DATABASE_PRIVATE_URL");

    if (!string.IsNullOrWhiteSpace(url))
    {
        try 
        {
            var uri = new Uri(url);
            var userInfo = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(userInfo[0]);
            var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            var db = uri.AbsolutePath.TrimStart('/');
            
            return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
        }
        catch(Exception ex)
        {
            Console.WriteLine($"DATABASE_URL parse hatası: {ex.Message}");
        }
    }

    // 2. Manuel PG* Değişkenleri
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");
    var db2 = Environment.GetEnvironmentVariable("PGDATABASE");

    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user2))
        return $"Host={host};Port={port};Database={db2};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";

    throw new Exception("Veritabanı bağlantı bilgileri (DATABASE_URL veya PGHOST) bulunamadı.");
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
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var sql = @"SELECT answer, expires_at FROM qa_cache WHERE qnorm = @q ORDER BY id DESC LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@q", qnorm);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var answer = reader.GetString(0);
        var expiresAt = reader.GetDateTime(1);

        if (expiresAt.ToUniversalTime() < DateTime.UtcNow) return null;
        return answer;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Cache okuma hatası: {ex.Message}");
        return null; // DB hatası varsa cache yokmuş gibi davran
    }
}

static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);
        var sql = @"INSERT INTO qa_cache (qnorm, question, answer, expires_at) VALUES (@qnorm, @question, @answer, @expires);";
        
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qnorm", qnorm);
        cmd.Parameters.AddWithValue("@question", question);
        cmd.Parameters.AddWithValue("@answer", answer);
        cmd.Parameters.AddWithValue("@expires", expires);

        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Cache yazma hatası: {ex.Message}");
    }
}

static async Task<string> AskOpenAI(string question)
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") 
              ?? Environment.GetEnvironmentVariable("OPENAI_KEY");

    if (string.IsNullOrWhiteSpace(apiKey))
        return "Sunucu Hatası: OPENAI_API_KEY tanımlı değil.";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = "Sen VR içindeki bir öğretmensin. Cevapları HER ZAMAN Türkçe ver. Kısa, net ve yardımsever ol." },
            new { role = "user", content = question }
        }
    };

    try 
    {
        var res = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        var jsonString = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            return $"OpenAI API Hatası: {(int)res.StatusCode} - {jsonString}";

        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;

        // GÜVENLİ PARSING (KeyNotFoundException önlemek için)
        if (root.TryGetProperty("error", out var errorEl))
            return $"OpenAI döndürdü: {errorEl.GetRawText()}";

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "OpenAI cevap döndürmedi (choices boş).";

        var first = choices[0];
        if (!first.TryGetProperty("message", out var msg))
            return "OpenAI format hatası (message yok).";

        if (!msg.TryGetProperty("content", out var contentEl))
            return "OpenAI format hatası (content yok).";

        var content = contentEl.GetString();
        return string.IsNullOrWhiteSpace(content) ? "Boş cevap." : content;
    }
    catch (Exception ex)
    {
        return $"OpenAI bağlantı hatası: {ex.Message}";
    }
}

// ---------- Uygulama Başlatma Mantığı ----------
try
{
    Console.WriteLine("Veritabanı bağlantısı hazırlanıyor...");
    _dbConnectionString = GetPgConn();
    await EnsureDbAsync(_dbConnectionString);
    Console.WriteLine("Veritabanı bağlantısı BAŞARILI.");
}
catch (Exception ex)
{
    _startupError = ex.Message;
    Console.WriteLine($"KRİTİK HATA - DB BAŞLATILAMADI: {_startupError}");
    // Uygulamanın çökmesine izin vermiyoruz, böylece /dbcheck endpoint'i hatayı gösterebilir.
}

// ---------- Endpoints ----------

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", time = DateTime.UtcNow }));

app.MapGet("/dbcheck", async () =>
{
    // Eğer başlangıçta hata varsa direkt onu dön
    if (_startupError != null)
        return Results.Problem(detail: _startupError, title: "Startup Database Error");

    if (string.IsNullOrEmpty(_dbConnectionString))
        return Results.Problem("Connection string is empty.");

    try
    {
        await using var conn = new NpgsqlConnection(_dbConnectionString);
        await conn.OpenAsync();

        var sql = "SELECT to_regclass('public.qa_cache') IS NOT NULL;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        var exists = (bool)(await cmd.ExecuteScalarAsync() ?? false);

        return Results.Ok(new { ok = true, table_exists = exists, connection = "Active" });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "DB Connection Check Failed");
    }
});

// JSON Body için güvenli Record tipi
app.MapPost("/ask", async (AskRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Soru (question) boş olamaz." });

    string question = req.Question;
    string qnorm = NormalizeQ(question);
    bool timeSensitive = IsTimeSensitive(question);
    string? answer = null;

    // 1. Cache kontrol (DB hatası varsa atla)
    if (!timeSensitive && _dbConnectionString != null && _startupError == null)
    {
        answer = await TryCachePg(_dbConnectionString, qnorm);
        if (answer != null)
            return Results.Ok(new { answer, cached = true });
    }

    // 2. OpenAI'a sor
    answer = await AskOpenAI(question);

    // 3. Cache'e yaz (DB çalışıyorsa)
    if (_dbConnectionString != null && _startupError == null)
    {
        var ttlMinutes = timeSensitive ? 10 : (60 * 24 * 30);
        // Arka planda kaydet, kullanıcıyı bekletme
        _ = SaveCachePg(_dbConnectionString, qnorm, question, answer, ttlMinutes); 
    }

    return Results.Ok(new { answer, cached = false });
});

app.Run();

// Request Modeli
public record AskRequest(
    [property: JsonPropertyName("question")] string Question
);