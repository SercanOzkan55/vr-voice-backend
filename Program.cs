using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Unity için CORS (gerekirse kapatırız)
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Railway / Docker: dışarıdan gelen PORT'a bind ol
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

string dbPath = "cache.db";

void InitDb()
{
    using var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();

    var cmd = con.CreateCommand();
    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS qa_cache (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  question_norm TEXT NOT NULL,
  answer TEXT NOT NULL,
  expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_qa_question ON qa_cache(question_norm);
";
    cmd.ExecuteNonQuery();
}
InitDb();

string Normalize(string q) => q.Trim().ToLowerInvariant();

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

string? TryCache(string norm)
{
    using var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();

    var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT answer, expires_at
FROM qa_cache
WHERE question_norm = $q
ORDER BY id DESC
LIMIT 1;";
    cmd.Parameters.AddWithValue("$q", norm);

    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;

    var expires = DateTime.Parse(reader.GetString(1));
    if (expires < DateTime.UtcNow) return null;

    return reader.GetString(0);
}

void SaveCache(string norm, string answer, int ttlMinutes)
{
    using var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();

    var cmd = con.CreateCommand();
    cmd.CommandText = @"
INSERT INTO qa_cache (question_norm, answer, expires_at)
VALUES ($q,$a,$e);";
    cmd.Parameters.AddWithValue("$q", norm);
    cmd.Parameters.AddWithValue("$a", answer);
    cmd.Parameters.AddWithValue("$e", DateTime.UtcNow.AddMinutes(ttlMinutes).ToString("o"));

    cmd.ExecuteNonQuery();
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
        // useWeb true ise web tool'u ver
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

    // fallback
    return doc.RootElement.GetProperty("output")[0]
        .GetProperty("content")[0]
        .GetProperty("text")
        .GetString() ?? "";
}

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

    // time-sensitive soruları cache'ten çekme (yanlış hava durumu vs istemiyorsun)
    var timeSensitive = IsTimeSensitive(question);
    if (!timeSensitive)
    {
        var cached = TryCache(norm);
        if (cached != null)
            return Results.Ok(new { answer = cached, cached = true, timeSensitive = false });
    }

    var answer = await AskOpenAI(question, useWeb: timeSensitive);

    // TTL: timeSensitive 10dk, değilse 30 gün
    var ttlMinutes = timeSensitive ? 10 : (60 * 24 * 30);
    SaveCache(norm, answer, ttlMinutes);

    return Results.Ok(new { answer, cached = false, timeSensitive });
});

app.Run();
