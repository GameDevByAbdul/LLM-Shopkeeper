using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Shopkeeper.Core
{
    /// <summary>
    /// Deterministic, data-driven text checks. Intentionally simple and auditable: a production build would
    /// add a moderation model behind this, but the deterministic layer is what guarantees a floor.
    /// </summary>
    public sealed class TextGuard
    {
        public readonly TextGuardDef Def;
        readonly Regex _blocked;
        readonly Regex _offTheme;
        readonly Regex _formatLeak = new Regex(@"[{}]|\b(offered_price|intent|mood)\s*[:=]|\bjson\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public TextGuard(TextGuardDef def)
        {
            Def = def;
            _blocked = Build(def.BlockedTerms, prefix: true);
            _offTheme = Build(def.OffThemeTerms, prefix: false);
        }

        public bool FindBlocked(string text, out string term) => Find(_blocked, text, out term);
        public bool FindOffTheme(string text, out string term) => Find(_offTheme, text, out term);
        public bool LooksLikeFormatLeak(string text) => _formatLeak.IsMatch(text);

        static bool Find(Regex rx, string text, out string term)
        {
            term = null;
            if (rx == null || string.IsNullOrEmpty(text)) return false;
            var m = rx.Match(text);
            if (!m.Success) return false;
            term = m.Value;
            return true;
        }

        static Regex Build(List<string> terms, bool prefix)
        {
            var parts = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t =>
            {
                string esc = Regex.Escape(t.Trim()).Replace(@"\ ", @"\s+");
                bool wordy = char.IsLetterOrDigit(t.Trim()[0]);
                bool wordyEnd = char.IsLetterOrDigit(t.Trim()[t.Trim().Length - 1]);
                // Word boundaries only make sense next to word characters ("$" or ".com" can't use \b).
                return (wordy ? @"\b" : "") + esc + (wordyEnd && !prefix ? @"\b" : "");
            }).ToList();
            if (parts.Count == 0) return null;
            return new Regex("(" + string.Join("|", parts) + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        public static string NormalizeWhitespace(string s)
        {
            if (s == null) return "";
            s = Regex.Replace(s, @"\s+", " ").Trim();
            // Models sometimes wrap the whole line in quotes.
            if (s.Length >= 2 && (s[0] == '"' && s[s.Length - 1] == '"' || s[0] == '“' && s[s.Length - 1] == '”'))
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        /// <summary>Cut to at most <paramref name="max"/> chars, preferring a sentence boundary.</summary>
        public static string TruncateAtSentence(string s, int max)
        {
            if (s.Length <= max) return s;
            int cut = -1;
            for (int i = max - 1; i >= max / 3; i--)
            {
                if (s[i] == '.' || s[i] == '!' || s[i] == '?') { cut = i + 1; break; }
            }
            if (cut > 0) return s.Substring(0, cut).Trim();
            int space = s.LastIndexOf(' ', max - 2);
            return s.Substring(0, space > 0 ? space : max - 1).TrimEnd() + "…";
        }
    }
}
