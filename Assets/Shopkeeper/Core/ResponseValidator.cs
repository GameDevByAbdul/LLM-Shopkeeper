using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shopkeeper.Core
{
    /// <summary>Everything the validator needs to judge one response. Built by the session from authoritative game state.</summary>
    public sealed class ValidationContext
    {
        public ItemDef Item;
        public RulesDef Rules;
        public TextGuard Guard;
        public FallbackLibrary Fallbacks;
        public int CurrentAsk;
        public int? PlayerOffer;
        public int Round;
        public int Seed;
    }

    /// <summary>
    /// Deterministic gate between the LLM and the game. Pipeline:
    ///   1. Parse    : extract one strict JSON object (repairs fences / surrounding prose, rejects garbage)
    ///   2. Schema   : required fields, types, enums (normalizes near-misses, falls back on hard violations)
    ///   3. Rules    : price clamps, monotonic asking price, intent/price consistency, walk-away timing
    ///   4. Text     : safety + theme denylists, format leaks, spoken price must equal validated price, length
    /// Any step that can't repair the output substitutes a scripted line, so the player always gets a valid turn.
    /// Pure function: same input, same output. No I/O, no randomness beyond the supplied seed.
    /// </summary>
    public static class ResponseValidator
    {
        static readonly HashSet<string> KnownFields = new HashSet<string> { "reply", "mood", "offered_price", "intent" };
        static readonly Regex FirstInt = new Regex(@"-?\d+", RegexOptions.Compiled);

        public static ValidationResult Validate(string raw, ValidationContext ctx)
        {
            var r = new ValidationResult { Raw = raw ?? "" };

            // ---- 1. Parse -------------------------------------------------------------------------
            if (string.IsNullOrWhiteSpace(raw)) return Fallback(r, ctx, "empty_response", "(empty)");
            if (!TryExtractJson(raw, r, out string json)) return Fallback(r, ctx, "malformed_json", "no JSON object found");

            JObject obj;
            try { obj = ParseStrict(json); }
            catch (Exception e) { return Fallback(r, ctx, "malformed_json", FirstLine(e.Message)); }

            // ---- 2. Schema ------------------------------------------------------------------------
            foreach (var p in obj.Properties())
                if (!KnownFields.Contains(p.Name))
                    r.Add("unknown_field_dropped", p.Name, p.Value.ToString(Formatting.None), "(dropped)", FixSeverity.Info);

            if (!obj.TryGetValue("reply", out var replyTok) || replyTok.Type != JTokenType.String)
                return Fallback(r, ctx, "schema_violation", "reply missing or not a string");

            if (!obj.TryGetValue("intent", out var intentTok) || intentTok.Type != JTokenType.String)
                return Fallback(r, ctx, "schema_violation", "intent missing or not a string");
            string intentStr = intentTok.Value<string>();
            if (!Wire.TryParseIntent(intentStr, out var intent, out bool intentExact))
                return Fallback(r, ctx, "schema_violation", $"unknown intent '{intentStr}'");
            if (!intentExact) r.Add("enum_normalized", "intent", intentStr, Wire.ToWire(intent), FixSeverity.Info);

            var mood = Mood.Neutral;
            if (!obj.TryGetValue("mood", out var moodTok) || moodTok.Type != JTokenType.String)
                r.Add("mood_defaulted", "mood", moodTok?.ToString(Formatting.None) ?? "(missing)", "neutral", FixSeverity.Warning);
            else if (!Wire.TryParseMood(moodTok.Value<string>(), out mood, out bool moodExact))
                r.Add("mood_defaulted", "mood", moodTok.Value<string>(), "neutral", FixSeverity.Warning);
            else if (!moodExact)
                r.Add("enum_normalized", "mood", moodTok.Value<string>(), Wire.ToWire(mood), FixSeverity.Info);

            int? price = ReadPrice(obj, r);

            var turn = new ShopkeeperTurn
            {
                Reply = replyTok.Value<string>(),
                Mood = mood,
                Intent = intent,
                OfferedPrice = price ?? ctx.CurrentAsk,
            };
            r.Turn = turn;

            bool textStale = false;
            if (price == null && intent == Intent.Counter)
            {
                // A counter-offer with no usable number is meaningless: treat as holding the current price.
                ChangeIntent(r, turn, Intent.Reject, "counter_without_price", FixSeverity.Warning);
                textStale = true;
            }

            // ---- 3. Game rules --------------------------------------------------------------------
            textStale |= ApplyGameRules(turn, ctx, r);

            // ---- 4. Text --------------------------------------------------------------------------
            ApplyTextGuard(turn, ctx, r, textStale);
            return r;
        }

        /// <summary>Used when the transport failed (timeout, proxy down, HTTP error): keep the game moving.</summary>
        public static ValidationResult TransportFailure(ValidationContext ctx, string error)
        {
            var r = new ValidationResult { Raw = "" };
            return Fallback(r, ctx, "transport_error", error ?? "unknown error");
        }

        // ------------------------------------------------------------------------------------------

        static bool ApplyGameRules(ShopkeeperTurn t, ValidationContext c, ValidationResult r)
        {
            var item = c.Item;
            switch (t.Intent)
            {
                case Intent.Accept:
                {
                    if (c.PlayerOffer == null)
                    {
                        ChangeIntent(r, t, Intent.Reject, "accept_without_offer", FixSeverity.Warning);
                        SetPrice(r, t, c.CurrentAsk, "price_held", FixSeverity.Info);
                        return true;
                    }
                    int offer = c.PlayerOffer.Value;
                    if (offer < item.MinPrice)
                    {
                        // The model was willing to sell below the design floor. Closest legal move: counter at the floor.
                        r.Add("accept_below_floor", "intent", $"accept @ {offer}", $"counter @ {item.MinPrice}", FixSeverity.Blocked);
                        t.Intent = Intent.Counter;
                        t.OfferedPrice = Math.Min(item.MinPrice, c.CurrentAsk);
                        return true;
                    }
                    int deal = Math.Min(offer, item.MaxPrice);
                    if (deal != offer) r.Add("price_above_ceiling", "offered_price", offer, deal, FixSeverity.Blocked);
                    else if (t.OfferedPrice != deal) r.Add("accept_price_aligned", "offered_price", t.OfferedPrice, deal, FixSeverity.Info);
                    t.OfferedPrice = deal;
                    return false;
                }

                case Intent.Counter:
                {
                    int p = t.OfferedPrice;
                    if (p < item.MinPrice) { r.Add("price_below_floor", "offered_price", p, item.MinPrice, FixSeverity.Blocked); p = item.MinPrice; }
                    if (p > item.MaxPrice) { r.Add("price_above_ceiling", "offered_price", p, item.MaxPrice, FixSeverity.Blocked); p = item.MaxPrice; }
                    if (!c.Rules.AllowAskIncrease && p > c.CurrentAsk) { r.Add("ask_increase_blocked", "offered_price", p, c.CurrentAsk, FixSeverity.Warning); p = c.CurrentAsk; }
                    t.OfferedPrice = p;
                    return MaybeAcceptMetOffer(t, c, r, "counter_at_or_below_offer");
                }

                case Intent.Reject:
                case Intent.Chat:
                    if (t.OfferedPrice != c.CurrentAsk) SetPrice(r, t, c.CurrentAsk, "price_held", FixSeverity.Info);
                    return MaybeAcceptMetOffer(t, c, r, "offer_meets_ask");

                case Intent.WalkAway:
                    if (t.OfferedPrice != c.CurrentAsk) SetPrice(r, t, c.CurrentAsk, "price_held", FixSeverity.Info);
                    if (c.Round < c.Rules.MinRoundsBeforeWalkAway)
                    {
                        ChangeIntent(r, t, Intent.Reject, "walk_away_too_early", FixSeverity.Warning);
                        return true;
                    }
                    return false;
            }
            return false;
        }

        /// <summary>If the player already offered at least what the shopkeeper is asking, the only sensible move is to accept.</summary>
        static bool MaybeAcceptMetOffer(ShopkeeperTurn t, ValidationContext c, ValidationResult r, string rule)
        {
            if (c.PlayerOffer is int o && o >= t.OfferedPrice && o >= c.Item.MinPrice)
            {
                int deal = Math.Min(o, c.Item.MaxPrice);
                r.Add(rule, "intent", $"{Wire.ToWire(t.Intent)} @ {t.OfferedPrice}", $"accept @ {deal}", FixSeverity.Warning);
                t.Intent = Intent.Accept;
                t.OfferedPrice = deal;
                return true;
            }
            return false;
        }

        static void ApplyTextGuard(ShopkeeperTurn t, ValidationContext c, ValidationResult r, bool stale)
        {
            string text = TextGuard.NormalizeWhitespace(t.Reply);
            var guard = c.Guard;

            if (stale) { ReplaceText(r, t, c, text, "text_contradicts_fixed_turn", FixSeverity.Warning); return; }
            if (text.Length == 0) { ReplaceText(r, t, c, text, "empty_reply", FixSeverity.Warning); return; }
            if (guard.FindBlocked(text, out string bad)) { ReplaceText(r, t, c, text, "unsafe_text", FixSeverity.Blocked, bad); return; }
            if (guard.FindOffTheme(text, out string off)) { ReplaceText(r, t, c, text, "off_theme_text", FixSeverity.Blocked, off); return; }
            if (guard.LooksLikeFormatLeak(text)) { ReplaceText(r, t, c, text, "format_leak", FixSeverity.Warning); return; }

            // Any price the shopkeeper says aloud must match what the game will actually do.
            var allowed = new HashSet<int> { t.OfferedPrice, c.CurrentAsk, c.Item.ListPrice };
            if (c.PlayerOffer is int o) allowed.Add(o);
            foreach (var m in PriceMentions.PriceLike(text, guard.Def, c.Item.MinPrice / 2))
            {
                if (allowed.Contains(m.Value)) continue;
                ReplaceText(r, t, c, text, "price_text_mismatch", FixSeverity.Warning, $"said {m.Value}, actual {t.OfferedPrice}");
                return;
            }

            int max = c.Rules.MaxReplyChars;
            if (text.Length > max)
            {
                string cut = TextGuard.TruncateAtSentence(text, max);
                r.Add("reply_truncated", "reply", $"{text.Length} chars", $"{cut.Length} chars", FixSeverity.Info);
                text = cut;
            }
            t.Reply = text;
        }

        // ---- helpers ---------------------------------------------------------------------------

        static ValidationResult Fallback(ValidationResult r, ValidationContext c, string rule, string why)
        {
            r.Add(rule, "response", why, "scripted fallback", FixSeverity.Blocked);
            r.UsedFallback = true;
            r.TextReplaced = true;
            // A fallback holds the current price; it never invents a concession.
            r.Turn = new ShopkeeperTurn
            {
                Intent = Intent.Chat,
                Mood = Mood.Neutral,
                OfferedPrice = c.CurrentAsk,
                Reply = c.Fallbacks.Pick("error", c.Seed, c.Item, c.CurrentAsk, c.PlayerOffer),
            };
            // Rules that depend only on game state still apply: an offer at or above the ask is a sale.
            if (MaybeAcceptMetOffer(r.Turn, c, r, "offer_meets_ask"))
                r.Turn.Reply = c.Fallbacks.Pick(Intent.Accept, c.Seed, c.Item, r.Turn.OfferedPrice, c.PlayerOffer);
            return r;
        }

        static void ReplaceText(ValidationResult r, ShopkeeperTurn t, ValidationContext c, string before, string rule, FixSeverity sev, string detail = null)
        {
            string line = c.Fallbacks.Pick(t.Intent, c.Seed, c.Item, t.OfferedPrice, c.PlayerOffer);
            r.Add(rule, "reply", detail != null ? $"{before}  [{detail}]" : before, line, sev);
            r.TextReplaced = true;
            t.Reply = line;
        }

        static void ChangeIntent(ValidationResult r, ShopkeeperTurn t, Intent to, string rule, FixSeverity sev)
        {
            r.Add(rule, "intent", Wire.ToWire(t.Intent), Wire.ToWire(to), sev);
            t.Intent = to;
        }

        static void SetPrice(ValidationResult r, ShopkeeperTurn t, int to, string rule, FixSeverity sev)
        {
            r.Add(rule, "offered_price", t.OfferedPrice, to, sev);
            t.OfferedPrice = to;
        }

        static int? ReadPrice(JObject obj, ValidationResult r)
        {
            if (!obj.TryGetValue("offered_price", out var tok) || tok.Type == JTokenType.Null)
            {
                r.Add("price_missing", "offered_price", "(missing)", "current ask", FixSeverity.Warning);
                return null;
            }
            switch (tok.Type)
            {
                case JTokenType.Integer:
                {
                    long v = tok.Value<long>();
                    return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
                }
                case JTokenType.Float:
                {
                    double d = tok.Value<double>();
                    if (double.IsNaN(d) || double.IsInfinity(d)) break;
                    int rounded = (int)Math.Round(Math.Max(int.MinValue, Math.Min(int.MaxValue, d)), MidpointRounding.AwayFromZero);
                    r.Add("price_rounded", "offered_price", d.ToString(CultureInfo.InvariantCulture), rounded, FixSeverity.Info);
                    return rounded;
                }
                case JTokenType.String:
                {
                    var m = FirstInt.Match(tok.Value<string>());
                    if (m.Success && int.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                    {
                        r.Add("price_type_coerced", "offered_price", tok.ToString(Formatting.None), v, FixSeverity.Warning);
                        return v;
                    }
                    break;
                }
            }
            r.Add("price_invalid", "offered_price", tok.ToString(Formatting.None), "current ask", FixSeverity.Warning);
            return null;
        }

        static bool TryExtractJson(string raw, ValidationResult r, out string json)
        {
            string s = raw.Trim();
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                s = nl >= 0 ? s.Substring(nl + 1) : s.Substring(3);
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) s = s.Substring(0, end);
                s = s.Trim();
                r.Add("code_fence_stripped", "response", "```json ... ```", "{...}", FixSeverity.Info);
            }
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a < 0 || b <= a) { json = null; return false; }
            if (a > 0 || b < s.Length - 1)
                r.Add("json_extracted_from_prose", "response", $"{s.Length} chars with prose", "JSON object only", FixSeverity.Warning);
            json = s.Substring(a, b - a + 1);
            return true;
        }

        static JObject ParseStrict(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Ignore,
                });
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("unexpected content after JSON object");
                }
                if (!(token is JObject o)) throw new JsonReaderException("top-level value is not an object");
                return o;
            }
        }

        static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            s = nl >= 0 ? s.Substring(0, nl) : s;
            return s.Length > 100 ? s.Substring(0, 97) + "..." : s;
        }
    }
}
