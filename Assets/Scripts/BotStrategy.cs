using UnityEngine;
using System.Collections.Generic;
using System.Linq;

// ═══════════════════════════════════════════════════════════════════════════════
// BotStrategy v9 — THE PLAYER'S OWN MIND
//
// ── WHY ALL PREVIOUS VERSIONS LOST ──────────────────────────────────────────
//
//   v1-v8 were all reactive: "what is the least-bad move right now?"
//   The human player plays proactively: "what does my hand need to become,
//   and which moves get me there?"
//
//   The player's strategy, extracted from 4 game logs and 10 deep observations:
//
//   PHASE 1 — BURN HIGH CARDS WHILE SAFE
//     "All players have a suit card. I keep playing higher in starting rounds
//      to discard my higher/dangerous cards."
//     Lead aces, kings, queens IMMEDIATELY in clean rounds.
//     Don't wait. A high card in your hand at round 5+ is a trap waiting to fire.
//
//   PHASE 2 — VOID MANUFACTURING + FREE DUMPS
//     After burning high cards, deliberately exhaust your shortest suit.
//     Each void = a free thulla weapon forever.
//     Lead low cards in suits where a beater plays BEFORE any void opponent.
//     Void player thullas → BEATER (not you) picks up.
//     You dumped a card for free.
//
//   PHASE 3 — CONTROLLED THULLA ACCEPTANCE
//     "I also got thullas but they gave me lower cards, or cards beneficial."
//     Sometimes you WANT to pick up — if every card in the round is lower
//     than your current average hand rank. You're trading danger for safety.
//
//   PHASE 4 — ENDGAME TRAP
//     "I make sure to play cards that eliminates other player's lower cards.
//      When I get a safe passage I can go lower and the other player becomes
//      lead, but will get thulla from me or play the card which I already
//      have the lower card to keep them in lead."
//     Keep ONE card below opponent's lowest. Lead it → opponent must play
//     above → they become highest → if thulla fires → they pick up.
//
//   ORDER-AWARE VOID GUARD (v8 fix, retained):
//     NEVER lead a suit where the first void player comes before any beater
//     in play order. That is an infinite pickup loop regardless of card played.
//
// ── HAND DANGER SCORE (HDS) ──────────────────────────────────────────────────
//   Player mentally tracks this at all times:
//   HDS = sum of danger per card - void bonuses
//   A=8, K=6, Q=4, J=3, 10=2, 9=1, 8-2=0 per card
//   Each void suit: -5
//   Low HDS = safe hand. High HDS = need to dump high cards NOW.
//
// ── BUG HISTORY ─────────────────────────────────────────────────────────────
//   BUG-A (v6): TrapPreservingLead returned rank-2 card as trap → removed
//   BUG-B (v6): WorstCaseCardsGained threshold too high → fixed
//   BUG-E (v6): Simulation missed infinite loops → fixed
//   BUG-F (v7): Strategy A fired when sole holder → fixed
//   BUG-G (v8): ClassifySuitDanger ignored play order → fixed
//   BUG-H (v8): FindFreeDumpCard ignored play order → fixed
//   BUG-I (v8): CandidateDangerPenalty ignored play order → fixed
//   NEW v9: Replaced simulation-based scoring with phase-aware planning
// ═══════════════════════════════════════════════════════════════════════════════

public class BotStrategy
{
    private GameState _gs;
    private bool _isBot(string id) => id != null && id.StartsWith("BOT_");

    // Void tracker: confirmed voids per player per suit
    private readonly Dictionary<string, HashSet<string>> _voidTracker
        = new Dictionary<string, HashSet<string>>();

    private enum GamePhase
    {
        BurnHigh,
        FreeDump,
        Endgame
    }

    private enum SuitDanger
    {
        Safe,
        Risky,
        Deadly
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Reset()
    {
        _voidTracker.Clear();
        Debug.Log("[BotStrategy] Reset — new game");
    }

    public void RecordMove(GameState gs, string playerId, string cardCode)
    {
        _gs = gs;
        if (!string.IsNullOrEmpty(_gs.leadSuit) && GetSuit(cardCode) != _gs.leadSuit)
        {
            if (!_voidTracker.ContainsKey(playerId))
                _voidTracker[playerId] = new HashSet<string>();
            _voidTracker[playerId].Add(_gs.leadSuit);
            Debug.Log($"[BotStrategy] {playerId} confirmed VOID in {_gs.leadSuit}");
        }
    }

    public string ChooseCard(GameState gs, string botId, List<string> hand)
    {
        _gs = gs;

        // Round 1 special: whoever has AS leads it
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count == 0 && hand.Contains("AS"))
            return "AS";
        if (_gs.roundNumber == 1 && _gs.cardsInPlay.Count > 0)
        {
            var sp = hand.FindAll(c => GetSuit(c) == "S");
            return sp.Count > 0 ? HighestOf(sp) : HighestOf(hand);
        }

        bool isLeading = string.IsNullOrEmpty(_gs.leadSuit);
        string card = isLeading ? ChooseLead(botId, hand) : ChooseFollow(botId, hand);
        Debug.Log($"[BotStrategy] {botId} plays {card} (leading={isLeading}, hand={hand.Count})");
        return card;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE DETECTION
    // ═══════════════════════════════════════════════════════════════════════════

    private GamePhase DetectPhase(string botId, List<string> hand)
    {
        if (hand.Count <= 4) return GamePhase.Endgame;
        if (CountHighCards(hand) == 0) return GamePhase.FreeDump;

        // Check if voids are forming across the table
        int totalSuitsHeld = 0;
        foreach (var pid in _gs.activePlayers)
            if (_gs.hands.ContainsKey(pid))
                totalSuitsHeld += GroupBySuit(_gs.hands[pid]).Count;

        float avgSuits = _gs.activePlayers.Count > 0
            ? (float)totalSuitsHeld / _gs.activePlayers.Count
            : 4;

        // Transition to FreeDump phase when voids start appearing
        if (avgSuits < 3.2f) return CountHighCards(hand) <= 1 ? GamePhase.FreeDump : GamePhase.BurnHigh;
        return GamePhase.BurnHigh;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LEAD LOGIC — THE CORE OF THE PLAYER'S STRATEGY
    // ═══════════════════════════════════════════════════════════════════════════

    private string ChooseLead(string botId, List<string> hand)
    {
        var phase = DetectPhase(botId, hand);
        var bySuit = GroupBySuit(hand);

        Debug.Log($"[BotStrategy] {botId} phase={phase} hds={HandDangerScore(hand)} " +
                  $"highCards={CountHighCards(hand)} suits={bySuit.Count}");

        // ── PHASE 1: Burn high cards in safe/clean rounds ─────────────────────
        if (phase == GamePhase.BurnHigh)
        {
            string burnCard = FindBestHighCardBurn(botId, hand, bySuit);
            if (!string.IsNullOrEmpty(burnCard))
            {
                Debug.Log($"[BotStrategy] {botId} PHASE1 BURN {burnCard}");
                return burnCard;
            }
        }

        // ── PHASE 2 / ENDGAME: Find free dump opportunity ─────────────────────
        string freeDump = FindBestFreeDump(botId, hand, bySuit);
        if (!string.IsNullOrEmpty(freeDump))
        {
            Debug.Log($"[BotStrategy] {botId} FREE DUMP {freeDump}");
            return freeDump;
        }

        // ── ENDGAME TRAP: Keep opponent in lead ───────────────────────────────
        if (phase == GamePhase.Endgame)
        {
            string trap = FindEndgameTrap(botId, hand);
            if (!string.IsNullOrEmpty(trap))
            {
                Debug.Log($"[BotStrategy] {botId} ENDGAME TRAP {trap}");
                return trap;
            }
        }

        // ── SAFE LEAD: burn highest from safest suit ──────────────────────────
        string safeLead = FindSafeLead(botId, hand, bySuit);
        if (!string.IsNullOrEmpty(safeLead))
        {
            Debug.Log($"[BotStrategy] {botId} SAFE LEAD {safeLead}");
            return safeLead;
        }

        // ── FORCED: all leads are dangerous, pick least-bad ──────────────────
        Debug.Log($"[BotStrategy] {botId} FORCED LEAD");
        return ForcedLead(botId, hand, bySuit);
    }

    // ── PHASE 1 IMPLEMENTATION: Find highest-danger card in a safe suit ───────

    private string FindBestHighCardBurn(string botId, List<string> hand,
        Dictionary<string, List<string>> bySuit)
    {
        // Find safe suits (no void opponents at all)
        var candidates = new List<(string card, int rank, bool isSafe)>();

        foreach (var kv in bySuit)
        {
            string suit = kv.Key;
            var danger = ClassifySuit(botId, suit, hand);

            if (danger == SuitDanger.Safe)
            {
                string h = HighestOf(kv.Value);
                if (GetRankValue(h) >= 11) // only burn J,Q,K,A in phase 1
                    candidates.Add((h, GetRankValue(h), true));
            }
            else if (danger == SuitDanger.Risky)
            {
                // Can also burn a high card if we're NOT the highest in play order
                string dump = FindOrderSafeCard(botId, suit, kv.Value);
                if (!string.IsNullOrEmpty(dump) && GetRankValue(dump) >= 10)
                    candidates.Add((dump, GetRankValue(dump), false));
            }
        }

        if (candidates.Count == 0) return "";
        // Prefer safe over risky, then highest rank first
        candidates.Sort((a, b) =>
        {
            if (a.isSafe != b.isSafe) return a.isSafe ? -1 : 1;
            return b.rank.CompareTo(a.rank);
        });
        return candidates[0].card;
    }

    // ── PHASE 2 IMPLEMENTATION: Free dump — beater before void in play order ──

    private string FindBestFreeDump(string botId, List<string> hand,
        Dictionary<string, List<string>> bySuit)
    {
        // For each RISKY suit: find lowest card where beater comes before void
        var candidates = new List<(string card, int rank)>();

        foreach (var kv in bySuit)
        {
            string suit = kv.Key;
            var danger = ClassifySuit(botId, suit, hand);
            if (danger != SuitDanger.Risky) continue;

            string dump = FindOrderSafeCard(botId, suit, kv.Value);
            if (!string.IsNullOrEmpty(dump))
                candidates.Add((dump, GetRankValue(dump)));
        }

        if (candidates.Count == 0) return "";
        candidates.Sort((a, b) => a.rank.CompareTo(b.rank)); // lowest first
        return candidates[0].card;
    }

    // ── ENDGAME TRAP: Lead a card that forces opponent to stay highest ─────────

    private string FindEndgameTrap(string botId, List<string> hand)
    {
        // Find a suit where we have the lowest card AND opponent has higher cards.
        // Leading our lowest forces opponent to play above us → they're highest.
        // If a thulla follows → opponent picks up.
        var bySuit = GroupBySuit(hand);
        var candidates = new List<(string card, float trapValue)>();

        foreach (var kv in bySuit)
        {
            string suit = kv.Key;
            var danger = ClassifySuit(botId, suit, hand);
            if (danger == SuitDanger.Deadly) continue;

            string myLowest = LowestOf(kv.Value);
            int myLowRank = GetRankValue(myLowest);

            // Find human player
            foreach (var pid in _gs.activePlayers)
            {
                if (_isBot(pid)) continue;
                if (!_gs.hands.ContainsKey(pid)) continue;

                var theirSuit = _gs.hands[pid].FindAll(c => GetSuit(c) == suit);
                if (theirSuit.Count == 0) continue;

                int theirLowest = GetRankValue(LowestOf(theirSuit));
                if (myLowRank < theirLowest)
                {
                    // We have lowest → lead it → they must play higher → they're trapped
                    float trapValue = theirLowest - myLowRank; // bigger gap = better trap
                    candidates.Add((myLowest, trapValue));
                }
            }
        }

        if (candidates.Count == 0) return "";
        candidates.Sort((a, b) => b.trapValue.CompareTo(a.trapValue));
        return candidates[0].card;
    }

    // ── SAFE LEAD: No voids, burn highest ─────────────────────────────────────

    private string FindSafeLead(string botId, List<string> hand,
        Dictionary<string, List<string>> bySuit)
    {
        var candidates = new List<(string card, int rank)>();
        foreach (var kv in bySuit)
        {
            string suit = kv.Key;
            var danger = ClassifySuit(botId, suit, hand);
            if (danger == SuitDanger.Safe)
                candidates.Add((HighestOf(kv.Value), GetRankValue(HighestOf(kv.Value))));
        }

        if (candidates.Count == 0) return "";
        candidates.Sort((a, b) => b.rank.CompareTo(a.rank));
        return candidates[0].card;
    }

    // ── FORCED LEAD: All suits dangerous, pick least-bad ──────────────────────

    private string ForcedLead(string botId, List<string> hand,
        Dictionary<string, List<string>> bySuit)
    {
        // Among all suits, pick the one with the most cards (dilutes damage)
        // and play the highest to at least burn something
        var best = bySuit.OrderByDescending(kv => kv.Value.Count).First();
        // But play LOWEST to reduce chance of being highest in play
        return LowestOf(best.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FOLLOW LOGIC
    // ═══════════════════════════════════════════════════════════════════════════

    private string ChooseFollow(string botId, List<string> hand)
    {
        string leadSuit = _gs.leadSuit;
        var suitCards = hand.FindAll(c => GetSuit(c) == leadSuit);
        var phase = DetectPhase(botId, hand);

        // Void → thulla with highest from smallest suit
        if (suitCards.Count == 0)
        {
            Debug.Log($"[BotStrategy] {botId} VOID THULLA in {leadSuit}");
            return ThullaCard(hand);
        }

        // Current state of the round
        var played = _gs.cardsInPlay;
        var playedPids = new HashSet<string>(played.Select(pc => pc.playerId));
        string curHigh = GetCurrentHighCard(leadSuit);
        int curRank = GetRankValue(curHigh);
        string curWinner = GetCurrentWinner(leadSuit);
        bool winnerIsHuman = !string.IsNullOrEmpty(curWinner) && !_isBot(curWinner);
        bool winnerIsBotAlly = !string.IsNullOrEmpty(curWinner) && _isBot(curWinner) && curWinner != botId;

        bool thullacoming = _gs.activePlayers.Any(pid =>
            pid != botId && !playedPids.Contains(pid) && IsConfirmedVoid(pid, leadSuit));

        int ourHighRank = GetRankValue(HighestOf(suitCards));

        // ── CLEAN ROUND ───────────────────────────────────────────────────────
        if (!thullacoming)
        {
            // PHASE 1: burn highest (dump high cards while clean)
            if (phase == GamePhase.BurnHigh)
                return HighestOf(suitCards);

            // Strategy D: avoid winning lead when hand is large/dangerous
            if (ourHighRank > curRank && (hand.Count >= 7 || CountHighCards(hand) >= 2))
            {
                string safe = BestBelow(suitCards, curRank);
                if (safe != null)
                {
                    Debug.Log($"[BotStrategy] {botId} AVOID LEAD {safe}");
                    return safe;
                }
            }

            return HighestOf(suitCards);
        }

        // ── THULLA IS COMING ─────────────────────────────────────────────────

        // RANK 3 — Controlled thulla acceptance:
        // Would becoming highest give us a beneficial pickup?
        if (ourHighRank > curRank && WouldPickupBenefit(botId, hand, leadSuit))
        {
            Debug.Log($"[BotStrategy] {botId} CONTROLLED PICKUP ACCEPT");
            return HighestOf(suitCards);
        }

        // Bot cooperation: target human as pickup
        string targetCard = TryTargetHuman(botId, suitCards, leadSuit);
        if (targetCard != null) return targetCard;

        // Protect bot ally who is winning
        if (winnerIsBotAlly)
        {
            string safe = BestBelow(suitCards, curRank);
            return safe ?? HighestOf(suitCards);
        }

        // Steal lead from human
        if (winnerIsHuman && ourHighRank > curRank)
            return HighestOf(suitCards);

        // Self-preservation: don't be highest
        if (ourHighRank > curRank)
        {
            string safe = BestBelow(suitCards, curRank);
            if (safe != null) return safe;
        }

        return HighestOf(suitCards);
    }

    // ── CONTROLLED THULLA ACCEPTANCE ─────────────────────────────────────────
    // Accept picking up if every card we'd receive is low (avg < our avg - 2)

    private bool WouldPickupBenefit(string botId, List<string> hand, string leadSuit)
    {
        // Calculate cards we'd pick up: current cards in play
        var roundCards = _gs.cardsInPlay.Select(pc => pc.card).ToList();
        if (roundCards.Count == 0) return false;

        // Don't accept if any pickup card is A or K
        if (roundCards.Any(c => GetRankValue(c) >= 13)) return false;

        float avgPickup = roundCards.Average(c => (float)GetRankValue(c));
        float avgHand = hand.Count > 0 ? (float)hand.Average(c => GetRankValue(c)) : 14f;

        // Only beneficial if pickup cards are significantly below our hand average
        return avgPickup < avgHand - 3f;
    }

    // ── TARGET HUMAN AS PICKUP ────────────────────────────────────────────────

    private string TryTargetHuman(string botId, List<string> suitCards, string suit)
    {
        var playedPids = new HashSet<string>(_gs.cardsInPlay.Select(pc => pc.playerId));

        foreach (var pid in _gs.activePlayers)
        {
            if (_isBot(pid)) continue;

            // Find human's lowest card of suit (predicted play or held)
            var humanPlayed = _gs.cardsInPlay
                .FirstOrDefault(pc => pc.playerId == pid && GetSuit(pc.card) == suit);

            int targetRank;
            if (humanPlayed != null)
                targetRank = GetRankValue(humanPlayed.card);
            else if (_gs.hands.ContainsKey(pid))
            {
                var humanSuit = _gs.hands[pid].FindAll(c => GetSuit(c) == suit);
                if (humanSuit.Count == 0) continue; // human void → will thulla
                targetRank = GetRankValue(LowestOf(humanSuit));
            }
            else continue;

            string below = BestBelow(suitCards, targetRank);
            if (below != null) return below;
        }

        return null;
    }

    // ── THULLA CARD: play highest from smallest suit ──────────────────────────

    private string ThullaCard(List<string> hand)
    {
        var bySuit = GroupBySuit(hand);
        var smallest = bySuit.OrderBy(kv => kv.Value.Count).First();
        return HighestOf(smallest.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VOID-AWARE SUIT CLASSIFICATION (v8 order-aware logic, retained)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Classifies suit danger using PLAY ORDER (the v8 core fix).
    /// SAFE: no void opponents.
    /// RISKY: a beater appears BEFORE the first void in play order → safe to dump lowest.
    /// DEADLY: first void appears before any beater → guaranteed pickup loop.
    /// </summary>
    private SuitDanger ClassifySuit(string botId, string suit, List<string> hand)
    {
        var myCards = hand.FindAll(c => GetSuit(c) == suit);
        if (myCards.Count == 0) return SuitDanger.Safe;
        int myMax = GetRankValue(HighestOf(myCards));

        bool anyVoid = _gs.activePlayers.Any(pid =>
            pid != botId && IsVoid(pid, suit));
        if (!anyVoid) return SuitDanger.Safe;

        // Walk play order: first beater before first void = Risky. First void = Deadly.
        var order = GetPlayOrder(botId);
        foreach (var pid in order)
        {
            if (!_gs.hands.ContainsKey(pid)) continue;
            var their = _gs.hands[pid].FindAll(c => GetSuit(c) == suit);
            if (their.Count == 0) return SuitDanger.Deadly;
            if (GetRankValue(HighestOf(their)) > myMax) return SuitDanger.Risky;
        }

        return SuitDanger.Deadly;
    }

    /// <summary>
    /// Finds the lowest card in suitCards where a beater appears
    /// BEFORE any void player in play order.
    /// Returns "" if no such safe card exists.
    /// </summary>
    private string FindOrderSafeCard(string botId, string suit, List<string> suitCards)
    {
        var sorted = suitCards.OrderBy(c => GetRankValue(c)).ToList();
        var order = GetPlayOrder(botId);

        foreach (var candidate in sorted)
        {
            int cr = GetRankValue(candidate);
            bool safe = false;
            foreach (var pid in order)
            {
                if (!_gs.hands.ContainsKey(pid)) continue;
                var their = _gs.hands[pid].FindAll(c => GetSuit(c) == suit);
                if (their.Count == 0) break; // void first → unsafe
                if (GetRankValue(HighestOf(their)) > cr)
                {
                    safe = true;
                    break;
                }
            }

            if (safe) return candidate;
        }

        return "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HAND SCORING
    // ═══════════════════════════════════════════════════════════════════════════

    private int HandDangerScore(List<string> hand)
    {
        int score = 0;
        foreach (var c in hand)
        {
            int r = GetRankValue(c);
            if (r == 14) score += 8;
            else if (r == 13) score += 6;
            else if (r == 12) score += 4;
            else if (r == 11) score += 3;
            else if (r >= 9) score += 1;
        }

        int voids = 4 - GroupBySuit(hand).Count;
        score -= voids * 5;
        return score;
    }

    private int CountHighCards(List<string> hand)
        => hand.Count(c => GetRankValue(c) >= 11);

    // ═══════════════════════════════════════════════════════════════════════════
    // GAME STATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private bool IsConfirmedVoid(string pid, string suit)
        => _voidTracker.ContainsKey(pid) && _voidTracker[pid].Contains(suit);

    private bool IsVoid(string pid, string suit)
    {
        if (IsConfirmedVoid(pid, suit)) return true;
        return _gs.hands.ContainsKey(pid) &&
               !_gs.hands[pid].Exists(c => GetSuit(c) == suit);
    }

    private List<string> GetPlayOrder(string botId)
    {
        int idx = _gs.activePlayers.IndexOf(botId);
        if (idx < 0) return new List<string>(_gs.activePlayers);
        var order = new List<string>();
        for (int i = 1; i < _gs.activePlayers.Count; i++)
            order.Add(_gs.activePlayers[(idx + i) % _gs.activePlayers.Count]);
        return order;
    }

    private string GetCurrentHighCard(string suit)
    {
        string best = "";
        int bestRank = -1;
        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != suit) continue;
            int r = GetRankValue(pc.card);
            if (r > bestRank)
            {
                bestRank = r;
                best = pc.card;
            }
        }

        return best;
    }

    private string GetCurrentWinner(string suit)
    {
        string w = "";
        int best = -1;
        foreach (var pc in _gs.cardsInPlay)
        {
            if (GetSuit(pc.card) != suit) continue;
            int r = GetRankValue(pc.card);
            if (r > best)
            {
                best = r;
                w = pc.playerId;
            }
        }

        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CARD HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private string BestBelow(List<string> cards, int maxRank)
    {
        string best = null;
        int bestRank = -1;
        foreach (var c in cards)
        {
            int r = GetRankValue(c);
            if (r < maxRank && r > bestRank)
            {
                bestRank = r;
                best = c;
            }
        }

        return best;
    }

    private string HighestOf(List<string> cards)
        => cards.Aggregate((a, b) => GetRankValue(a) >= GetRankValue(b) ? a : b);

    private string LowestOf(List<string> cards)
        => cards.Aggregate((a, b) => GetRankValue(a) <= GetRankValue(b) ? a : b);

    private Dictionary<string, List<string>> GroupBySuit(List<string> hand)
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var c in hand)
        {
            string s = GetSuit(c);
            if (!d.ContainsKey(s)) d[s] = new List<string>();
            d[s].Add(c);
        }

        return d;
    }

    private string GetSuit(string code)
        => string.IsNullOrEmpty(code) ? "" : code[code.Length - 1].ToString();

    private int GetRankValue(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        string r = code.Substring(0, code.Length - 1);
        switch (r)
        {
            case "A": return 14;
            case "K": return 13;
            case "Q": return 12;
            case "J": return 11;
            default: return int.TryParse(r, out int v) ? v : 0;
        }
    }
}