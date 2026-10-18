using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

// =====================================================================
// RAG modelleri
// =====================================================================

// Bilgi tabanındaki tek bir metin parçası ve onun embedding'i (sayı listesi).
record KnowledgeChunk(
    string SourceId,
    string Source,
    string Text,
    float[] Embedding,
    string? EmbeddingModel = null);

// Arayüzde gösterilen "şu dosya, şu kadar parça" bilgisi.
record SourceInfo(string Id, string Name, int ChunkCount);

// Arama sonucu: bulunan parça ve soruya ne kadar benzediği (0-1 arası skor).
record SearchHit(string Source, string Text, double Score);

// Ollama /api/embed cevabı.
record EmbedResponse(List<float[]>? Embeddings);

// =====================================================================
// Dosyadan düz metin çıkarır (.pdf, .txt, .md)
// =====================================================================
static class DocumentReader
{
    public static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".txt", ".md" };

    public static string ReadText(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName);
        using var stream = file.OpenReadStream();

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(stream);
            return string.Join("\n\n",
                pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

// =====================================================================
// ADIM 1 - PARÇALAMA (chunking)
// Uzun metni ~800 karakterlik parçalara böleriz. Neden?
// - Embedding modeli kısa metinlerde daha iyi çalışır.
// - Soruyla ilgili olmayan kısımları modele göndermek istemeyiz.
// Parçalar arasında 150 karakterlik örtüşme (overlap) bırakırız ki
// bir cümle iki parçanın arasında kalıp anlamını kaybetmesin.
// =====================================================================
static class TextChunker
{
    public static List<string> Split(string text, int maxChars = 800, int overlapChars = 150)
    {
        text = text.Replace("\r\n", "\n").Trim();

        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var end = Math.Min(start + maxChars, text.Length);

            // Mümkünse kelimeyi ortasından değil, paragraf/cümle/boşluk sınırından kes.
            if (end < text.Length)
                end = FindBreak(text, start, end);

            var piece = text[start..end].Trim();
            if (piece.Length > 0)
                chunks.Add(piece);

            if (end >= text.Length) break;

            // Bir sonraki parça, öncekinin son 150 karakterini de içerir (örtüşme).
            start = Math.Max(end - overlapChars, start + 1);
        }

        return chunks;
    }

    private static int FindBreak(string text, int start, int end)
    {
        // En fazla parçanın yarısına kadar geri gidip uygun bir kesme noktası ararız.
        var minEnd = start + (end - start) / 2;

        foreach (var separator in new[] { "\n\n", "\n", ". ", " " })
        {
            var index = text.LastIndexOf(
                separator, end - 1, end - minEnd, StringComparison.Ordinal);

            if (index >= minEnd)
                return index + separator.Length;
        }

        return end;
    }
}

// =====================================================================
// ADIM 2-3-4 - DEPOLAMA ve ARAMA
// Parçaları ve embedding'lerini bellekte tutar, data/knowledge.json
// dosyasına da yazar. Böylece uygulamayı kapatıp açsan da bilgi kalır.
// =====================================================================
class KnowledgeBase
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly int _topK;
    private readonly double _minScore;
    private readonly string _queryPrefix;
    private readonly string _documentPrefix;
    private readonly string _embeddingModel;
    private List<KnowledgeChunk> _chunks = new();

    public KnowledgeBase(
        IHostEnvironment env,
        IConfiguration config,
        ILogger<KnowledgeBase> logger)
    {
        _topK = config.GetValue("Rag:TopK", 4);
        _minScore = config.GetValue("Rag:MinScore", 0.3);
        _queryPrefix = config["Rag:QueryPrefix"] ?? "";
        _documentPrefix = config["Rag:DocumentPrefix"] ?? "";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "embeddinggemma";

        var folder = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "knowledge.json");

        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                _chunks = JsonSerializer.Deserialize<List<KnowledgeChunk>>(json)
                          ?? new List<KnowledgeChunk>();

                // Önceki sürüm model adını saklamıyordu. O sürümde varsayılan embedding modeli kullanılmıştı.
                var migrated = false;
                _chunks = _chunks.Select(chunk =>
                {
                    if (chunk.EmbeddingModel is not null) return chunk;
                    migrated = true;
                    return chunk with { EmbeddingModel = _embeddingModel };
                }).ToList();

                if (migrated) Save();

                if (_chunks.Any(chunk => chunk.EmbeddingModel != _embeddingModel))
                    logger.LogWarning(
                        "Bilgi tabanında farklı embedding modeliyle oluşturulmuş parçalar var. " +
                        "Etkin model {Model}; eski parçaları yeniden yükleyerek indeksle.",
                        _embeddingModel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bilgi tabanı dosyası okunamadı: {Path}", _filePath);
            }
        }
    }

    public bool HasContent
    {
        get
        {
            lock (_lock)
            {
                return _chunks.Any(chunk => chunk.EmbeddingModel == _embeddingModel);
            }
        }
    }

    public List<SourceInfo> ListSources()
    {
        lock (_lock)
        {
            return _chunks
                .GroupBy(chunk => chunk.SourceId)
                .Select(group => new SourceInfo(
                    group.Key, group.First().Source, group.Count()))
                .ToList();
        }
    }

    // Metni parçalar, her parçanın embedding'ini çıkarır ve kaydeder.
    public async Task<SourceInfo> AddAsync(
        string sourceName,
        string text,
        OllamaClient ollama,
        CancellationToken ct)
    {
        var pieces = TextChunker.Split(text);

        // Embedding modeli bazen "bu bir doküman parçası" ön ekiyle daha iyi çalışır.
        var inputs = pieces.Select(piece => _documentPrefix + piece).ToList();
        var embeddings = await ollama.EmbedAsync(inputs, ct);

        var sourceId = Guid.NewGuid().ToString("N");
        var newChunks = pieces
            .Select((piece, i) => new KnowledgeChunk(
                sourceId, sourceName, piece, embeddings[i], _embeddingModel))
            .ToList();

        lock (_lock)
        {
            // Aynı isimli dosya tekrar yüklenirse eskisinin yerine geçer.
            _chunks.RemoveAll(chunk => chunk.Source == sourceName);
            _chunks.AddRange(newChunks);
            Save();
        }

        return new SourceInfo(sourceId, sourceName, newChunks.Count);
    }

    // Soruyu embedding'e çevirir, tüm parçalarla karşılaştırır, en benzer olanları döndürür.
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        OllamaClient ollama,
        CancellationToken ct)
    {
        var queryVectors = await ollama.EmbedAsync(new[] { _queryPrefix + query }, ct);
        var queryVector = queryVectors[0];

        List<KnowledgeChunk> snapshot;
        lock (_lock)
        {
            snapshot = _chunks
                .Where(chunk => chunk.EmbeddingModel == _embeddingModel)
                .ToList();
        }

        return snapshot
            .Select(chunk => new SearchHit(
                chunk.Source, chunk.Text, CosineSimilarity(queryVector, chunk.Embedding)))
            .Where(hit => hit.Score >= _minScore)
            .OrderByDescending(hit => hit.Score)
            .Take(_topK)
            .ToList();
    }

    public bool Remove(string sourceId)
    {
        lock (_lock)
        {
            var removed = _chunks.RemoveAll(chunk => chunk.SourceId == sourceId);
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    private void Save()
    {
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_chunks));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    // Kosinüs benzerliği: iki sayı listesinin "aynı yöne bakma" oranı.
    // 1'e yakınsa anlamca çok benzer, 0'a yakınsa alakasızdır.
    private static double CosineSimilarity(float[] a, float[] b)
    {
        // Uzunluklar farklıysa vektörler farklı modelle üretilmiş demektir.
        if (a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0
            ? 0
            : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
