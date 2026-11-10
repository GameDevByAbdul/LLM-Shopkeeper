using System.Linq;

namespace Shopkeeper.Core
{
    /// <summary>
    /// Extracts the player's offer with plain code, not the LLM. This is what makes "the shopkeeper agreed
    /// to 1 gold, check the chat log" impossible: the number the game acts on never passes through the model.
    /// </summary>
    public static class OfferParser
    {
        public const int MaxOffer = 100000;

        /// <param name="currentAsk">Used when the player accepts in words ("deal!", "I'll take it").</param>
        public static int? Parse(string playerText, TextGuardDef g, int currentAsk)
        {
            if (string.IsNullOrWhiteSpace(playerText)) return null;

            var mentions = PriceMentions.Scan(playerText, g.CurrencyWords, g.PriceCueWords)
                .Where(m => m.Value >= 1 && m.Value <= MaxOffer)
                .ToList();
            if (mentions.Count > 0)
            {
                // Prefer an explicitly priced number ("I'll pay 70 gold"); otherwise the last number said.
                var priced = mentions.Where(m => m.HasCurrency || m.HasCue).ToList();
                return (priced.Count > 0 ? priced[priced.Count - 1] : mentions[mentions.Count - 1]).Value;
            }

            string lower = " " + TextGuard.NormalizeWhitespace(playerText).ToLowerInvariant().TrimEnd('!', '.', '?') + " ";
            if (lower.Contains(" no ") || lower.Contains(" not ") || lower.Contains("n't ")) return null; // "no deal"
            foreach (var phrase in g.AcceptPhrases)
                if (lower.Contains(" " + phrase + " ")) return currentAsk;
            return null;
        }
    }
}
