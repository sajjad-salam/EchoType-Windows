namespace EchoType.Tasks;

/// <summary>
/// Links text to projects by name or keyword without any model: "feature for the Joker bot",
/// "فكرة للجوكر" or "بالجوكر" all hit a project named Joker with the keyword جوكر. Arabic
/// spelling variants, the ال article and attached prepositions (و ف ب ل ك) are ignored, and
/// longer words may be one letter off.
/// </summary>
internal static class ProjectMatcher {

    private static readonly string[] ArabicPrefixes = [
        "وبال", "وال", "فال", "بال", "كال", "لل", "ال", "و", "ف", "ب", "ل", "ك",
    ];

    public static List<Project> Match(string? text, IEnumerable<Project> projects) {
        var found = new List<Project>();
        if (string.IsNullOrWhiteSpace(text)) {
            return found;
        }
        string normalized = " " + WorkspaceMode.Normalize(text) + " ";
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens) {
            foreach (var stem in Stems(token)) {
                stems.Add(stem);
            }
        }
        foreach (var project in projects) {
            if (Names(project).Any(name => Hits(name, normalized, stems))) {
                found.Add(project);
            }
        }
        return found;
    }

    private static IEnumerable<string> Names(Project project) {
        yield return project.Name;
        foreach (var keyword in project.Keywords) {
            yield return keyword;
        }
    }

    private static bool Hits(string name, string normalizedText, HashSet<string> stems) {
        string n = WorkspaceMode.Normalize(name).Trim();
        if (n.Length < 2) {
            return false;
        }
        if (n.Contains(' ')) {
            // Phrase names match as a whole phrase.
            return normalizedText.Contains(" " + n + " ", StringComparison.Ordinal);
        }
        var nameStems = Stems(n).ToList();
        foreach (var stem in nameStems) {
            if (stems.Contains(stem)) {
                return true;
            }
        }
        if (n.Length >= 5) {
            foreach (var stem in stems) {
                if (Math.Abs(stem.Length - n.Length) <= 1 && Distance(stem, n) <= 1) {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The word, plus the word without an Arabic prefix or English plural/possessive.</summary>
    private static IEnumerable<string> Stems(string token) {
        if (token.Length == 0) {
            yield break;
        }
        yield return token;
        foreach (var prefix in ArabicPrefixes) {
            if (token.Length - prefix.Length >= 2 && token.StartsWith(prefix, StringComparison.Ordinal)) {
                yield return token[prefix.Length..];
            }
        }
        if (token.Length > 3 && token.EndsWith('s')) {
            yield return token[..^1];
        }
    }

    private static int Distance(string a, string b) {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) {
            prev[j] = j;
        }
        for (int i = 1; i <= a.Length; i++) {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
