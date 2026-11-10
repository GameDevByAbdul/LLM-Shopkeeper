using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Shopkeeper.Core
{
    public struct NumberMention
    {
        public int Value;
        public int Start;
        public int Length;
        public bool IsDigits;
        /// <summary>A currency word follows ("90 gold", "ninety coins").</summary>
        public bool HasCurrency;
        /// <summary>A pricing cue word is nearby ("pay 90", "my price is ninety").</summary>
        public bool HasCue;

        public override string ToString() => $"{Value}@{Start}{(HasCurrency ? " $" : "")}{(HasCue ? " cue" : "")}";
    }

    /// <summary>
    /// Finds numbers in free text, both digits ("120") and English words ("one hundred and twenty").
    /// Used to (a) parse the player's offer deterministically and (b) catch shopkeeper replies whose
    /// spoken price disagrees with the validated price.
    /// </summary>
    public static class PriceMentions
    {
        static readonly Regex TokenRx = new Regex(@"\d[\d,]*|[A-Za-z']+", RegexOptions.Compiled);

        static readonly Dictionary<string, int> Small = new Dictionary<string, int>
        {
            { "zero", 0 }, { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 }, { "five", 5 }, { "six", 6 },
            { "seven", 7 }, { "eight", 8 }, { "nine", 9 }, { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 },
            { "thirteen", 13 }, { "fourteen", 14 }, { "fifteen", 15 }, { "sixteen", 16 }, { "seventeen", 17 },
            { "eighteen", 18 }, { "nineteen", 19 }, { "twenty", 20 }, { "thirty", 30 }, { "forty", 40 },
            { "fifty", 50 }, { "sixty", 60 }, { "seventy", 70 }, { "eighty", 80 }, { "ninety", 90 },
        };

        struct Tok { public string Text; public string Lower; public int Start; public int End; }

        public static List<NumberMention> Scan(string text, ICollection<string> currencyWords, ICollection<string> cueWords)
        {
            var result = new List<NumberMention>();
            if (string.IsNullOrEmpty(text)) return result;

            var toks = new List<Tok>();
            foreach (Match m in TokenRx.Matches(text))
                toks.Add(new Tok { Text = m.Value, Lower = m.Value.ToLowerInvariant(), Start = m.Index, End = m.Index + m.Length });

            for (int i = 0; i < toks.Count; i++)
            {
                int first = i, last = i;
                int value;
                bool digits;

                if (char.IsDigit(toks[i].Text[0]))
                {
                    if (!int.TryParse(toks[i].Text.TrimEnd(',').Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out value))
                        continue;
                    digits = true;
                }
                else if (IsNumberWord(toks[i].Lower) || (toks[i].Lower == "a" && i + 1 < toks.Count && IsScaleWord(toks[i + 1].Lower)))
                {
                    // Greedily consume a run like "one hundred and twenty five" / "a hundred".
                    while (last + 1 < toks.Count)
                    {
                        string next = toks[last + 1].Lower;
                        if (IsNumberWord(next)) { last++; continue; }
                        if (next == "and" && last + 2 < toks.Count && IsNumberWord(toks[last + 2].Lower) && RunHasScale(toks, first, last)) { last++; continue; }
                        break;
                    }
                    value = WordsToInt(toks, first, last);
                    digits = false;
                }
                else continue;

                var mention = new NumberMention
                {
                    Value = value,
                    Start = toks[first].Start,
                    Length = toks[last].End - toks[first].Start,
                    IsDigits = digits,
                    HasCurrency = AnyWithin(toks, last + 1, last + 2, currencyWords),
                    HasCue = AnyWithin(toks, first - 3, first - 1, cueWords) || AnyWithin(toks, last + 1, last + 3, cueWords),
                };
                result.Add(mention);
                i = last;
            }
            return result;
        }

        /// <summary>
        /// Mentions that plausibly refer to a price. Small numbers and cue-less words ("a hundred years old")
        /// are ignored so flavor text doesn't trip the guardrail.
        /// </summary>
        public static IEnumerable<NumberMention> PriceLike(string text, TextGuardDef g, int minPlausible)
        {
            foreach (var m in Scan(text, g.CurrencyWords, g.PriceCueWords))
            {
                if (m.Value < 10 && !m.HasCurrency) continue;
                if (m.HasCurrency) { yield return m; continue; }
                if (m.Value < minPlausible) continue;
                if (m.IsDigits || m.HasCue) yield return m;
            }
        }

        static bool IsNumberWord(string w) => Small.ContainsKey(w) || IsScaleWord(w);
        static bool IsScaleWord(string w) => w == "hundred" || w == "thousand";

        static bool RunHasScale(List<Tok> toks, int a, int b)
        {
            for (int k = a; k <= b; k++) if (IsScaleWord(toks[k].Lower)) return true;
            return false;
        }

        static int WordsToInt(List<Tok> toks, int a, int b)
        {
            int total = 0, current = 0;
            for (int k = a; k <= b; k++)
            {
                string w = toks[k].Lower;
                if (w == "and" || w == "a") continue;
                if (w == "hundred") current = (current == 0 ? 1 : current) * 100;
                else if (w == "thousand") { total += (current == 0 ? 1 : current) * 1000; current = 0; }
                else current += Small[w];
            }
            return total + current;
        }

        static bool AnyWithin(List<Tok> toks, int from, int to, ICollection<string> words)
        {
            for (int k = Math.Max(0, from); k <= to && k < toks.Count; k++)
                if (words.Contains(toks[k].Lower)) return true;
            return false;
        }
    }
}
