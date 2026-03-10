// ============================================================
//  BotStrategy.cs  —  Hard-Bot AI for Thulla / Bhabhi
//
//  Implements the complete pseudo-code algorithm including:
//    • First-turn logic
//    • Leading logic  (Rules 0-4 + LeadChainSafe look-ahead)
//    • Following logic (same rules + LeadChainSafe look-ahead)
//    • Discard algorithm
//    • LeadChainSafe: multi-round look-ahead that lets the bot
//      willingly burn high cards (AS/KS/QS…) when it can see
//      a future trap waiting at the end of the chain.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Stateful bot-strategy engine.
/// One shared instance lives on GameManager (_botStrategy).
/// </summary>
public class BotStrategy
{
    // ── Tracking state ───────────────────────────────────────────────────────
    // pid → suits the player is confirmed void in (played out-of-suit before)
    private readonly Dictionary<string, HashSet<string>> _knownVoids
        = new Dictionary<string, HashSet<string>>();

    // Cards confirmed out of the game via clean-round discards
    private readonly HashSet<string> _discardPile = new HashSet<string>();

    // ALL cards that have been played in any round (clean discard OR Thulla/out-of-suit).
    // Used to skip swapping cards that opponents have already seen — no point
    // upgrading to a card that is publicly known.
    private readonly HashSet<string> _knownCards = new HashSet<string>();

    // Maximum look-ahead depth for LeadChainSafe to avoid infinite loops
    private const int MaxChainDepth = 10;

    // ── Public API ───────────────────────────────────────────────────────────

    public void Reset()
    {
        _knownVoids.Clear();
        _discardPile.Clear();
        _knownCards.Clear();
    }

    /// <summary>
    /// Called after every card is played so the bot learns about voids.
    /// A player who breaks suit is confirmed void in the lead suit.
    /// Every played card — in-suit or out-of-suit — is added to _knownCards.
    /// </summary>
    public void RecordMove(GameState gs, string playerId, string cardCode)
    {
        // Track ALL played cards as known (seen by everyone at the table)
        if (!string.IsNullOrEmpty(cardCode))
            _knownCards.Add(cardCode);

        if (string.IsNullOrEmpty(gs.leadSuit)) return;
        if (GetSuit(cardCode) != gs.leadSuit)
            EnsureVoids(playerId).Add(gs.leadSuit);
    }

    /// <summary>
    /// Called when a player picks up a Thulla pile.
    /// They may have regained cards of suits we thought they were void in.
    /// </summary>
    public void RecordPickup(GameState gs, string playerId, List<string> cards)
    {
        if (!_knownVoids.ContainsKey(playerId)) return;
        foreach (var c in cards)
            _knownVoids[playerId].Remove(GetSuit(c));
    }

    /// <summary>
    /// Called after a clean round so we know those cards are gone forever.
    /// All discarded cards are added to both _discardPile and _knownCards.
    /// </summary>
    public void RecordDiscard(List<string> cards)
    {
        foreach (var c in cards)
        {
            _discardPile.Add(c);
            _knownCards.Add(c);  // also mark as seen/known
        }
    }

    /// <summary>Returns true if this card has already been played in any round.</summary>
    public bool IsKnownCard(string card) => _knownCards.Contains(card);

    /// <summary>Main entry point called by GameManager.AutoPlay.</summary>
    public string ChooseCard(GameState gs, string botId, List<string> hand)
    {
        if (hand == null || hand.Count == 0) return null;

        // ── 3-PLAYER BOT STEAL ───────────────────────────────────────────────
        // When exactly 3 players remain, this bot is leading (no lead suit yet),
        // and the very next active player is ALSO a bot — steal their hand.
        //
        // Why: two bots + one human left. If the leading bot steals the other
        // bot's hand, the stolen bot exits as a winner and we go straight to
        // a clean 1-bot vs 1-human endgame. The stolen cards are added to this
        // bot's hand by ExecuteSteal in GameManager (same as the normal steal
        // path — AutoPlay already handles the "STEAL" return value).
        //
        // Conditions:
        //   1. Exactly 3 active players remain.
        //   2. Bot is currently leading (no lead suit — start of a round).
        //   3. The next active player is a bot (not a human).
        //
        // Example:
        //   Active: [BotA(leading), BotB, Human]
        //   BotA is in lead, 3 players left, next = BotB (bot).
        //   → Return "STEAL". GameManager calls ExecuteSteal(BotA).
        //   → BotB exits as winner. BotA absorbs BotB's cards.
        //   → 2 players left: BotA vs Human. Normal game resumes.
        if (gs.activePlayers.Count == 3 && string.IsNullOrEmpty(gs.leadSuit))
        {
            int myIndex = gs.activePlayers.IndexOf(botId);
            if (myIndex >= 0)
            {
                string nextId = gs.activePlayers[(myIndex + 1) % gs.activePlayers.Count];
                bool nextIsBot = nextId != null && nextId.StartsWith("BOT_");
                if (nextIsBot)
                {
                    Debug.Log($"[BotStrategy] 3-player steal: {botId} steals from {nextId}");
                    return "STEAL";
                }
            }
        }

        if (gs.roundNumber == 1)
            return ChooseFirstTurnCard(gs, botId, hand);

        bool isLeading = string.IsNullOrEmpty(gs.leadSuit);
        return isLeading
            ? LeadingLogic(gs, botId, hand)
            : FollowingLogic(gs, botId, hand);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FIRST TURN
    // ═══════════════════════════════════════════════════════════════════════

    private string ChooseFirstTurnCard(GameState gs, string botId, List<string> hand)
    {
        // Must play AS if leading and holding it
        if (gs.cardsInPlay.Count == 0 && hand.Contains("AS"))
            return "AS";

        var spades = hand.Where(c => GetSuit(c) == "S").ToList();
        if (spades.Count > 0)
            return spades.Count == 1 ? spades[0] : HighestCard(spades);

        return DiscardUsingLowestSuitAlgorithm(hand, "S");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LEADING LOGIC
    // ═══════════════════════════════════════════════════════════════════════

    private string LeadingLogic(GameState gs, string botId, List<string> hand)
    {
        var active   = gs.activePlayers;
        int myIndex  = active.IndexOf(botId);
        if (myIndex < 0) return hand[0];

        string nextId   = active[(myIndex + 1) % active.Count];
        string secondId = active.Count >= 3 ? active[(myIndex + 2) % active.Count] : null;

        // ── Rule 0 : Dominant suits ──────────────────────────────────────────
        var dominantSuits = BuildDominantSuits(hand);

        var sortedHand = hand.OrderByDescending(GetRankValue).ToList();

        // ── Rule 1 : Clean round ─────────────────────────────────────────────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (AllActivePlayers_HaveSuit(gs, botId, suit))
                if (LeadChainSafe(gs, botId, card, 0))
                    return card;
        }

        // ── Rule 2 : Trap next player ────────────────────────────────────────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (!PlayerHasSuit(gs, nextId, suit)) continue;
            if (PlayerHasGreaterCard(gs, nextId, card) && !PlayerHasLowerCard(gs, nextId, card))
                if (LeadChainSafe(gs, botId, card, 0))
                    return card;
        }

        // ── Rule 3 : Trap second player ──────────────────────────────────────
        if (secondId != null)
        {
            foreach (var card in sortedHand)
            {
                string suit = GetSuit(card);
                if (dominantSuits.Contains(suit)) continue;
                if (!PlayerHasSuit(gs, nextId, suit)) continue;
                if (PlayerHasGreaterCard(gs, nextId, card) && PlayerHasLowerCard(gs, nextId, card))
                {
                    if (!PlayerHasSuit(gs, secondId, suit)) continue;
                    if (PlayerHasGreaterCard(gs, secondId, card) && !PlayerHasLowerCard(gs, secondId, card))
                        if (LeadChainSafe(gs, botId, card, 0))
                            return card;
                }
            }
        }

        // ── Rule 4 : Smart fallback — feed under opponent's high card ────────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (!PlayerHasSuit(gs, nextId, suit)) continue;
            if (PlayerHasGreaterCard(gs, nextId, card))
                if (LeadChainSafe(gs, botId, card, 0))
                    return card;
        }

        // ── Final fallback ───────────────────────────────────────────────────
        string leadDiscard = DiscardUsingLowestSuitAlgorithm(hand, "");
        return UpgradeDiscardWithOtherBots(gs, botId, leadDiscard);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FOLLOWING LOGIC
    // ═══════════════════════════════════════════════════════════════════════

    private string FollowingLogic(GameState gs, string botId, List<string> hand)
    {
        // ══════════════════════════════════════════════════════════════════
        //  FOLLOWING LOGIC — COMPLETE PRIORITY ORDER
        //
        //  Core principle: we NEVER want to win this trick and get the lead.
        //  The highest card already on the table is the real ceiling — not
        //  just what the next player holds.
        //
        //  STEP 0 — Find the current highest card on the table (in leadSuit).
        //           This is the "table ceiling". If we play anything below it
        //           we are 100% guaranteed NOT to win this trick.
        //
        //  STEP 1 — SAFE DUMP (highest priority)
        //           If we have any card strictly below the table ceiling,
        //           find the HIGHEST such card. Dump it. We lose no lead,
        //           we remove our strongest possible card safely.
        //
        //           Example from screenshot:
        //             AH is on the table (rank 14) — table ceiling = 14.
        //             Aryan has KH (rank 13). KH < 14 → safe dump.
        //             → Play KH immediately. KH is gone, AH holder wins.
        //
        //           Example 2:
        //             10H on table. We have QH 9H 5H. Next has KH.
        //             Table ceiling = 10. Our cards below 10: 9H, 5H.
        //             → Play 9H (highest below ceiling). Safe dump.
        //
        //  STEP 2 — TRAP via next player (if no safe dump exists)
        //           All our cards are >= table ceiling (we WILL win unless
        //           someone after us beats us). So check: does the next
        //           player have ONLY higher cards of this suit? If yes,
        //           they will beat us → we can play our highest and still
        //           not end up with lead. LeadChainSafe check applies.
        //
        //  STEP 3 — TRAP via second player (if next player has mixed cards)
        //           Next player has both higher and lower cards of suit.
        //           Check if the second-next player has ONLY higher cards.
        //           If yes, second player will beat us → safe to play.
        //
        //  STEP 4 — LAST RESORT
        //           No safe dump, no trap. We WILL win this trick no matter
        //           what. Play the LOWEST card of the suit to minimise the
        //           damage (preserve higher cards for future traps).
        // ══════════════════════════════════════════════════════════════════

        string leadSuit = gs.leadSuit;
        var active      = gs.activePlayers;
        int myIndex     = active.IndexOf(botId);
        if (myIndex < 0) return hand[0];

        string nextId   = active[(myIndex + 1) % active.Count];
        string secondId = active.Count >= 3 ? active[(myIndex + 2) % active.Count] : null;

        var suitCards = hand.Where(c => GetSuit(c) == leadSuit)
                            .OrderByDescending(GetRankValue).ToList();

        if (suitCards.Count > 0)
        {
            // ── STEP 0: Find table ceiling ───────────────────────────────────
            // Highest card of leadSuit already played this trick
            int tableCeiling = 0;
            foreach (var pc in gs.cardsInPlay)
                if (GetSuit(pc.card) == leadSuit)
                    tableCeiling = System.Math.Max(tableCeiling, GetRankValue(pc.card));

            // ── STEP 1A: 4-player clean round — play HIGHEST card ───────────
            //
            // Special case: exactly 4 active players remain AND every player
            // still has cards of the lead suit → this is guaranteed to be a
            // clean discard round (all cards leave the game, no one picks up).
            // In this case play our ABSOLUTE HIGHEST card of the suit — there
            // is zero risk because every card gets discarded regardless of rank.
            //
            // Example:
            //   4 players active. Lead suit = H. All 4 have hearts.
            //   We have QH JH 7H. Table ceiling = 10H.
            //   Normal logic would play 7H (below ceiling).
            //   But it's a guaranteed clean round → play QH instead.
            //   QH gets discarded cleanly. Much better value.
            if (gs.activePlayers.Count == 4 &&
                AllActivePlayers_HaveSuit(gs, botId, leadSuit))
            {
                // Play highest card of suit — entire round discards cleanly
                return suitCards[0];
            }

            // ── STEP 1B: Safe Dump — play highest card below table ceiling ───
            //
            // Any card we play below the ceiling cannot win the trick.
            // Pick the HIGHEST such card to dump our strongest safe card.
            //
            // AH on table (ceiling=14), we have KH(13) QH(12) 9H(9)
            //   → All three are below 14 → play KH (highest safe dump).
            //
            // 10H on table (ceiling=10), we have QH(12) 9H(9) 5H(5)
            //   → Below 10: 9H, 5H → play 9H.
            if (tableCeiling > 0)
            {
                var safeDumps = suitCards.Where(c => GetRankValue(c) < tableCeiling).ToList();
                // suitCards is already sorted highest→lowest, so safeDumps[0] is the best dump
                if (safeDumps.Count > 0)
                    return safeDumps[0];
            }

            // ── STEP 2: Trap next player ─────────────────────────────────────
            //
            // No safe dump exists (all our cards >= ceiling, or no card on table yet).
            // Check: does next player have ONLY higher cards of this suit?
            // If yes → they will beat whatever we play → not our problem.
            //
            // We have QH(12). Next has KH(13) only (no lower H).
            //   → Next has greater(QH)=true, no lower(QH)=true → play QH.
            foreach (var card in suitCards)
                if (PlayerHasGreaterCard(gs, nextId, card) && !PlayerHasLowerCard(gs, nextId, card))
                    if (LeadChainSafe(gs, botId, card, 0))
                        return card;

            // ── STEP 3: Trap second player ───────────────────────────────────
            //
            // Next player has mixed cards (some higher, some lower than ours).
            // They might or might not beat us. Check the second-next player:
            // if they have ONLY higher cards, they guarantee the trick goes to them.
            //
            // We have 9H. Next has QH and 6H (mixed). Second has AH only.
            //   → Next: greater(9H)=true, lower(9H)=true → mixed, skip Rule 2.
            //   → Second: greater(9H)=true, no lower(9H)=true → play 9H.
            if (secondId != null)
                foreach (var card in suitCards)
                    if (PlayerHasGreaterCard(gs, nextId, card) && PlayerHasLowerCard(gs, nextId, card))
                        if (PlayerHasGreaterCard(gs, secondId, card) && !PlayerHasLowerCard(gs, secondId, card))
                            if (LeadChainSafe(gs, botId, card, 0))
                                return card;

            // ── STEP 4: Last resort — play lowest to minimise damage ─────────
            //
            // We cannot avoid winning this trick. Play the LOWEST card of the
            // suit to preserve higher cards for future trap opportunities.
            //
            // We have QH JH 8H. Nobody has a higher H. We will win no matter what.
            //   → Play 8H. Keep QH and JH for leading/trapping later.
            return suitCards.Last();
        }

        // No lead suit → discard, then upgrade the chosen card with other bots
        string discardCard = DiscardUsingLowestSuitAlgorithm(hand, leadSuit);
        return UpgradeDiscardWithOtherBots(gs, botId, discardCard);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LEAD CHAIN SAFE  —  Multi-round look-ahead
    //
    //  Question: "If I play this card right now, and I end up with the lead,
    //             will I always be able to avoid being stuck leading forever
    //             without a trap card?"
    //
    //  Returns TRUE  if the chain eventually reaches a round where the bot
    //                does NOT win the trick (so someone else leads), OR where
    //                the bot wins but already has a valid trap card to play.
    //  Returns FALSE if the chain reaches a round where the bot wins but has
    //                NO card that can trap any opponent — a dead end.
    // ═══════════════════════════════════════════════════════════════════════

    private bool LeadChainSafe(GameState gs, string botId, string card, int depth)
    {
        // Safety cap — treat very deep chains as safe to avoid stalling
        if (depth >= MaxChainDepth) return true;

        // Clone state so simulation doesn't touch real game
        SimState sim = CloneToSim(gs, botId);

        // Simulate playing this card as the lead
        string trickWinner = SimulateTrick(sim, botId, card);

        // If bot doesn't win this trick → someone else leads next → safe ✅
        if (trickWinner != botId) return true;

        // Bot wins and leads again — remove the played card from sim hand
        // (SimulateTrick already removed it; winner's hand is updated in sim)

        List<string> botHandAfter = sim.hands[botId];
        if (botHandAfter.Count == 0) return true; // bot ran out of cards → won the game

        // Check: can the bot find a trap card from the new lead position?
        string trapCard = FindTrapCard(sim, botId, botHandAfter);

        if (trapCard != null)
        {
            // Bot has a trap card available — recurse to make sure that
            // trap card itself doesn't lead to another unsafe chain
            return LeadChainSafe(BuildGameState(sim, gs), botId, trapCard, depth + 1);
        }

        // Bot leads again but has NO trap card → unsafe ❌
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FIND TRAP CARD
    //  Returns the best card the bot can lead that will trap an opponent,
    //  or null if no such card exists in the current hand.
    // ═══════════════════════════════════════════════════════════════════════

    private string FindTrapCard(SimState sim, string botId, List<string> hand)
    {
        var active   = sim.activePlayers;
        int myIndex  = active.IndexOf(botId);
        if (myIndex < 0 || active.Count < 2) return null;

        string nextId   = active[(myIndex + 1) % active.Count];
        string secondId = active.Count >= 3 ? active[(myIndex + 2) % active.Count] : null;

        var dominantSuits = BuildDominantSuitsFromSim(sim, botId, hand);
        var sortedHand    = hand.OrderByDescending(GetRankValue).ToList();

        // Rule 1: clean-round card
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (SimAllHaveSuit(sim, botId, suit)) return card;
        }

        // Rule 2: trap next
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (!SimPlayerHasSuit(sim, nextId, suit)) continue;
            if (SimPlayerHasGreater(sim, nextId, card) && !SimPlayerHasLower(sim, nextId, card))
                return card;
        }

        // Rule 3: trap second
        if (secondId != null)
        {
            foreach (var card in sortedHand)
            {
                string suit = GetSuit(card);
                if (dominantSuits.Contains(suit)) continue;
                if (!SimPlayerHasSuit(sim, nextId, suit)) continue;
                if (SimPlayerHasGreater(sim, nextId, card) && SimPlayerHasLower(sim, nextId, card))
                {
                    if (!SimPlayerHasSuit(sim, secondId, suit)) continue;
                    if (SimPlayerHasGreater(sim, secondId, card) && !SimPlayerHasLower(sim, secondId, card))
                        return card;
                }
            }
        }

        // Rule 4: smart fallback (can feed under opponent's high card)
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (!SimPlayerHasSuit(sim, nextId, suit)) continue;
            if (SimPlayerHasGreater(sim, nextId, card)) return card;
        }

        return null; // No trap card found
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SIMULATION HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight game-state clone used purely for look-ahead simulation.
    /// Only copies the data the chain-checker needs.
    /// </summary>
    private struct SimState
    {
        public List<string>              activePlayers;
        public Dictionary<string, List<string>> hands;
        public HashSet<string>           voids; // flat: "pid|suit"
    }

    private SimState CloneToSim(GameState gs, string botId)
    {
        var sim = new SimState
        {
            activePlayers = new List<string>(gs.activePlayers),
            hands         = new Dictionary<string, List<string>>(),
            voids         = new HashSet<string>()
        };
        foreach (var kv in gs.hands)
            sim.hands[kv.Key] = new List<string>(kv.Value);
        foreach (var kv in _knownVoids)
            foreach (var s in kv.Value)
                sim.voids.Add(kv.Key + "|" + s);
        return sim;
    }

    private GameState BuildGameState(SimState sim, GameState original)
    {
        // Create a minimal GameState wrapper so LeadChainSafe can recurse
        // using the same helper methods that take a GameState parameter.
        var gs = new GameState();
        gs.activePlayers = new List<string>(sim.activePlayers);
        foreach (var kv in sim.hands)
            gs.hands[kv.Key] = new List<string>(kv.Value);
        gs.roundNumber  = original.roundNumber + 1;
        gs.leadSuit     = "";
        gs.cardsInPlay  = new List<PlayedCard>();
        gs.turnStartTime = original.turnStartTime;
        gs.phase        = original.phase;
        gs.winners      = new List<string>(original.winners);
        gs.bhabhi       = original.bhabhi;
        return gs;
    }

    /// <summary>
    /// Simulate one full trick starting with botId playing leadCard.
    /// Every other active player plays their best card of the lead suit
    /// (or their lowest card if void — simulating a Thulla).
    /// Returns the id of the trick winner (highest lead-suit card player).
    /// Updates sim.hands by removing played cards.
    /// </summary>
    private string SimulateTrick(SimState sim, string botId, string leadCard)
    {
        string suit = GetSuit(leadCard);
        var active  = sim.activePlayers;
        int myIdx   = active.IndexOf(botId);

        // Remove bot's lead card
        if (sim.hands.ContainsKey(botId))
            sim.hands[botId].Remove(leadCard);

        string winnerId   = botId;
        int    winnerRank = GetRankValue(leadCard);

        // Each other player responds
        for (int offset = 1; offset < active.Count; offset++)
        {
            string pid  = active[(myIdx + offset) % active.Count];
            if (!sim.hands.ContainsKey(pid) || sim.hands[pid].Count == 0) continue;

            List<string> pHand = sim.hands[pid];
            var suitCards = pHand.Where(c => GetSuit(c) == suit).ToList();

            string played;
            if (suitCards.Count > 0)
            {
                // Play highest of suit (simplified opponent behaviour)
                played = HighestCard(suitCards);
                int rank = GetRankValue(played);
                if (rank > winnerRank) { winnerRank = rank; winnerId = pid; }
            }
            else
            {
                // Void — play lowest card (Thulla — stops the trick)
                played = LowestCard(pHand);
                // Thulla means highest-suit-card holder picks up — but for
                // the look-ahead we only care who would lead next, which is
                // the highest-suit-card player (the bot if no one beat them).
            }

            pHand.Remove(played);
        }

        return winnerId;
    }

    // ── SimState-based player knowledge helpers ──────────────────────────────

    private bool SimPlayerHasSuit(SimState sim, string pid, string suit)
    {
        if (sim.voids.Contains(pid + "|" + suit)) return false;
        if (sim.hands.ContainsKey(pid)) return sim.hands[pid].Any(c => GetSuit(c) == suit);
        return true;
    }

    private bool SimAllHaveSuit(SimState sim, string selfId, string suit)
        => sim.activePlayers.Where(p => p != selfId).All(p => SimPlayerHasSuit(sim, p, suit));

    private bool SimPlayerHasGreater(SimState sim, string pid, string refCard)
    {
        if (!sim.hands.ContainsKey(pid)) return false;
        string suit = GetSuit(refCard); int rank = GetRankValue(refCard);
        return sim.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) > rank);
    }

    private bool SimPlayerHasLower(SimState sim, string pid, string refCard)
    {
        if (!sim.hands.ContainsKey(pid)) return false;
        string suit = GetSuit(refCard); int rank = GetRankValue(refCard);
        return sim.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) < rank);
    }

    private HashSet<string> BuildDominantSuitsFromSim(SimState sim, string botId, List<string> hand)
    {
        var dom = new HashSet<string>();
        foreach (string s in AllSuits)
        {
            int remaining = 13 - _discardPile.Count(c => GetSuit(c) == s);
            int myCount   = hand.Count(c => GetSuit(c) == s);
            if (myCount >= remaining - 1) dom.Add(s);
        }
        return dom;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UPGRADE DISCARD CARD WITH OTHER BOTS
    //
    //  When a bot is forced to play out-of-suit (Thulla / discard), before
    //  actually playing the chosen card, check if any other active bot holds
    //  a HIGHER card of the SAME suit. If yes, swap — this bot plays the
    //  highest available card of that suit instead, dumping it from the game
    //  at the best possible value.
    //
    //  Why: when you MUST discard anyway, you want to remove the highest
    //  possible card of that suit — not waste a low card while strong cards
    //  sit in other bots' hands.
    //
    //  Rule: No restriction on A/K/Q here — any higher card can be swapped in.
    //        The other bot absorbs the weaker card in return.
    //
    //  Example:
    //    Bot1 must discard. Chosen card = 8H.
    //    Bot2 has 10H. Bot3 has QH.
    //    Best upgrade = QH (rank 12 > rank 10 > rank 8).
    //    → Swap: Bot1 gets QH, Bot3 gets 8H.
    //    → Bot1 plays QH. QH is removed from game on this Thulla.
    //
    //  Example 2 (no upgrade available):
    //    Bot1 chosen card = JH. No other bot has any Heart higher than J.
    //    → Play JH as-is.
    // ═══════════════════════════════════════════════════════════════════════

    private string UpgradeDiscardWithOtherBots(GameState gs, string botId, string chosenCard)
    {
        if (string.IsNullOrEmpty(chosenCard)) return chosenCard;

        // ── Skip upgrade if the trick was LED by a bot ───────────────────────
        // When a bot started this trick, swapping out-of-suit cards between
        // bots is unnecessary — the leading bot is already coordinated.
        // Only upgrade when a HUMAN led the trick (cardsInPlay[0] is human).
        if (gs.cardsInPlay != null && gs.cardsInPlay.Count > 0)
        {
            string leadPlayer = gs.cardsInPlay[0].playerId;
            if (leadPlayer != null && leadPlayer.StartsWith("BOT_"))
            {
                Debug.Log($"[Discard Upgrade] Skipped — trick was led by bot {leadPlayer}");
                return chosenCard;
            }
        }

        // ── Skip upgrade if the chosen card is already KNOWN ─────────────────
        // A known card has already been played publicly in a previous round.
        // No benefit in swapping it — opponents already know it exists.
        // Play it as-is and preserve unknown cards for future swaps.
        if (_knownCards.Contains(chosenCard))
        {
            Debug.Log($"[Discard Upgrade] Skipped — {chosenCard} is already a known card");
            return chosenCard;
        }

        string suit    = GetSuit(chosenCard);
        int    myRank  = GetRankValue(chosenCard);

        // Find the highest card of the same suit across all other active bots
        string bestUpgradeCard = null;
        string bestUpgradePid  = null;
        int    bestUpgradeRank = myRank; // must strictly beat current card

        foreach (string pid in gs.activePlayers)
        {
            if (pid == botId) continue;                        // skip self
            if (!pid.StartsWith("BOT_")) continue;            // only swap with other bots
            if (!gs.hands.ContainsKey(pid)) continue;

            foreach (string c in gs.hands[pid])
            {
                if (GetSuit(c) != suit) continue;
                int r = GetRankValue(c);
                if (r > bestUpgradeRank)
                {
                    bestUpgradeRank = r;
                    bestUpgradeCard = c;
                    bestUpgradePid  = pid;
                }
            }
        }

        if (bestUpgradeCard == null)
            return chosenCard; // no upgrade available — play original card

        // Perform the swap directly on the real hands
        gs.hands[botId].Remove(chosenCard);
        gs.hands[bestUpgradePid].Remove(bestUpgradeCard);
        gs.hands[botId].Add(bestUpgradeCard);
        gs.hands[bestUpgradePid].Add(chosenCard);

        Debug.Log($"[Discard Upgrade] Bot {botId} [{chosenCard}] ↔ Bot {bestUpgradePid} [{bestUpgradeCard}] — playing {bestUpgradeCard}");

        return bestUpgradeCard;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DISCARD ALGORITHM
    //  Choose the suit with the fewest cards; break ties by highest total
    //  value. Play the highest card of the chosen suit.
    // ═══════════════════════════════════════════════════════════════════════

    private string DiscardUsingLowestSuitAlgorithm(List<string> hand, string excludeSuit)
    {
        var byS = hand
            .Where(c => string.IsNullOrEmpty(excludeSuit) || GetSuit(c) != excludeSuit)
            .GroupBy(GetSuit)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (byS.Count == 0) return HighestCard(hand);

        int minCount = byS.Values.Min(l => l.Count);

        var candidates = byS.Where(kv => kv.Value.Count == minCount).ToList();
        if (candidates.Count == 1) return HighestCard(candidates[0].Value);

        int maxVal    = candidates.Max(kv => kv.Value.Sum(GetRankValue));
        var bestSuits = candidates.Where(kv => kv.Value.Sum(GetRankValue) == maxVal).ToList();
        return HighestCard(bestSuits[0].Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SHARED HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly string[] AllSuits = { "S", "H", "D", "C" };

    private static string GetSuit(string code)
        => string.IsNullOrEmpty(code) ? "" : code[code.Length - 1].ToString();

    private static int GetRankValue(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        string r = code.Substring(0, code.Length - 1);
        switch (r)
        {
            case "A": return 14; case "K": return 13;
            case "Q": return 12; case "J": return 11;
            default:  return int.TryParse(r, out int v) ? v : 0;
        }
    }

    private static string HighestCard(IEnumerable<string> cards)
        => cards.OrderByDescending(GetRankValue).First();

    private static string LowestCard(IEnumerable<string> cards)
        => cards.OrderBy(GetRankValue).First();

    private int DiscardedCount(string suit)
        => _discardPile.Count(c => GetSuit(c) == suit);

    private HashSet<string> EnsureVoids(string pid)
    {
        if (!_knownVoids.ContainsKey(pid)) _knownVoids[pid] = new HashSet<string>();
        return _knownVoids[pid];
    }

    private HashSet<string> BuildDominantSuits(List<string> hand)
    {
        var dom = new HashSet<string>();
        foreach (string s in AllSuits)
        {
            int remaining = 13 - DiscardedCount(s);
            int myCount   = hand.Count(c => GetSuit(c) == s);
            if (myCount >= remaining - 1) dom.Add(s);
        }
        return dom;
    }

    // ── GameState-based player knowledge ─────────────────────────────────────

    private bool PlayerHasSuit(GameState gs, string pid, string suit)
    {
        if (_knownVoids.ContainsKey(pid) && _knownVoids[pid].Contains(suit)) return false;
        if (gs.hands.ContainsKey(pid)) return gs.hands[pid].Any(c => GetSuit(c) == suit);
        return true;
    }

    private bool AllActivePlayers_HaveSuit(GameState gs, string selfId, string suit)
        => gs.activePlayers.Where(p => p != selfId).All(p => PlayerHasSuit(gs, p, suit));

    private bool PlayerHasGreaterCard(GameState gs, string pid, string refCard)
    {
        if (!gs.hands.ContainsKey(pid)) return false;
        string suit = GetSuit(refCard); int rank = GetRankValue(refCard);
        return gs.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) > rank);
    }

    private bool PlayerHasLowerCard(GameState gs, string pid, string refCard)
    {
        if (!gs.hands.ContainsKey(pid)) return false;
        string suit = GetSuit(refCard); int rank = GetRankValue(refCard);
        return gs.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) < rank);
    }

    /// <summary>
    /// Returns the highest card of <paramref name="suit"/> from a player's hand,
    /// or empty string if they have none of that suit.
    /// </summary>
    private string GetHighestCardOfSuitFromHand(GameState gs, string pid, string suit)
    {
        if (!gs.hands.ContainsKey(pid)) return "";
        var matching = gs.hands[pid].Where(c => GetSuit(c) == suit).ToList();
        return matching.Count > 0 ? HighestCard(matching) : "";
    }
}
