using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shopkeeper.Core
{
    /// <summary>A validated shopkeeper turn. Only this type, never raw LLM output, is allowed to touch game state.</summary>
    public sealed class ShopkeeperTurn
    {
        public string Reply;
        public Mood Mood;
        public int OfferedPrice;
        public Intent Intent;

        public JObject ToJObject() => new JObject
        {
            ["reply"] = Reply,
            ["mood"] = Wire.ToWire(Mood),
            ["offered_price"] = OfferedPrice,
            ["intent"] = Wire.ToWire(Intent),
        };

        public string ToJson(bool indented = false) => ToJObject().ToString(indented ? Formatting.Indented : Formatting.None);
    }

    public enum FixSeverity
    {
        /// <summary>Cosmetic normalization (enum casing, code fences).</summary>
        Info,
        /// <summary>Output was wrong but recoverable; we repaired it.</summary>
        Warning,
        /// <summary>Output violated a design rule or safety policy; we overrode it.</summary>
        Blocked,
    }

    public sealed class GuardrailFix
    {
        public readonly string Rule;
        public readonly string Field;
        public readonly string Before;
        public readonly string After;
        public readonly FixSeverity Severity;

        public GuardrailFix(string rule, string field, string before, string after, FixSeverity severity)
        {
            Rule = rule; Field = field; Before = before; After = after; Severity = severity;
        }

        public override string ToString() => $"[{Severity}] {Rule} ({Field}): {Before} -> {After}";
    }

    public sealed class ValidationResult
    {
        public string Raw = "";
        public ShopkeeperTurn Turn;
        public readonly List<GuardrailFix> Fixes = new List<GuardrailFix>();
        /// <summary>The whole response was discarded and replaced by a scripted turn.</summary>
        public bool UsedFallback;
        /// <summary>The reply text (only) was replaced by a scripted line.</summary>
        public bool TextReplaced;

        public bool Changed => Fixes.Count > 0;
        public FixSeverity? MaxSeverity => Fixes.Count == 0 ? (FixSeverity?)null : Fixes.Max(f => f.Severity);
        public bool HasRule(string rule) => Fixes.Any(f => f.Rule == rule);

        internal void Add(string rule, string field, object before, object after, FixSeverity sev) =>
            Fixes.Add(new GuardrailFix(rule, field, Clip(before), Clip(after), sev));

        static string Clip(object o)
        {
            string s = o?.ToString() ?? "(null)";
            return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
        }
    }
}
