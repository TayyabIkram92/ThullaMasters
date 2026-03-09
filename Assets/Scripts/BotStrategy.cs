// ============================================================
//  BotStrategy.cs  —  Hard-Bot AI for Thulla / Bhabhi
//  Implements the full strategic algorithm from BotStrategy_cs.txt
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Stateful bot-strategy engine.
/// One shared instance lives on GameManager (_botStrategy).
/// </summary>
public class BotStrategy
{
    // ── Discard tracking ────────────────────────────────────────────────────
    // Maps playerId → set of suits the player is known to be void in.
    private readonly Dictionary<string, HashSet<string>> _knownVoids
        = new Dictionary<string, HashSet<string>>();

    // Cards confirmed gone from the game (clean-round discards).
    private readonly HashSet<string> _discardPile = new HashSet<string>();

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Reset all learned state at the start of a new game.</summary>
    public void Reset()
    {
        _knownVoids.Clear();
        _discardPile.Clear();
    }

    /// <summary>
    /// Called by GameManager after every card is played so the bot can learn
    /// about voids (when a player plays out-of-suit = Thulla).
    /// </summary>
    public void RecordMove(GameState gs, string playerId, string cardCode)
    {
        if (string.IsNullOrEmpty(gs.leadSuit)) return;          // first card of round, no lead yet
        string suit = GetSuit(cardCode);
        if (suit != gs.leadSuit)
        {
            // Player had to break suit → they are void in leadSuit
            EnsureVoids(playerId).Add(gs.leadSuit);
        }
    }

    /// <summary>
    /// Called when a player picks up a Thulla pile so we can track the new
    /// cards in their hand (useful for future opponent-modelling).
    /// Currently records the event; extend as needed.
    /// </summary>
    public void RecordPickup(GameState gs, string playerId, List<string> cards)
    {
        // If the player just received cards back, they are no longer guaranteed
        // void in that suit (the pile may include that suit).
        // Simple conservative approach: clear their void flags for any suit
        // present in the cards they picked up.
        if (!_knownVoids.ContainsKey(playerId)) return;
        foreach (var c in cards)
            _knownVoids[playerId].Remove(GetSuit(c));
    }

    /// <summary>Main decision entry-point called by GameManager.AutoPlay.</summary>
    public string ChooseCard(GameState gs, string botId, List<string> hand)
    {
        if (hand == null || hand.Count == 0) return null;

        bool isFirstTurn = gs.roundNumber == 1;
        bool isLeading   = string.IsNullOrEmpty(gs.leadSuit);

        // ── FIRST TURN LOGIC ─────────────────────────────────────────────────
        if (isFirstTurn)
            return ChooseFirstTurnCard(gs, botId, hand);

        // ── NORMAL PLAY ──────────────────────────────────────────────────────
        if (isLeading)
            return ChooseLeadCard(gs, botId, hand);
        else
            return ChooseFollowCard(gs, botId, hand);
    }

    // ── FIRST TURN ───────────────────────────────────────────────────────────

    private string ChooseFirstTurnCard(GameState gs, string botId, List<string> hand)
    {
        // First card of the whole game (round 1, no cards in play yet)
        if (gs.cardsInPlay.Count == 0)
        {
            // Must play AS if we have it
            if (hand.Contains("AS")) return "AS";
        }

        // Round 1, following someone else's lead
        var spades = hand.Where(c => GetSuit(c) == "S").ToList();
        if (spades.Count > 0)
        {
            if (spades.Count == 1) return spades[0];
            return HighestCard(spades);
        }

        // No spades → discard using the lowest-suit algorithm
        return DiscardUsingLowestSuitAlgorithm(hand, "S");
    }

    // ── LEADING ──────────────────────────────────────────────────────────────

    private string ChooseLeadCard(GameState gs, string botId, List<string> hand)
    {
        var activePlayers = gs.activePlayers;
        int myIndex       = activePlayers.IndexOf(botId);
        if (myIndex < 0) return hand[0];

        string nextId   = activePlayers[(myIndex + 1) % activePlayers.Count];
        string secondId = activePlayers[(myIndex + 2) % activePlayers.Count];

        // ── RULE 0 : Find dominant suits ──────────────────────────────────────
        var dominantSuits = new HashSet<string>();
        foreach (string s in AllSuits)
        {
            int remaining = 13 - DiscardedCount(s);
            int myCount   = hand.Count(c => GetSuit(c) == s);
            if (myCount >= remaining - 1)
                dominantSuits.Add(s);
        }

        // Sort hand highest-to-lowest for searching
        var sortedHand = hand.OrderByDescending(GetRankValue).ToList();

        // ── RULE 1 : Clean round (all players have suit) ──────────────────────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (AllActivePlayers_HaveSuit(gs, botId, suit))
                return card;
        }

        // ── RULE 2 : Trap next player ─────────────────────────────────────────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (dominantSuits.Contains(suit)) continue;
            if (!PlayerHasSuit(gs, nextId, suit)) continue;
            if (PlayerHasGreaterCard(gs, nextId, card) && !PlayerHasLowerCard(gs, nextId, card))
                return card;
        }

        // ── RULE 3 : Trap second player ───────────────────────────────────────
        if (activePlayers.Count >= 3)
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
                        return card;
                }
            }
        }

        // ── RULE 4 : Smart fallback — feed card under opponent's high card ─────
        foreach (var card in sortedHand)
        {
            string suit = GetSuit(card);
            if (!PlayerHasSuit(gs, nextId, suit)) continue;
            if (PlayerHasGreaterCard(gs, nextId, card))
                return card;
        }

        // Ultimate fallback: lowest card
        return LowestCard(hand);
    }

    // ── FOLLOWING ─────────────────────────────────────────────────────────────

    private string ChooseFollowCard(GameState gs, string botId, List<string> hand)
    {
        string leadSuit = gs.leadSuit;
        var activePlayers = gs.activePlayers;
        int myIndex       = activePlayers.IndexOf(botId);
        if (myIndex < 0) return hand[0];

        string nextId   = activePlayers[(myIndex + 1) % activePlayers.Count];
        string secondId = activePlayers.Count >= 3
            ? activePlayers[(myIndex + 2) % activePlayers.Count]
            : null;

        var suitCards = hand.Where(c => GetSuit(c) == leadSuit).OrderByDescending(GetRankValue).ToList();

        if (suitCards.Count > 0)
        {
            // Follow same suit with adapted trap logic

            // Rule 1 mirror: all players have suit
            foreach (var card in suitCards)
            {
                if (AllActivePlayers_HaveSuit(gs, botId, leadSuit))
                    return card;
            }

            // Rule 2 mirror: trap next player
            foreach (var card in suitCards)
            {
                if (PlayerHasGreaterCard(gs, nextId, card) && !PlayerHasLowerCard(gs, nextId, card))
                    return card;
            }

            // Rule 3 mirror: trap second player
            if (secondId != null)
            {
                foreach (var card in suitCards)
                {
                    if (PlayerHasGreaterCard(gs, nextId, card) && PlayerHasLowerCard(gs, nextId, card))
                    {
                        if (PlayerHasGreaterCard(gs, secondId, card) && !PlayerHasLowerCard(gs, secondId, card))
                            return card;
                    }
                }
            }

            // Fallback: play lowest of lead suit (safest discard)
            return suitCards.Last();
        }

        // ── NO LEAD SUIT ─ Discard Algorithm ──────────────────────────────────
        return DiscardUsingLowestSuitAlgorithm(hand, leadSuit);
    }

    // ── DISCARD ALGORITHM ────────────────────────────────────────────────────
    // Pick the suit with the fewest cards to exhaust; break ties by highest
    // total value so we dump valuable cards first.

    private string DiscardUsingLowestSuitAlgorithm(List<string> hand, string excludeSuit)
    {
        var suitsInHand = hand
            .Where(c => GetSuit(c) != excludeSuit)
            .GroupBy(GetSuit)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (suitsInHand.Count == 0)
        {
            // No choice — play the highest card of anything
            return HighestCard(hand);
        }

        int minCount = suitsInHand.Values.Min(l => l.Count);

        do
        {
            var candidates = suitsInHand.Where(kv => kv.Value.Count == minCount).ToList();

            if (candidates.Count == 1)
                return HighestCard(candidates[0].Value);

            // Multiple suits at same count: pick highest total value suit
            int maxVal    = candidates.Max(kv => kv.Value.Sum(GetRankValue));
            var bestSuits = candidates.Where(kv => kv.Value.Sum(GetRankValue) == maxVal).ToList();

            // Pick any if still tied
            return HighestCard(bestSuits[0].Value);

            // (loop guard — realistically won't iterate)
        }
        while (false);

        return HighestCard(hand);
    }

    // ── HELPERS ──────────────────────────────────────────────────────────────

    private static readonly string[] AllSuits = { "S", "H", "D", "C" };

    private static string GetSuit(string code)
        => string.IsNullOrEmpty(code) ? "" : code[code.Length - 1].ToString();

    private static int GetRankValue(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        string r = code.Substring(0, code.Length - 1);
        switch (r)
        {
            case "A": return 14;
            case "K": return 13;
            case "Q": return 12;
            case "J": return 11;
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
        if (!_knownVoids.ContainsKey(pid))
            _knownVoids[pid] = new HashSet<string>();
        return _knownVoids[pid];
    }

    // ── Player knowledge helpers (probability-aware) ──────────────────────────

    /// <summary>Best estimate of whether a player has any card of the given suit.</summary>
    private bool PlayerHasSuit(GameState gs, string pid, string suit)
    {
        // If we know they're void, definitely not.
        if (_knownVoids.ContainsKey(pid) && _knownVoids[pid].Contains(suit))
            return false;

        // If we can inspect their actual hand (bot vs. bot, host-side), use it.
        if (gs.hands.ContainsKey(pid))
            return gs.hands[pid].Any(c => GetSuit(c) == suit);

        // Otherwise assume they might have it (safe/conservative)
        return true;
    }

    private bool AllActivePlayers_HaveSuit(GameState gs, string selfId, string suit)
    {
        foreach (var pid in gs.activePlayers)
        {
            if (pid == selfId) continue;
            if (!PlayerHasSuit(gs, pid, suit)) return false;
        }
        return true;
    }

    private bool PlayerHasGreaterCard(GameState gs, string pid, string referenceCard)
    {
        if (!gs.hands.ContainsKey(pid)) return false; // can't tell → assume no
        string suit = GetSuit(referenceCard);
        int rank    = GetRankValue(referenceCard);
        return gs.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) > rank);
    }

    private bool PlayerHasLowerCard(GameState gs, string pid, string referenceCard)
    {
        if (!gs.hands.ContainsKey(pid)) return false;
        string suit = GetSuit(referenceCard);
        int rank    = GetRankValue(referenceCard);
        return gs.hands[pid].Any(c => GetSuit(c) == suit && GetRankValue(c) < rank);
    }
}
