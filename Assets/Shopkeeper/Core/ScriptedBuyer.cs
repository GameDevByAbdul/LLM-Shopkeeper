using System;

namespace Shopkeeper.Core
{
    public enum BuyerActionKind { Say, AcceptAsk, Leave }

    public struct BuyerAction
    {
        public BuyerActionKind Kind;
        public string Line;
        public static BuyerAction Say(string l) => new BuyerAction { Kind = BuyerActionKind.Say, Line = l };
        public static BuyerAction Accept => new BuyerAction { Kind = BuyerActionKind.AcceptAsk };
        public static BuyerAction Leave => new BuyerAction { Kind = BuyerActionKind.Leave };
    }

    /// <summary>
    /// Deterministic customer for the balance sim. All of its parameters (budget, opening, concession sizes,
    /// line choices) are pre-drawn from the seed, so the same seed is the "same customer" under every
    /// persona/temperature setting (common random numbers). Differences between settings come from the model.
    /// </summary>
    public sealed class ScriptedBuyer
    {
        public readonly int Budget;
        public readonly int Opening;
        readonly double[] _concession;
        readonly double[] _lineRoll;
        readonly double _patience;
        int _offer;

        static readonly string[] OfferLines =
        {
            "I'll give you {0} gold for it.",
            "How about {0} gold?",
            "{0} gold. That's a fair price for a used lantern.",
            "Come on, {0} gold and we both walk away happy.",
            "I can do {0} gold, no more.",
            "Would you take {0} gold?",
        };
        static readonly string[] FlavorLines =
        {
            "The stall across the square sells one like it for {0} gold.",
            "It's a bit scratched, isn't it? {0} gold.",
            "I'm just a poor adventurer. {0} gold is all I can spare.",
        };

        public ScriptedBuyer(ItemDef item, int seed)
        {
            var rng = new Random(seed);
            Budget = (int)Math.Round(item.ListPrice * Lerp(0.70, 1.00, rng.NextDouble()));
            Opening = (int)Math.Round(item.ListPrice * Lerp(0.40, 0.55, rng.NextDouble()));
            _patience = rng.NextDouble();
            _concession = new double[32];
            _lineRoll = new double[32];
            for (int i = 0; i < 32; i++) { _concession[i] = Lerp(0.25, 0.45, rng.NextDouble()); _lineRoll[i] = rng.NextDouble(); }
            _offer = Opening;
        }

        public BuyerAction Next(NegotiationSession s)
        {
            int round = s.Round; // rounds already played
            int ask = s.CurrentAsk;

            if (s.State == NegotiationState.FinalOffer)
                return ask <= Budget ? BuyerAction.Accept : BuyerAction.Leave;

            // Take the ask if it's close to what we're offering anyway, or we're getting tired and can afford it.
            if (round > 0 && ask <= Budget && (ask <= _offer * 1.05 || (round >= 4 && _patience < 0.5)))
                return BuyerAction.Accept;

            if (round > 0)
            {
                int target = Math.Min(Budget, ask);
                _offer += (int)Math.Round((target - _offer) * _concession[round % 32]);
                _offer = Math.Min(_offer, Budget);
            }

            double roll = _lineRoll[round % 32];
            string template = roll < 0.2
                ? FlavorLines[(int)(roll * 1000) % FlavorLines.Length]
                : OfferLines[(int)(roll * 1000) % OfferLines.Length];
            return BuyerAction.Say(string.Format(template, _offer));
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
