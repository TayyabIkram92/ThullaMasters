using System;
using System.Collections.Generic;

/// <summary>
/// Represents a single playing card.
///
/// Suit order:  Spades=0, Hearts=1, Clubs=2, Diamonds=3
/// Rank order:  Ace=0, 2=1, 3=2, ... 10=9, Jack=10, Queen=11, King=12
///
/// Sprite index = (suitIndex * 13) + rankIndex
/// This matches the Inspector array order:
///   0-12  : AS 2S 3S ... KS
///   13-25 : AH 2H 3H ... KH
///   26-38 : AC 2C 3C ... KC
///   39-51 : AD 2D 3D ... KD
///
/// Short codes: AS, KH, 10C, JD, etc. — used for Firestore serialization.
/// </summary>
[Serializable]
public class CardData
{
    public int suitIndex;   // 0=Spades 1=Hearts 2=Clubs 3=Diamonds
    public int rankIndex;   // 0=Ace 1=2 ... 9=10 10=Jack 11=Queen 12=King

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>Index into the 52-sprite Inspector array.</summary>
    public int SpriteIndex => (suitIndex * 13) + rankIndex;

    private static readonly string[] SuitCodes = { "S", "H", "C", "D" };
    private static readonly string[] RankCodes = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    /// <summary>Short code used in Firestore e.g. "AS", "KH", "10C".</summary>
    public string ShortCode => RankCodes[rankIndex] + SuitCodes[suitIndex];

    // ── Constructors ──────────────────────────────────────────────────────────

    public CardData() { }

    public CardData(int suitIndex, int rankIndex)
    {
        this.suitIndex = suitIndex;
        this.rankIndex = rankIndex;
    }

    // ── Firestore Serialization ───────────────────────────────────────────────

    public string ToFirestoreString() => ShortCode;

    /// <summary>Parse from short code string e.g. "AS", "10H", "KD".</summary>
    public static CardData FromShortCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;

        // Last character is always the suit
        char suitChar = code[code.Length - 1];
        string rankStr = code.Substring(0, code.Length - 1);

        int suit = Array.IndexOf(SuitCodes, suitChar.ToString());
        int rank = Array.IndexOf(RankCodes, rankStr);

        if (suit < 0 || rank < 0)
        {
            UnityEngine.Debug.LogError($"[CardData] Invalid card code: {code}");
            return null;
        }

        return new CardData(suit, rank);
    }

    // ── Deck Builder ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a full shuffled deck of 52 cards.
    /// Uses Fisher-Yates shuffle.
    /// </summary>
    public static List<CardData> GenerateShuffledDeck()
    {
        var deck = new List<CardData>(52);

        for (int suit = 0; suit < 4; suit++)
            for (int rank = 0; rank < 13; rank++)
                deck.Add(new CardData(suit, rank));

        // Fisher-Yates shuffle
        var rng = new System.Random();
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        return deck;
    }
}
