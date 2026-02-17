using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// CORS Ayarları (Her yerden erişime izin ver)
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway Port Ayarı
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

// ---------- Global Değişkenler ----------
string? _dbConnectionString = null;
string? _startupError = null;

// ---------- Yardımcı Fonksiyonlar ----------
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
    string[] keywords = { "bugün", "yarın", "şu an", "hava", "dolar", "euro", "kur", "haber", "saat" };
    return keywords.Any(k => x.Contains(k));
}

static string GetPgConn()
{
    // 1. Railway Otomatik URL
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
        catch { /* Parse hatası olursa manuel değişkenlere geç */ }
    }

    // 2. Manuel Değişkenler
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");
    var db2 = Environment.GetEnvironmentVariable("PGDATABASE");
    var port2 = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";

    if (!string.IsNullOrWhiteSpace(host))
        return $"Host={host};Port={port2};Database={db2};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";

    throw new Exception("DATABASE_URL veya PGHOST bulunamadı.");
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
        CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);";
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string?> TryCachePg(string connString, string qnorm)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        var sql = "SELECT answer, expires_at FROM qa_cache WHERE qnorm = @q ORDER BY id DESC LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@q", qnorm);
        await using var reader = await cmd.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            var answer = reader.GetString(0);
            var expires = reader.GetDateTime(1);
            if (expires.ToUniversalTime() > DateTime.UtcNow) return answer;
        }
        return null;
    }
    catch { return null; }
}

static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);
        var sql = "INSERT INTO qa_cache (qnorm, question, answer, expires_at) VALUES (@qn, @q, @a, @exp)";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qn", qnorm);
        cmd.Parameters.AddWithValue("@q", question);
        cmd.Parameters.AddWithValue("@a", answer);
        cmd.Parameters.AddWithValue("@exp", expires);
        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex) { Console.WriteLine($"Cache Yazma Hatası: {ex.Message}"); }
}

static async Task<string> AskOpenAI(string question)
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey)) return "HATA: OPENAI_API_KEY sunucuda tanımlı değil.";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = "Sen VR öğretmensin. Türkçe cevap ver. Kısa ve net ol." },
            new { role = "user", content = question }
        }
    };

    try
    {
        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        var jsonString = await response.Content.ReadAsStringAsync();
        
        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;
        
        // GÜVENLİ PARSING (Görsel 1'deki hatayı çözer)
        if (root.TryGetProperty("error", out var err)) 
            return $"OpenAI API Hatası: {err.GetRawText()}";

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            // TryGetProperty ile zincirleme kontrol
            if(choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? "Boş cevap.";
            }
        }
        return "OpenAI cevap formatı anlaşılamadı (choices/message/content eksik).";
    }
    catch (Exception ex)
    {
        return $"OpenAI Bağlantı Hatası: {ex.Message}";
    }
}

// ---------- UYGULAMA BAŞLANGICI ----------

// Veritabanını başlatmayı dene ama HATA VERİRSE PATLAMA
try
{
    Console.WriteLine("DB Bağlantısı hazırlanıyor...");
    _dbConnectionString = GetPgConn();
    await EnsureDbAsync(_dbConnectionString);
    Console.WriteLine("DB Başlatma BAŞARILI.");
}
catch (Exception ex)
{
    _startupError = ex.Message;
    Console.WriteLine($"KRİTİK HATA (Startup): {_startupError}");
    // throw new Exception(...) YAPMIYORUZ ki sunucu açılsın ve hatayı görebilelim.
}

// ---------- ENDPOINTS ----------

// 1. Ana Sayfa (Versiyon kontrolü için)
app.MapGet("/", () => Results.Ok("VR Voice Backend V2 (Düzeltilmiş Sürüm) Çalışıyor!"));

// 2. Health Check
app.MapGet("/health", () => Results.Ok(new { status = "OK", time = DateTime.UtcNow }));

// 3. DB Check (Hata ayıklama için en önemlisi)
app.MapGet("/dbcheck", async () =>
{
    if (_startupError != null)
        return Results.Problem(detail: _startupError, title: "Başlangıç Hatası (Startup Error)");

    if (string.IsNullOrEmpty(_dbConnectionString))
        return Results.Problem("Connection string oluşmadı.");

    try
    {
        await using var conn = new NpgsqlConnection(_dbConnectionString);
        await conn.OpenAsync();
        return Results.Ok(new { status = "Bağlı", database = conn.Database });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "Anlık Bağlantı Hatası");
    }
});

// 4. Soru Sorma (Ask)
app.MapPost("/ask", async (AskRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Soru boş olamaz." });

    string qnorm = NormalizeQ(req.Question);
    bool isTimeSensitive = IsTimeSensitive(req.Question);
    string? answer = null;

    // Cache'den oku (Sadece DB sağlamsa)
    if (!isTimeSensitive && _startupError == null && _dbConnectionString != null)
    {
        answer = await TryCachePg(_dbConnectionString, qnorm);
        if (answer != null) return Results.Ok(new { answer, source = "cache" });
    }

    // OpenAI'a sor
    answer = await AskOpenAI(req.Question);

    // Cache'e yaz (Sadece DB sağlamsa)
    if (_startupError == null && _dbConnectionString != null)
    {
        var ttl = isTimeSensitive ? 10 : 43200; // 30 gün
        _ = SaveCachePg(_dbConnectionString, qnorm, req.Question, answer, ttl);
    }

    return Results.Ok(new { answer, source = "openai" });
});

app.Run();

// Request Modeli
public record AskRequest(
    [property: JsonPropertyName("question")] string Question
);