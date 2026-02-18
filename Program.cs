using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Build marker
Console.WriteLine("BUILD_MARKER: 2026-02-18_v4_semanticcache");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

string? dbConn = null;
string? startupError = null;

// --- Cache thresholds (konservatif / agresif değil) ---
const double FUZZY_SIM_THRESHOLD = 0.85;   // pg_trgm similarity
const double SEMANTIC_COSINE_MIN = 0.90;   // embedding cosine similarity
const double OVERLAP_MIN = 0.35;           // ana kelime overlap güvenliği

const int TTL_TIME_SENSITIVE_MIN = 10;
const int TTL_NORMAL_MIN = 60 * 24 * 30;

// Embedding boyutu (text-embedding-3-small -> 1536)
const int EMB_DIM = 1536;

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

    // PG* fallback
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var p = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var dbn = Environment.GetEnvironmentVariable("PGDATABASE");
    var user2 = Environment.GetEnvironmentVariable("PGUSER");
    var pass2 = Environment.GetEnvironmentVariable("PGPASSWORD");

    if (!string.IsNullOrWhiteSpace(host) &&
        !string.IsNullOrWhiteSpace(dbn) &&
        !string.IsNullOrWhiteSpace(user2) &&
        !string.IsNullOrWhiteSpace(pass2))
    {
        return $"Host={host};Port={p};Database={dbn};Username={user2};Password={pass2};Ssl Mode=Require;Trust Server Certificate=true;";
    }

    throw new Exception("DB env bulunamadı. (DATABASE_URL* veya PG* yok)");
}

// --- Stopwords + overlap guard (agresif reuse engeli) ---
static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
{
    "ve","ile","ama","fakat","şu","bu","o","bir","de","da","mi","mı","mu","mü",
    "nasıl","nedir","ne","kaç","için","tarif","tarifi","yapılır","yapımı",
    "lütfen","kısaca","tek","cümleyle","açıkla","anlat","eder","midir","mıydı",
    "ben","sen","o","biz","siz","onlar","şey","gibi","çok","az"
};

static IEnumerable<string> KeyTerms(string s)
{
    if (string.IsNullOrWhiteSpace(s)) yield break;

    var cleaned = new string(s.ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ')
        .ToArray());

    foreach (var w in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        if (w.Length < 3) continue;
        if (Stop.Contains(w)) continue;
        yield return w;
    }
}

static double JaccardDistinct(IEnumerable<string> a, IEnumerable<string> b)
{
    var A = a.Distinct().ToHashSet();
    var B = b.Distinct().ToHashSet();
    if (A.Count == 0 || B.Count == 0) return 0;

    int inter = A.Intersect(B).Count();
    int uni = A.Union(B).Count();
    return uni == 0 ? 0 : (double)inter / uni;
}

// --- OpenAI Embedding (ML kısmı) ---
static async Task<float[]?> GetEmbeddingAsync(string text)
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_KEY");

    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "text-embedding-3-small",
        input = text
    };

    var res = await http.PostAsJsonAsync("https://api.openai.com/v1/embeddings", payload);
    var json = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode) return null;

    using var doc = JsonDocument.Parse(json);

    var emb = doc.RootElement
        .GetProperty("data")[0]
        .GetProperty("embedding")
        .EnumerateArray()
        .Select(x => x.GetSingle())
        .ToArray();

    return emb;
}

static async Task EnsureDbAsync(string connString)
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    // pg_trgm (fuzzy) + vector (semantic embedding) extensions
    var sql = $@"
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS qa_cache (
  id BIGSERIAL PRIMARY KEY,
  qnorm TEXT NOT NULL,
  question TEXT NOT NULL,
  answer TEXT NOT NULL,
  embedding vector({EMB_DIM}),
  expires_at TIMESTAMPTZ NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
CREATE INDEX IF NOT EXISTS idx_qa_cache_expires ON qa_cache(expires_at);

-- Fuzzy arama için GIN index
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm_trgm ON qa_cache USING gin (qnorm gin_trgm_ops);

-- Semantic arama için index (opsiyonel ama iyi)
CREATE INDEX IF NOT EXISTS idx_qa_cache_embedding ON qa_cache USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
";
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

// --- Cache: exact + fuzzy (pg_trgm) ---
static async Task<(string? answer, bool hit, double sim, string? matchedQnorm)> TryCachePgExactOrFuzzy(
    string connString, string qnorm, double threshold)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        // 1) exact
        {
            var sql = @"SELECT answer, expires_at, qnorm FROM qa_cache
                        WHERE qnorm=@q
                        ORDER BY id DESC LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", qnorm);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var expires = reader.GetDateTime(1);
                if (expires.ToUniversalTime() >= DateTime.UtcNow)
                {
                    return (reader.GetString(0), true, 1.0, reader.GetString(2));
                }
            }
        }

        // 2) fuzzy
        {
            var sql = @"
SELECT answer, expires_at, qnorm, similarity(qnorm, @q) AS sim
FROM qa_cache
WHERE expires_at > NOW()
ORDER BY sim DESC, id DESC
LIMIT 1;
";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", qnorm);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (null, false, 0, null);

            var answer = reader.GetString(0);
            var expires = reader.GetDateTime(1);
            var matched = reader.GetString(2);
            var sim = reader.GetDouble(3);

            if (expires.ToUniversalTime() < DateTime.UtcNow) return (null, false, 0, null);

            if (sim >= threshold)
                return (answer, true, sim, matched);

            return (null, false, sim, matched);
        }
    }
    catch
    {
        return (null, false, 0, null);
    }
}

// --- Cache: semantic (pgvector cosine) + overlap guard ---
static async Task<(string? answer, bool hit, long? id, double cosine, double overlap, string? matchedQuestion)>
TryCachePgSemantic(string connString, string question, float[] emb)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        // Önce trigram ile adayları biraz daraltalım (performans + alakasızları azaltır)
        var qnorm = NormalizeQ(question);

        var sql = @"
SELECT id, question, answer,
       (1 - (embedding <=> @emb)) AS cosine_sim
FROM qa_cache
WHERE embedding IS NOT NULL
  AND expires_at > NOW()
  AND similarity(qnorm, @q) >= 0.55
ORDER BY cosine_sim DESC
LIMIT 25;
";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@q", qnorm);
        cmd.Parameters.AddWithValue("@emb", emb);

        await using var reader = await cmd.ExecuteReaderAsync();

        var qTerms = KeyTerms(question).ToArray();

        long bestId = 0;
        string? bestAns = null;
        string? bestQ = null;
        double bestCos = 0;
        double bestOv = 0;
        double bestScore = 0;

        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);
            var candQ = reader.GetString(1);
            var candAns = reader.GetString(2);
            var cosine = reader.GetDouble(3);

            if (cosine < SEMANTIC_COSINE_MIN) continue;

            var cTerms = KeyTerms(candQ).ToArray();
            var overlap = JaccardDistinct(qTerms, cTerms);

            if (overlap < OVERLAP_MIN) continue;

            var score = (0.75 * cosine) + (0.25 * overlap);

            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
                bestAns = candAns;
                bestQ = candQ;
                bestCos = cosine;
                bestOv = overlap;
            }
        }

        if (bestAns != null)
            return (bestAns, true, bestId, bestCos, bestOv, bestQ);

        return (null, false, null, 0, 0, null);
    }
    catch
    {
        return (null, false, null, 0, 0, null);
    }
}

// --- Save (embedding ile) ---
static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes, float[]? emb)
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

        var sql = @"
INSERT INTO qa_cache (qnorm, question, answer, embedding, expires_at)
VALUES (@qnorm,@question,@answer,@emb,@expires);
";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qnorm", qnorm);
        cmd.Parameters.AddWithValue("@question", question);
        cmd.Parameters.AddWithValue("@answer", answer);
        cmd.Parameters.AddWithValue("@expires", expires);

        if (emb == null) cmd.Parameters.AddWithValue("@emb", DBNull.Value);
        else cmd.Parameters.AddWithValue("@emb", emb);

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
    Console.WriteLine($"DB INIT ERROR: {startupError}");
}

/* ---------- ENDPOINTS ---------- */

app.MapGet("/", () => "OK");
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapGet("/version", () => "BUILD_2026_02_18_v4_semanticcache");

app.MapGet("/routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var routes = sources
        .SelectMany(s => s.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText)
        .Distinct()
        .OrderBy(x => x);

    return Results.Ok(routes);
});

app.MapGet("/dbcheck", async () =>
{
    if (startupError != null)
        return Results.Problem(startupError);

    try
    {
        if (dbConn == null) return Results.Problem("dbConn null");

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
        return Results.BadRequest(new { error = "question boş" });

    var sw = Stopwatch.StartNew();

    var question = req.Question;
    var qnorm = NormalizeQ(question);
    var timeSensitive = IsTimeSensitive(question);

    // 1) Cache: exact + fuzzy
    if (!timeSensitive && dbConn != null && startupError == null)
    {
        var cached = await TryCachePgExactOrFuzzy(dbConn, qnorm, FUZZY_SIM_THRESHOLD);
        if (cached.hit && cached.answer != null)
        {
            sw.Stop();
            return Results.Ok(new
            {
                answer = cached.answer,
                cached = true,
                mode = cached.sim >= 0.999 ? "exact" : "fuzzy",
                similarity = cached.sim,
                matched = cached.matchedQnorm,
                ms = sw.ElapsedMilliseconds
            });
        }
    }

    float[]? emb = null;

    // 2) Cache: semantic (embedding) + overlap guard
    if (!timeSensitive && dbConn != null && startupError == null)
    {
        emb = await GetEmbeddingAsync(question);
        if (emb != null)
        {
            var sem = await TryCachePgSemantic(dbConn, question, emb);
            if (sem.hit && sem.answer != null)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    answer = sem.answer,
                    cached = true,
                    mode = "semantic",
                    cosine = sem.cosine,
                    overlap = sem.overlap,
                    matchedQuestion = sem.matchedQuestion,
                    matchedId = sem.id,
                    ms = sw.ElapsedMilliseconds
                });
            }
        }
    }

    // 3) OpenAI
    var answer = await AskOpenAI(question);

    // 4) Save (embedding ile)
    if (dbConn != null && startupError == null)
    {
        if (emb == null) emb = await GetEmbeddingAsync(question);
        var ttl = timeSensitive ? TTL_TIME_SENSITIVE_MIN : TTL_NORMAL_MIN;

        _ = SaveCachePg(dbConn, qnorm, question, answer, ttl, emb);
    }

    sw.Stop();
    return Results.Ok(new { answer, cached = false, mode = "llm", ms = sw.ElapsedMilliseconds });
});

app.Run();

public record AskRequest(
    [property: JsonPropertyName("question")] string Question
);
