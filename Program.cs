using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

Console.WriteLine("BUILD_MARKER: 2026-02-18_v8_websearch_fixed_FULL");

// Bind to Railway PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

// --- Config ---
string? dbConn = null;
string? startupError = null;
bool semanticEnabled = false; // pgvector varsa true olacak

const double FUZZY_SIM_THRESHOLD = 0.85;   // pg_trgm similarity
const double SEMANTIC_COSINE_MIN = 0.90;   // cosine similarity
const double OVERLAP_MIN = 0.35;           // keyword overlap guard

const int TTL_TIME_SENSITIVE_MIN = 10;
const int TTL_NORMAL_MIN = 60 * 24 * 30;

// text-embedding-3-small -> 1536
const int EMB_DIM = 1536;

var Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "ve","ile","ama","fakat","şu","bu","o","bir","de","da","mi","mı","mu","mü",
    "nasıl","nedir","ne","kaç","için","tarif","tarifi","yapılır","yapımı",
    "lütfen","kısaca","tek","cümleyle","açıkla","anlat","eder","midir","mıydı",
    "ben","sen","o","biz","siz","onlar","şey","gibi","çok","az"
};

static string NormalizeQ(string s)
{
    if (string.IsNullOrWhiteSpace(s)) return "";
    s = s.Trim().ToLowerInvariant();
    while (s.Contains("  ")) s = s.Replace("  ", " ");
    return s;
}

static bool IsTimeSensitive(string q)
{
    var x = (q ?? "").ToLowerInvariant();
    string[] keywords =
    {
        "bugün","yarın","şu an","hava","dolar","euro","kur","haber","son dakika","maç","skor",
        "today","tomorrow","now","weather","price","news","latest","score","match"
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
        if (url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            var userInfo = uri.UserInfo.Split(':', 2);

            var user = Uri.UnescapeDataString(userInfo[0]);
            var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            var db = uri.AbsolutePath.TrimStart('/');

            return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};Ssl Mode=Require;Trust Server Certificate=true;";
        }

        return url;
    }

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

static NpgsqlDataSource BuildDataSource(string connString)
{
    var b = new NpgsqlDataSourceBuilder(connString);
    // client-side mapping (extension olmasa bile sorun değil)
    b.UseVector();
    return b.Build();
}

IEnumerable<string> KeyTerms(string s)
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

static string? GetOpenAIKey()
{
    return Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? Environment.GetEnvironmentVariable("OPENAI_KEY");
}

// --- Embedding ---
static async Task<float[]?> GetEmbeddingAsync(string text)
{
    var apiKey = GetOpenAIKey();
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new { model = "text-embedding-3-small", input = text };

    var res = await http.PostAsJsonAsync("https://api.openai.com/v1/embeddings", payload);
    var json = await res.Content.ReadAsStringAsync();

    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine("EMBED_FAIL -> " + res.StatusCode + " " + json);
        return null;
    }

    using var doc = JsonDocument.Parse(json);

    var emb = doc.RootElement
        .GetProperty("data")[0]
        .GetProperty("embedding")
        .EnumerateArray()
        .Select(x => x.GetSingle())
        .ToArray();

    if (emb.Length != EMB_DIM)
        Console.WriteLine($"EMBED_WARN -> dim={emb.Length} expected={EMB_DIM}");

    return emb;
}

static async Task<bool> HasVectorAsync(NpgsqlConnection conn)
{
    const string sql = "SELECT EXISTS(SELECT 1 FROM pg_available_extensions WHERE name='vector');";
    await using var cmd = new NpgsqlCommand(sql, conn);
    return (bool)(await cmd.ExecuteScalarAsync()!);
}

static async Task EnsureDbAsync(string connString, Action<bool> setSemanticEnabled)
{
    await using var ds = BuildDataSource(connString);
    await using var conn = await ds.OpenConnectionAsync();

    var baseSql = @"
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS qa_cache (
  id BIGSERIAL PRIMARY KEY,
  qnorm TEXT NOT NULL,
  question TEXT NOT NULL,
  answer TEXT NOT NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm ON qa_cache(qnorm);
CREATE INDEX IF NOT EXISTS idx_qa_cache_expires ON qa_cache(expires_at);
CREATE INDEX IF NOT EXISTS idx_qa_cache_qnorm_trgm ON qa_cache USING gin (qnorm gin_trgm_ops);
";
    await using (var cmd = new NpgsqlCommand(baseSql, conn))
        await cmd.ExecuteNonQueryAsync();

    var hasVector = await HasVectorAsync(conn);
    setSemanticEnabled(hasVector);
    Console.WriteLine("PGVECTOR_AVAILABLE -> " + hasVector);

    if (!hasVector) return;

    var vecSql = $@"
CREATE EXTENSION IF NOT EXISTS vector;

ALTER TABLE qa_cache
ADD COLUMN IF NOT EXISTS embedding vector({EMB_DIM});

CREATE INDEX IF NOT EXISTS idx_qa_cache_embedding
ON qa_cache USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
";
    await using (var cmd2 = new NpgsqlCommand(vecSql, conn))
        await cmd2.ExecuteNonQueryAsync();
}

// --- exact + fuzzy ---
static async Task<(string? answer, bool hit, double sim, string? matchedQnorm)> TryCachePgExactOrFuzzy(
    string connString, string qnorm, double threshold)
{
    try
    {
        await using var ds = BuildDataSource(connString);
        await using var conn = await ds.OpenConnectionAsync();

        // exact
        {
            var sql = @"SELECT answer, expires_at, qnorm
                        FROM qa_cache
                        WHERE qnorm=@q
                        ORDER BY id DESC LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", qnorm);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var expires = reader.GetDateTime(1);
                if (expires.ToUniversalTime() >= DateTime.UtcNow)
                    return (reader.GetString(0), true, 1.0, reader.GetString(2));
            }
        }

        // fuzzy
        {
            var sql = @"
SELECT answer, expires_at, qnorm, similarity(qnorm, @q) AS sim
FROM qa_cache
WHERE expires_at > NOW()
ORDER BY sim DESC, id DESC
LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", qnorm);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (null, false, 0, null);

            var answer = reader.GetString(0);
            var expires = reader.GetDateTime(1);
            var matched = reader.GetString(2);
            var sim = reader.GetDouble(3);

            if (expires.ToUniversalTime() < DateTime.UtcNow) return (null, false, 0, null);

            if (sim >= threshold) return (answer, true, sim, matched);
            return (null, false, sim, matched);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("TRY_FUZZY_FAIL -> " + ex.Message);
        return (null, false, 0, null);
    }
}

// --- semantic cosine + overlap ---
static async Task<(string? answer, bool hit, long? id, double cosine, double overlap, string? matchedQuestion)>
TryCachePgSemantic(string connString, string question, float[] emb, Func<string, IEnumerable<string>> keyTerms)
{
    try
    {
        await using var ds = BuildDataSource(connString);
        await using var conn = await ds.OpenConnectionAsync();

        var qnorm = NormalizeQ(question);

        var sql = @"
SELECT id, question, answer,
       (1 - (embedding <=> @emb)) AS cosine_sim
FROM qa_cache
WHERE embedding IS NOT NULL
  AND expires_at > NOW()
  AND similarity(qnorm, @q) >= 0.55
ORDER BY cosine_sim DESC
LIMIT 25;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@q", qnorm);
        cmd.Parameters.AddWithValue("@emb", new Vector(emb));

        await using var reader = await cmd.ExecuteReaderAsync();

        var qTerms = keyTerms(question).ToArray();

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

            var cTerms = keyTerms(candQ).ToArray();
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
    catch (Exception ex)
    {
        Console.WriteLine("TRY_SEM_FAIL -> " + ex.Message);
        return (null, false, null, 0, 0, null);
    }
}

// --- Web search via Responses API ---
static async Task<string?> AskOpenAI_WebSearch(string question)
{
    var apiKey = GetOpenAIKey();
    if (string.IsNullOrWhiteSpace(apiKey)) return null;

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "gpt-4o-mini",
        tools = new object[]
        {
            new { type = "web_search", search_context_size = "low" }
        },
        input = new object[]
        {
            new { role = "system", content = "Türkçe cevap ver. Kısa ve net ol. Web'den bulduğun bilgiye dayan. Uydurma yapma." },
            new { role = "user", content = question }
        }
    };

    var res = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
    var json = await res.Content.ReadAsStringAsync();

    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine("WEB_SEARCH_FAIL -> " + (int)res.StatusCode + " " + json);
        return null;
    }

    Console.WriteLine("WEB_SEARCH_OK_JSON_FIRST500 -> " + (json.Length > 500 ? json.Substring(0, 500) : json));

    using var doc = JsonDocument.Parse(json);

    if (!doc.RootElement.TryGetProperty("output", out var outputArr) || outputArr.ValueKind != JsonValueKind.Array)
        return null;

    var sb = new StringBuilder();

    foreach (var outItem in outputArr.EnumerateArray())
    {
        if (outItem.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in contentArr.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var ct) && ct.GetString() == "output_text")
                {
                    if (c.TryGetProperty("text", out var txtEl))
                        sb.Append(txtEl.GetString());
                }
            }
        }

        if (outItem.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
            sb.Append(directText.GetString());
    }

    var text = sb.ToString().Trim();
    return string.IsNullOrWhiteSpace(text) ? null : text;
}


// --- save ---
static async Task SaveCachePg(string connString, string qnorm, string question, string answer, int ttlMinutes, float[]? emb, bool semanticEnabled)
{
    try
    {
        await using var ds = BuildDataSource(connString);
        await using var conn = await ds.OpenConnectionAsync();

        var expires = DateTime.UtcNow.AddMinutes(ttlMinutes);

        if (!semanticEnabled || emb == null)
        {
            var sql = @"INSERT INTO qa_cache (qnorm, question, answer, expires_at)
                        VALUES (@qnorm,@question,@answer,@expires);";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@qnorm", qnorm);
            cmd.Parameters.AddWithValue("@question", question);
            cmd.Parameters.AddWithValue("@answer", answer);
            cmd.Parameters.AddWithValue("@expires", expires);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"CACHE_SAVE_OK -> {qnorm} (emb=no)");
            return;
        }

        var sql2 = @"INSERT INTO qa_cache (qnorm, question, answer, embedding, expires_at)
                     VALUES (@qnorm,@question,@answer,@emb,@expires);";
        await using var cmd2 = new NpgsqlCommand(sql2, conn);
        cmd2.Parameters.AddWithValue("@qnorm", qnorm);
        cmd2.Parameters.AddWithValue("@question", question);
        cmd2.Parameters.AddWithValue("@answer", answer);
        cmd2.Parameters.AddWithValue("@emb", new Vector(emb));
        cmd2.Parameters.AddWithValue("@expires", expires);
        await cmd2.ExecuteNonQueryAsync();
        Console.WriteLine($"CACHE_SAVE_OK -> {qnorm} (emb=yes)");
    }
    catch (Exception ex)
    {
        Console.WriteLine("CACHE_SAVE_FAIL -> " + ex.Message);
    }
}

// --- Normal chat (no web) ---
static async Task<string> AskOpenAI_NoWeb(string question)
{
    var apiKey = GetOpenAIKey();
    if (string.IsNullOrWhiteSpace(apiKey))
        return "OPENAI_API_KEY / OPENAI_KEY tanımlı değil.";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var payload = new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = "Sen VR içindeki öğretmensin. Cevapları HER ZAMAN Türkçe ver. Kısa ve net ol." },
            new { role = "user", content = question }
        },
        max_tokens = 400,
        temperature = 0.2
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

        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        return content ?? "boş cevap";
    }
    catch (Exception ex)
    {
        return ex.Message;
    }
}

// --- Main entry for answering ---
static async Task<string> AskOpenAI(string question)
{
    if (IsTimeSensitive(question))
    {
        var webAns = await AskOpenAI_WebSearch(question);
        if (!string.IsNullOrWhiteSpace(webAns))
            return webAns;

        return await AskOpenAI_NoWeb("Kullanıcı canlı veri soruyor (hava/kur/maç/haber). İnternete erişim yoksa uydurma yapma ve dürüstçe söyle: " + question);
    }

    return await AskOpenAI_NoWeb(question);
}

/* ---------- STARTUP ---------- */
try
{
    dbConn = GetPgConn();
    await EnsureDbAsync(dbConn, v => semanticEnabled = v);
    Console.WriteLine("DB OK");
}
catch (Exception ex)
{
    startupError = ex.Message;
    Console.WriteLine($"DB INIT ERROR: {startupError}");
}

/* ---------- ROUTES ---------- */

app.MapGet("/", () => "OK");
app.MapGet("/health", () => Results.Ok(new { ok = true, semanticEnabled }));
app.MapGet("/version", () => "BUILD_2026_02_18_v8_websearch_fixed_FULL");

app.MapGet("/routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var routes = sources.SelectMany(s => s.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText)
        .Distinct()
        .OrderBy(x => x);

    return Results.Ok(routes);
});

app.MapGet("/dbcheck", async () =>
{
    if (startupError != null) return Results.Problem(startupError);
    if (dbConn == null) return Results.Problem("dbConn null");

    try
    {
        await using var ds = BuildDataSource(dbConn);
        await using var conn = await ds.OpenConnectionAsync();
        return Results.Ok(new { ok = true, semanticEnabled });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/cache/count", async () =>
{
    if (startupError != null) return Results.Problem(startupError);
    if (dbConn == null) return Results.Problem("dbConn null");

    await using var ds = BuildDataSource(dbConn);
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM qa_cache;", conn);
    var count = (long)(await cmd.ExecuteScalarAsync()!);
    return Results.Ok(new { count });
});

app.MapGet("/cache/last", async () =>
{
    if (startupError != null) return Results.Problem(startupError);
    if (dbConn == null) return Results.Problem("dbConn null");

    await using var ds = BuildDataSource(dbConn);
    await using var conn = await ds.OpenConnectionAsync();

    var hasEmbColSql = @"
SELECT EXISTS(
  SELECT 1
  FROM information_schema.columns
  WHERE table_name='qa_cache' AND column_name='embedding'
);";
    await using var chk = new NpgsqlCommand(hasEmbColSql, conn);
    var hasEmbCol = (bool)(await chk.ExecuteScalarAsync()!);

    var sql = hasEmbCol
        ? "SELECT id, qnorm, created_at, expires_at, (embedding IS NOT NULL) AS has_emb FROM qa_cache ORDER BY id DESC LIMIT 1;"
        : "SELECT id, qnorm, created_at, expires_at, false AS has_emb FROM qa_cache ORDER BY id DESC LIMIT 1;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.NotFound();

    return Results.Ok(new
    {
        id = r.GetInt64(0),
        qnorm = r.GetString(1),
        created_at = r.GetDateTime(2),
        expires_at = r.GetDateTime(3),
        has_embedding = r.GetBoolean(4),
        semanticEnabled
    });
});

app.MapPost("/ask", async (AskRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "question boş" });

    var sw = Stopwatch.StartNew();

    var question = req.Question;
    var qnorm = NormalizeQ(question);
    var timeSensitive = IsTimeSensitive(question);

    // 1) exact + fuzzy (time-sensitive sorularda cache HIT arama yapma)
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
                semanticEnabled,
                ms = sw.ElapsedMilliseconds
            });
        }
    }

    float[]? emb = null;

    // 2) semantic (sadece pgvector varsa, time-sensitive değilse)
    if (!timeSensitive && semanticEnabled && dbConn != null && startupError == null)
    {
        emb = await GetEmbeddingAsync(question);
        if (emb != null)
        {
            var sem = await TryCachePgSemantic(dbConn, question, emb, KeyTerms);
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
                    semanticEnabled,
                    ms = sw.ElapsedMilliseconds
                });
            }
        }
    }

    // 3) LLM (web search dahil)
    var answer = await AskOpenAI(question);

    // 4) Save  ✅ time-sensitive soruları ASLA kaydetme
    if (!timeSensitive && dbConn != null && startupError == null)
    {
        var ttl = TTL_NORMAL_MIN;

        if (semanticEnabled && emb == null)
            emb = await GetEmbeddingAsync(question);

        await SaveCachePg(dbConn, qnorm, question, answer, ttl, emb, semanticEnabled);
    }

    sw.Stop();
    return Results.Ok(new { answer, cached = false, mode = "llm", semanticEnabled, ms = sw.ElapsedMilliseconds });
});

app.Run();

public record AskRequest(
    [property: JsonPropertyName("question")] string Question
);
