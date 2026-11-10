using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Shopkeeper.Core
{
    /// <summary>
    /// Designer-owned data (StreamingAssets/Shopkeeper/design.json). One source of truth shared by the
    /// Unity runtime and the offline balance simulator, so the sim measures exactly what ships.
    /// </summary>
    public sealed class DesignConfig
    {
        public string ShopkeeperName = "Grizelda";
        public string ShopName = "Grizelda's Curios";
        public RulesDef Rules = new RulesDef();
        public List<ItemDef> Items = new List<ItemDef>();
        public List<PersonaDef> Personas = new List<PersonaDef>();
        public TextGuardDef TextGuard = new TextGuardDef();
        public Dictionary<string, List<string>> FallbackLines = new Dictionary<string, List<string>>();

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            MissingMemberHandling = MissingMemberHandling.Error, // typos in design data should fail loudly
        };

        public static DesignConfig FromJson(string json)
        {
            var cfg = JsonConvert.DeserializeObject<DesignConfig>(json, Settings)
                      ?? throw new FormatException("design.json is empty");
            cfg.Validate();
            return cfg;
        }

        public ItemDef GetItem(string id) =>
            Items.Find(i => i.Id == id) ?? throw new KeyNotFoundException($"No item '{id}' in design.json");

        public PersonaDef GetPersona(string id) =>
            Personas.Find(p => p.Id == id) ?? throw new KeyNotFoundException($"No persona '{id}' in design.json");

        /// <summary>Design-time sanity checks. A broken config is a build error, not a runtime surprise.</summary>
        public void Validate()
        {
            if (Items.Count == 0) throw new FormatException("design.json: no items");
            if (Personas.Count == 0) throw new FormatException("design.json: no personas");
            foreach (var i in Items)
            {
                if (!(0 < i.MinPrice && i.MinPrice <= i.ListPrice && i.ListPrice <= i.MaxPrice))
                    throw new FormatException($"design.json: item '{i.Id}' needs 0 < min <= list <= max");
            }
            if (Rules.MaxRounds < 1) throw new FormatException("design.json: rules.max_rounds must be >= 1");
            foreach (var key in new[] { "greeting", "counter", "accept", "reject", "walk_away", "chat", "error" })
            {
                if (!FallbackLines.TryGetValue(key, out var lines) || lines.Count == 0)
                    throw new FormatException($"design.json: fallback_lines.{key} must have at least one line");
            }
        }
    }

    public sealed class RulesDef
    {
        public int MaxRounds = 8;
        public int MinRoundsBeforeWalkAway = 3;
        public int MaxReplyChars = 240;
        public int HistoryTurns = 6;
        public bool AllowAskIncrease = false;
        public int MaxOutputTokens = 200;
    }

    public sealed class ItemDef
    {
        public string Id;
        public string Name;
        public string Description;
        public int ListPrice;
        /// <summary>Hard floor. The LLM is never told this number; the validator enforces it.</summary>
        public int MinPrice;
        /// <summary>Hard ceiling. Protects the player from a model that invents absurd prices.</summary>
        public int MaxPrice;
    }

    public sealed class PersonaDef
    {
        public string Id;
        public string DisplayName;
        public string Traits;
        public float DefaultTemperature = 0.8f;
    }

    public sealed class TextGuardDef
    {
        /// <summary>Prefix-matched on word boundaries ("shit" also catches "shitty").</summary>
        public List<string> BlockedTerms = new List<string>();
        /// <summary>Whole-word/phrase matched. Breaks immersion: AI self-reference, real-world tech, real currencies.</summary>
        public List<string> OffThemeTerms = new List<string>();
        public List<string> CurrencyWords = new List<string> { "gold", "coin", "coins", "crowns", "gp", "g" };
        public List<string> PriceCueWords = new List<string> { "price", "offer", "pay", "sell", "take", "give", "deal", "cost", "costs", "worth", "for", "at" };
        public List<string> AcceptPhrases = new List<string> { "deal", "i'll take it", "ill take it", "sold", "agreed", "you have a deal" };
    }
}
