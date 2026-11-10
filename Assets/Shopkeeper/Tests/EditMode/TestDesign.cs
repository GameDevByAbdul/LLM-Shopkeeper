using System.IO;
using Shopkeeper.Core;

namespace Shopkeeper.Tests
{
    /// <summary>Loads the real design.json so tests exercise shipped data, not a hand-made copy.</summary>
    static class TestDesign
    {
        static DesignConfig _cached;
        static TextGuard _guard;
        static FallbackLibrary _fallbacks;

        public static DesignConfig Load()
        {
            if (_cached != null) return _cached;
            string dir = Directory.GetCurrentDirectory();
            // Unity runs tests from the project root; dotnet test runs from tools/Core.Tests/bin/...
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string p = Path.Combine(dir, "Assets", "StreamingAssets", "Shopkeeper", "design.json");
                if (File.Exists(p)) return _cached = DesignConfig.FromJson(File.ReadAllText(p));
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("design.json not found above " + Directory.GetCurrentDirectory());
        }

        public static ValidationContext Ctx(int currentAsk = 120, int? offer = null, int round = 2, string item = "lantern")
        {
            var d = Load();
            return new ValidationContext
            {
                Item = d.GetItem(item),
                Rules = d.Rules,
                Guard = _guard ?? (_guard = new TextGuard(d.TextGuard)),
                Fallbacks = _fallbacks ?? (_fallbacks = new FallbackLibrary(d)),
                CurrentAsk = currentAsk,
                PlayerOffer = offer,
                Round = round,
                Seed = 7,
            };
        }

        public static string Json(string reply, string mood, object price, string intent) =>
            "{\"reply\":\"" + reply + "\",\"mood\":\"" + mood + "\",\"offered_price\":" + price + ",\"intent\":\"" + intent + "\"}";
    }
}
