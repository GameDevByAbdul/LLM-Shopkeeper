using System;
using System.Collections.Generic;

namespace Shopkeeper.Core
{
    public enum Mood { Neutral, Happy, Amused, Annoyed, Angry }

    /// <summary>What the shopkeeper is doing this turn. The game, not the LLM, decides what each intent does to state.</summary>
    public enum Intent { Counter, Accept, Reject, WalkAway, Chat }

    /// <summary>Wire-format names for the enums, plus tolerant parsing of near-miss values an LLM tends to produce.</summary>
    public static class Wire
    {
        public static readonly string[] MoodValues = { "neutral", "happy", "amused", "annoyed", "angry" };
        public static readonly string[] IntentValues = { "counter", "accept", "reject", "walk_away", "chat" };

        public static string ToWire(Mood m) => MoodValues[(int)m];
        public static string ToWire(Intent i) => IntentValues[(int)i];

        static readonly Dictionary<string, Mood> MoodAliases = new Dictionary<string, Mood>
        {
            { "calm", Mood.Neutral }, { "thoughtful", Mood.Neutral }, { "curious", Mood.Neutral },
            { "pleased", Mood.Happy }, { "delighted", Mood.Happy }, { "content", Mood.Happy }, { "cheerful", Mood.Happy }, { "excited", Mood.Happy },
            { "playful", Mood.Amused }, { "sly", Mood.Amused }, { "smug", Mood.Amused }, { "teasing", Mood.Amused },
            { "irritated", Mood.Annoyed }, { "impatient", Mood.Annoyed }, { "suspicious", Mood.Annoyed }, { "skeptical", Mood.Annoyed }, { "grumpy", Mood.Annoyed },
            { "furious", Mood.Angry }, { "offended", Mood.Angry }, { "insulted", Mood.Angry }, { "outraged", Mood.Angry },
        };

        static readonly Dictionary<string, Intent> IntentAliases = new Dictionary<string, Intent>
        {
            { "counteroffer", Intent.Counter }, { "counter_offer", Intent.Counter }, { "offer", Intent.Counter }, { "haggle", Intent.Counter }, { "negotiate", Intent.Counter },
            { "agree", Intent.Accept }, { "deal", Intent.Accept }, { "accepted", Intent.Accept }, { "sell", Intent.Accept },
            { "refuse", Intent.Reject }, { "decline", Intent.Reject }, { "rejected", Intent.Reject }, { "hold", Intent.Reject },
            { "walkaway", Intent.WalkAway }, { "leave", Intent.WalkAway }, { "end", Intent.WalkAway }, { "end_negotiation", Intent.WalkAway },
            { "small_talk", Intent.Chat }, { "talk", Intent.Chat }, { "smalltalk", Intent.Chat }, { "info", Intent.Chat }, { "describe", Intent.Chat },
        };

        static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

        /// <param name="exact">True when the value matched the canonical wire name with no normalization.</param>
        public static bool TryParseMood(string s, out Mood mood, out bool exact)
        {
            exact = false;
            string n = Normalize(s);
            int idx = Array.IndexOf(MoodValues, n);
            if (idx >= 0) { mood = (Mood)idx; exact = n == s; return true; }
            return MoodAliases.TryGetValue(n, out mood);
        }

        public static bool TryParseIntent(string s, out Intent intent, out bool exact)
        {
            exact = false;
            string n = Normalize(s);
            int idx = Array.IndexOf(IntentValues, n);
            if (idx >= 0) { intent = (Intent)idx; exact = n == s; return true; }
            return IntentAliases.TryGetValue(n, out intent);
        }
    }
}
