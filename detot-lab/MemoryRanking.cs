internal static class MemoryRanking
{
    private static readonly (string Phrase, string Term)[] Aliases =
    [
        ("thiet bi in", "printer"), ("may in", "printer"), ("printing device", "printer"), ("copier", "printer"),
        ("phong ke toan", "accounting"), ("bo phan tai vu", "accounting"), ("tai vu", "accounting"), ("finance", "accounting"),
        ("hop muc", "toner"), ("het muc", "toner"), ("ink", "toner"),
        ("quay le tan", "reception"), ("le tan", "reception"), ("front desk", "reception"),
        ("giay bi ket", "paperjam"), ("ket giay", "paperjam"), ("stuck sheet", "paperjam"), ("paper jam", "paperjam"),
        ("chung chi bao mat", "certificate"), ("chung chi tls", "certificate"), ("security credential", "certificate"),
        ("trang web", "website"), ("web server", "website"), ("https", "tls")
    ];
    private static readonly HashSet<string> StopWords = new(
        ["a", "an", "the", "in", "at", "on", "for", "of", "which", "where", "has", "needs", "new",
         "cua", "cho", "o", "bo", "phan", "nao", "gi", "hoi", "ve", "toi", "minh"],
        StringComparer.Ordinal);

    private static HashSet<string> Terms(string text)
    {
        var padded = $" {text} ";
        foreach (var (phrase, term) in Aliases)
            padded = padded.Replace($" {phrase} ", $" {term} ", StringComparison.Ordinal);
        return padded.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(term => !StopWords.Contains(term)).ToHashSet(StringComparer.Ordinal);
    }
    internal static double Cosine(ReadOnlySpan<float> query, ReadOnlySpan<float> candidate)
    {
        if (query.IsEmpty || query.Length != candidate.Length) return 0;
        double dot = 0, queryNorm = 0, candidateNorm = 0;
        for (var i = 0; i < query.Length; i++)
        {
            var left = (double)query[i];
            var right = (double)candidate[i];
            if (!double.IsFinite(left) || !double.IsFinite(right)) return 0;
            dot += left * right;
            queryNorm += left * left;
            candidateNorm += right * right;
        }
        return queryNorm > 0 && candidateNorm > 0
            ? Math.Clamp(dot / Math.Sqrt(queryNorm * candidateNorm), 0, 1)
            : 0;
    }

    internal static double Keyword(string normalizedQuery, string normalizedText)
    {
        var query = Terms(normalizedQuery);
        if (query.Count == 0) return 0;
        var terms = Terms(normalizedText);
        return (double)query.Count(terms.Contains) / query.Count;
    }

    internal static double Combine(double semantic, double keyword) =>
        semantic >= 0.54 ? 0.75 * semantic + 0.25 * keyword :
        keyword >= 0.6 ? 0.6 * keyword + 0.4 * semantic : 0;
}
