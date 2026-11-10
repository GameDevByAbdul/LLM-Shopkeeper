using System.Collections.Generic;

namespace Shopkeeper.Core
{
    /// <summary>Hand-written lines used whenever generated text can't be trusted. Picked deterministically from a seed.</summary>
    public sealed class FallbackLibrary
    {
        readonly Dictionary<string, List<string>> _lines;
        readonly string _shopkeeperName;

        public FallbackLibrary(DesignConfig design)
        {
            _lines = design.FallbackLines;
            _shopkeeperName = design.ShopkeeperName;
        }

        public string Pick(string bucket, int seed, ItemDef item, int price, int? offer)
        {
            var list = _lines.TryGetValue(bucket, out var l) && l.Count > 0 ? l : _lines["error"];
            int idx = (int)((uint)(seed * 2654435761u) % (uint)list.Count);
            return list[idx]
                .Replace("{price}", price.ToString())
                .Replace("{offer}", offer?.ToString() ?? "nothing")
                .Replace("{item}", item.Name)
                .Replace("{name}", _shopkeeperName);
        }

        public string Pick(Intent intent, int seed, ItemDef item, int price, int? offer) =>
            Pick(Wire.ToWire(intent), seed, item, price, offer);
    }
}
