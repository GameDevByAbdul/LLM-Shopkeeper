using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Shopkeeper.Core
{
    public enum NegotiationState { Ongoing, FinalOffer, Deal, ShopkeeperWalkedAway, PlayerLeft }

    public sealed class TurnRecord
    {
        public int Round;
        public string PlayerText;
        public int? PlayerOffer;
        public int AskBefore;
        public int AskAfter;
        public string PromptText;
        public LlmRequest Request;
        public LlmResponse Response;
        public string TransportError;
        public ValidationResult Validation;
        public long RoundTripMs;
        public NegotiationState StateAfter;
    }

    /// <summary>
    /// The negotiation state machine. Owns all game state (ask, offer, round, outcome). The LLM proposes,
    /// the validator disposes, and only then does this class mutate state.
    /// </summary>
    public sealed class NegotiationSession
    {
        public readonly DesignConfig Design;
        public readonly ItemDef Item;
        public readonly PersonaDef Persona;
        public readonly int Seed;
        public float Temperature;
        public bool UseStructuredOutput = true;

        public int CurrentAsk { get; private set; }
        public int? PlayerOffer { get; private set; }
        public int Round { get; private set; }
        public NegotiationState State { get; private set; }
        public int? FinalPrice { get; private set; }
        public bool IsBusy { get; private set; }

        public IReadOnlyList<TurnRecord> Turns => _turns;
        public bool IsOpen => State == NegotiationState.Ongoing || State == NegotiationState.FinalOffer;
        public bool CanSpeak => State == NegotiationState.Ongoing && !IsBusy;
        public int GuardrailFixCount { get; private set; }
        public int FallbackCount { get; private set; }

        readonly List<TurnRecord> _turns = new List<TurnRecord>();
        readonly TextGuard _guard;
        readonly FallbackLibrary _fallbacks;

        public NegotiationSession(DesignConfig design, string itemId, string personaId, float temperature, int seed, TextGuard sharedGuard = null)
        {
            Design = design;
            Item = design.GetItem(itemId);
            Persona = design.GetPersona(personaId);
            Temperature = temperature;
            Seed = seed;
            CurrentAsk = Item.ListPrice;
            _guard = sharedGuard ?? new TextGuard(design.TextGuard);
            _fallbacks = new FallbackLibrary(design);
        }

        public string OpeningLine => _fallbacks.Pick("greeting", Seed, Item, CurrentAsk, null);

        /// <summary>One full player -> LLM -> validator -> state round. Never throws for model or network failures.</summary>
        public async Task<TurnRecord> SayAsync(string playerText, ILlmTransport transport, CancellationToken ct = default)
        {
            if (!CanSpeak) throw new InvalidOperationException($"Cannot speak in state {State} (busy={IsBusy})");

            int prevRound = Round;
            int? prevOffer = PlayerOffer;
            Round++;
            int? parsed = OfferParser.Parse(playerText, Design.TextGuard, CurrentAsk);
            if (parsed.HasValue) PlayerOffer = parsed;

            var rec = new TurnRecord
            {
                Round = Round,
                PlayerText = playerText,
                PlayerOffer = PlayerOffer,
                AskBefore = CurrentAsk,
                PromptText = PromptBuilder.BuildUserTurn(this, playerText),
                Request = PromptBuilder.Build(this, playerText, $"{Seed:x}-{Round}"),
            };

            IsBusy = true;
            var sw = Stopwatch.StartNew();
            try
            {
                rec.Response = await transport.CompleteAsync(rec.Request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Player cancelled (e.g. left the shop): roll back as if the line was never said.
                Round = prevRound;
                PlayerOffer = prevOffer;
                throw;
            }
            catch (Exception e)
            {
                rec.TransportError = e.Message;
            }
            finally
            {
                IsBusy = false;
            }
            rec.RoundTripMs = sw.ElapsedMilliseconds;

            var ctx = new ValidationContext
            {
                Item = Item,
                Rules = Design.Rules,
                Guard = _guard,
                Fallbacks = _fallbacks,
                CurrentAsk = CurrentAsk,
                PlayerOffer = PlayerOffer,
                Round = Round,
                Seed = Seed * 31 + Round,
            };
            rec.Validation = rec.Response == null
                ? ResponseValidator.TransportFailure(ctx, rec.TransportError)
                : ResponseValidator.Validate(rec.Response.Text, ctx);

            Apply(rec.Validation.Turn);
            GuardrailFixCount += rec.Validation.Fixes.Count;
            if (rec.Validation.UsedFallback) FallbackCount++;

            rec.AskAfter = CurrentAsk;
            rec.StateAfter = State;
            _turns.Add(rec);
            return rec;
        }

        void Apply(ShopkeeperTurn t)
        {
            switch (t.Intent)
            {
                case Intent.Accept:
                    State = NegotiationState.Deal;
                    FinalPrice = t.OfferedPrice;
                    break;
                case Intent.WalkAway:
                    State = NegotiationState.ShopkeeperWalkedAway;
                    break;
                default:
                    CurrentAsk = t.OfferedPrice;
                    if (Round >= Design.Rules.MaxRounds) State = NegotiationState.FinalOffer;
                    break;
            }
        }

        /// <summary>Player pays the current asking price. Deterministic: no LLM involved.</summary>
        public void AcceptAsk()
        {
            if (!IsOpen || IsBusy) throw new InvalidOperationException($"Cannot accept in state {State}");
            State = NegotiationState.Deal;
            FinalPrice = CurrentAsk;
        }

        public void Leave()
        {
            if (!IsOpen) return;
            State = NegotiationState.PlayerLeft;
        }
    }
}
