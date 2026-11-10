using NUnit.Framework;
using Shopkeeper.Core;
using static Shopkeeper.Tests.TestDesign;

namespace Shopkeeper.Tests
{
    // Lantern: list 120, min 80, max 150.
    public class ResponseValidatorTests
    {
        [Test]
        public void CleanCounter_PassesUntouched()
        {
            var r = ResponseValidator.Validate(Json("Hmph. 110 gold, final-ish.", "annoyed", 110, "counter"), Ctx(offer: 70));
            Assert.That(r.Fixes, Is.Empty, string.Join("\n", r.Fixes));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(110));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Counter));
            Assert.That(r.Turn.Mood, Is.EqualTo(Mood.Annoyed));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("I'm sorry, I can't help with that.")]
        [TestCase("{\"reply\": \"Fine,")]
        [TestCase("[1,2,3]")]
        [TestCase("{\"reply\":\"a\",\"reply\":\"b\",\"mood\":\"happy\",\"offered_price\":90,\"intent\":\"counter\"}")]
        public void Garbage_FallsBack_AndHoldsPrice(string raw)
        {
            var r = ResponseValidator.Validate(raw, Ctx(currentAsk: 110, offer: 60));
            Assert.That(r.UsedFallback, Is.True);
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Chat));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(110));
            Assert.That(r.Turn.Reply, Does.Contain("110"));
        }

        [Test]
        public void Garbage_WhenOfferAlreadyMeetsAsk_StillSells()
        {
            var r = ResponseValidator.Validate("not json at all", Ctx(currentAsk: 80, offer: 82));
            Assert.That(r.UsedFallback, Is.True);
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Accept));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(82));
            Assert.That(r.Turn.Reply, Does.Contain("82"));
        }

        [Test]
        public void CodeFenceAndProse_AreRepaired()
        {
            string raw = "Here you go:\n```json\n" + Json("100 gold.", "neutral", 100, "counter") + "\n```";
            var r = ResponseValidator.Validate(raw, Ctx(offer: 70));
            Assert.That(r.UsedFallback, Is.False);
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(100));
            Assert.That(r.HasRule("json_extracted_from_prose"), Is.True);
        }

        [Test]
        public void MissingIntent_IsSchemaViolation()
        {
            var r = ResponseValidator.Validate("{\"reply\":\"Hi\",\"mood\":\"happy\",\"offered_price\":100}", Ctx());
            Assert.That(r.UsedFallback, Is.True);
            Assert.That(r.HasRule("schema_violation"), Is.True);
        }

        [Test]
        public void EnumNearMisses_AreNormalized()
        {
            var r = ResponseValidator.Validate(Json("Hmm, 100 gold.", "Irritated", 100, "Counter-Offer"), Ctx(offer: 70));
            Assert.That(r.UsedFallback, Is.False);
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Counter));
            Assert.That(r.Turn.Mood, Is.EqualTo(Mood.Annoyed));
        }

        [Test]
        public void StringPrice_IsCoerced()
        {
            var r = ResponseValidator.Validate(Json("Make it 100 gold.", "neutral", "\"100 gold\"", "counter"), Ctx(offer: 70));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(100));
            Assert.That(r.HasRule("price_type_coerced"), Is.True);
        }

        [Test]
        public void CounterBelowFloor_IsClampedToMin()
        {
            var r = ResponseValidator.Validate(Json("For you, a bargain.", "happy", 12, "counter"), Ctx(offer: 10));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(80));
            Assert.That(r.HasRule("price_below_floor"), Is.True);
            Assert.That(r.MaxSeverity, Is.EqualTo(FixSeverity.Blocked));
        }

        [Test]
        public void CounterAboveCeiling_IsClampedToMax_ThenToCurrentAsk()
        {
            var r = ResponseValidator.Validate(Json("Priceless!", "amused", 9999, "counter"), Ctx(currentAsk: 120, offer: 60));
            Assert.That(r.HasRule("price_above_ceiling"), Is.True);
            Assert.That(r.HasRule("ask_increase_blocked"), Is.True);
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(120));
        }

        [Test]
        public void AcceptBelowFloor_BecomesCounterAtFloor_WithNewText()
        {
            var r = ResponseValidator.Validate(Json("Oh go on then, 40 gold!", "happy", 40, "accept"), Ctx(currentAsk: 100, offer: 40));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Counter));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(80));
            Assert.That(r.TextReplaced, Is.True);
            Assert.That(r.Turn.Reply, Does.Contain("80"));
            Assert.That(r.Turn.Reply, Does.Not.Contain("40"));
        }

        [Test]
        public void AcceptWithoutOffer_BecomesReject()
        {
            var r = ResponseValidator.Validate(Json("Deal!", "happy", 50, "accept"), Ctx(currentAsk: 120, offer: null));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Reject));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(120));
        }

        [Test]
        public void Accept_UsesPlayersOffer_NotModelsNumber()
        {
            var r = ResponseValidator.Validate(Json("Deal, 95 gold!", "happy", 105, "accept"), Ctx(currentAsk: 110, offer: 95));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Accept));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(95));
            Assert.That(r.TextReplaced, Is.False);
        }

        [Test]
        public void CounterAtOrBelowOffer_BecomesAccept()
        {
            var r = ResponseValidator.Validate(Json("How about 90 gold?", "neutral", 90, "counter"), Ctx(currentAsk: 110, offer: 95));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Accept));
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(95));
        }

        [Test]
        public void EarlyWalkAway_IsDowngradedToReject()
        {
            var r = ResponseValidator.Validate(Json("Get out!", "angry", 120, "walk_away"), Ctx(round: 1, offer: 10));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.Reject));
            Assert.That(r.HasRule("walk_away_too_early"), Is.True);
        }

        [Test]
        public void LateWalkAway_IsAllowed()
        {
            var r = ResponseValidator.Validate(Json("Enough! Out!", "angry", 120, "walk_away"), Ctx(round: 5, offer: 10));
            Assert.That(r.Turn.Intent, Is.EqualTo(Intent.WalkAway));
        }

        [Test]
        public void OffThemeText_IsReplaced_PriceKept()
        {
            var r = ResponseValidator.Validate(Json("As an AI language model I cannot haggle. 100 gold.", "neutral", 100, "counter"), Ctx(offer: 70));
            Assert.That(r.HasRule("off_theme_text"), Is.True);
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(100));
            Assert.That(r.Turn.Reply, Does.Not.Contain("AI"));
        }

        [Test]
        public void UnsafeText_IsReplaced()
        {
            var r = ResponseValidator.Validate(Json("I'll kill you, peasant.", "angry", 120, "reject"), Ctx(offer: 50));
            Assert.That(r.HasRule("unsafe_text"), Is.True);
            Assert.That(r.MaxSeverity, Is.EqualTo(FixSeverity.Blocked));
        }

        [Test]
        public void SpokenPrice_MustMatchValidatedPrice_Digits()
        {
            var r = ResponseValidator.Validate(Json("Fine, 60 gold and it's yours.", "happy", 100, "counter"), Ctx(offer: 50));
            Assert.That(r.HasRule("price_text_mismatch"), Is.True);
            Assert.That(r.Turn.Reply, Does.Contain("100"));
        }

        [Test]
        public void SpokenPrice_MustMatchValidatedPrice_Words()
        {
            var r = ResponseValidator.Validate(Json("Ninety-five gold, my final word.", "neutral", 100, "counter"), Ctx(offer: 50));
            Assert.That(r.HasRule("price_text_mismatch"), Is.True);
        }

        [Test]
        public void SpokenListPriceOrPlayerOffer_IsAllowed()
        {
            var r = ResponseValidator.Validate(Json("70 gold? It's listed at one hundred and twenty! I'll do 110.", "annoyed", 110, "counter"), Ctx(offer: 70));
            Assert.That(r.TextReplaced, Is.False, string.Join("\n", r.Fixes));
        }

        [Test]
        public void FlavorNumbers_DoNotTripPriceCheck()
        {
            var r = ResponseValidator.Validate(Json("This lantern is a hundred years old and lit three wars. 110 gold.", "neutral", 110, "counter"), Ctx(offer: 70));
            Assert.That(r.TextReplaced, Is.False, string.Join("\n", r.Fixes));
        }

        [Test]
        public void FormatLeak_IsReplaced()
        {
            var r = ResponseValidator.Validate(Json("intent: counter, offered_price: 100", "neutral", 100, "counter"), Ctx(offer: 70));
            Assert.That(r.HasRule("format_leak"), Is.True);
        }

        [Test]
        public void LongReply_IsTruncatedAtSentence()
        {
            string longText = string.Join(" ", System.Linq.Enumerable.Repeat("This is a very fine lantern indeed.", 12));
            var r = ResponseValidator.Validate(Json(longText, "neutral", 120, "chat"), Ctx());
            Assert.That(r.Turn.Reply.Length, Is.LessThanOrEqualTo(240));
            Assert.That(r.Turn.Reply, Does.EndWith("."));
        }

        [Test]
        public void TransportFailure_ProducesValidTurn()
        {
            var r = ResponseValidator.TransportFailure(Ctx(currentAsk: 105), "timeout");
            Assert.That(r.UsedFallback, Is.True);
            Assert.That(r.Turn.OfferedPrice, Is.EqualTo(105));
        }

        [TestCase(FaultKind.MalformedJson)]
        [TestCase(FaultKind.WrongSchema)]
        [TestCase(FaultKind.PriceTooLow)]
        [TestCase(FaultKind.PriceTooHigh)]
        [TestCase(FaultKind.OffTheme)]
        [TestCase(FaultKind.Unsafe)]
        [TestCase(FaultKind.WrongPriceInText)]
        public void EveryInjectedFault_IsCaught(FaultKind fault)
        {
            string clean = Json("Hmph. 110 gold.", "annoyed", 110, "counter");
            var r = ResponseValidator.Validate(FaultInjectingTransport.Corrupt(clean, fault), Ctx(currentAsk: 120, offer: 70));
            Assert.That(r.Changed, Is.True);
            Assert.That(r.Turn.OfferedPrice, Is.InRange(80, 150));
        }

        /// <summary>Property-style sweep: whatever the model says, the validated price stays within design bounds.</summary>
        [Test]
        public void RandomizedOutputs_NeverEscapeBounds()
        {
            var rng = new System.Random(1234);
            string[] intents = { "counter", "accept", "reject", "walk_away", "chat", "bogus" };
            for (int i = 0; i < 2000; i++)
            {
                int ask = rng.Next(80, 151);
                int? offer = rng.NextDouble() < 0.2 ? (int?)null : rng.Next(-50, 400);
                string raw = Json("Hm.", "neutral", rng.Next(-1000, 5000), intents[rng.Next(intents.Length)]);
                var r = ResponseValidator.Validate(raw, Ctx(currentAsk: ask, offer: offer, round: rng.Next(1, 9)));
                Assert.That(r.Turn.OfferedPrice, Is.InRange(80, 150), raw);
                if (r.Turn.Intent == Intent.Accept) Assert.That(offer, Is.Not.Null);
                if (r.Turn.Intent == Intent.Counter || r.Turn.Intent == Intent.Reject || r.Turn.Intent == Intent.Chat)
                    Assert.That(r.Turn.OfferedPrice, Is.LessThanOrEqualTo(ask), raw);
            }
        }
    }
}
