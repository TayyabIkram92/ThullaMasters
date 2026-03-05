using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Full game state stored inside the Firestore room document under "gameState".
///
/// Firestore structure (inside rooms/{roomId}):
/// gameState: {
///   phase:               "playing" | "shootout" | "finished"
///   currentPlayerIndex:  int   (index into room.players)
///   leadSuit:            "S"|"H"|"C"|"D"|""
///   cardsInPlay:         [ { playerId, card } ]
///   roundNumber:         int
///   turnStartTime:       long  (UTC milliseconds — written once per turn)
///   winners:             [ playerId, ... ]
///   bhabhi:              playerId | ""
///   activePlayers:       [ playerId, ... ]  (players still in game)
///   hands:               { playerId: [cardCode,...] }  (updated after each move)
///   shootoutDrawerId:    playerId | ""   (who is drawing in shootout)
/// }
/// </summary>
[Serializable]
public class PlayedCard
{
    public string playerId;
    public string card;      // short code e.g. "AS"

    public PlayedCard() { }
    public PlayedCard(string playerId, string card)
    {
        this.playerId = playerId;
        this.card     = card;
    }

    public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
    {
        { "playerId", playerId },
        { "card",     card     }
    };

    public static PlayedCard FromDictionary(Dictionary<string, object> d) => new PlayedCard(
        d.ContainsKey("playerId") ? d["playerId"].ToString() : "",
        d.ContainsKey("card")     ? d["card"].ToString()     : ""
    );
}

[Serializable]
public class GameState
{
    // ── Core state ────────────────────────────────────────────────────────────
    public string            phase;               // "playing"|"shootout"|"finished"
    public int               currentPlayerIndex;
    public string            leadSuit;            // "S"|"H"|"C"|"D"|""
    public List<PlayedCard>  cardsInPlay;
    public int               roundNumber;
    public long              turnStartTime;        // UTC ms

    // ── Player tracking ───────────────────────────────────────────────────────
    public List<string>      winners;
    public string            bhabhi;
    public List<string>      activePlayers;        // ordered, still-in-game player ids

    // ── Hands (authoritative copy in Firestore) ───────────────────────────────
    public Dictionary<string, List<string>> hands;

    // ── Shootout ──────────────────────────────────────────────────────────────
    public string            shootoutDrawerId;     // who must draw a card

    // ── Constants ─────────────────────────────────────────────────────────────
    public const string PhaseNone     = "";
    public const string PhasePlaying  = "playing";
    public const string PhaseShootout = "shootout";
    public const string PhaseFinished = "finished";

    public const int TurnSeconds     = 20;
    public const int ShootoutSeconds = 10;

    public GameState()
    {
        phase              = PhasePlaying;
        currentPlayerIndex = 0;
        leadSuit           = "";
        cardsInPlay        = new List<PlayedCard>();
        roundNumber        = 1;
        turnStartTime      = 0;
        winners            = new List<string>();
        bhabhi             = "";
        activePlayers      = new List<string>();
        hands              = new Dictionary<string, List<string>>();
        shootoutDrawerId   = "";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public string CurrentPlayerId =>
        (activePlayers != null && currentPlayerIndex < activePlayers.Count)
            ? activePlayers[currentPlayerIndex]
            : "";

    public bool IsPlayerActive(string id) =>
        activePlayers != null && activePlayers.Contains(id);

    /// <summary>How many seconds remain in this turn (based on turnStartTime).</summary>
    public float SecondsRemaining(int totalSeconds)
    {
        if (turnStartTime <= 0) return totalSeconds;
        long nowMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float elapsed   = (nowMs - turnStartTime) / 1000f;
        return Mathf.Max(0f, totalSeconds - elapsed);
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    public Dictionary<string, object> ToDictionary()
    {
        var playedList = new List<object>();
        foreach (var pc in cardsInPlay)
            playedList.Add(pc.ToDictionary());

        var handsDict = new Dictionary<string, object>();
        foreach (var kv in hands)
        {
            var list = new List<object>();
            foreach (var c in kv.Value) list.Add(c);
            handsDict[kv.Key] = list;
        }

        return new Dictionary<string, object>
        {
            { "phase",              phase              },
            { "currentPlayerIndex", currentPlayerIndex },
            { "leadSuit",           leadSuit           },
            { "cardsInPlay",        playedList         },
            { "roundNumber",        roundNumber        },
            { "turnStartTime",      turnStartTime      },
            { "winners",            new List<object>(winners)       },
            { "bhabhi",             bhabhi             },
            { "activePlayers",      new List<object>(activePlayers) },
            { "hands",              handsDict          },
            { "shootoutDrawerId",   shootoutDrawerId   }
        };
    }

    public static GameState FromDictionary(Dictionary<string, object> d)
    {
        var gs = new GameState
        {
            phase              = d.ContainsKey("phase")              ? d["phase"].ToString()                    : PhasePlaying,
            currentPlayerIndex = d.ContainsKey("currentPlayerIndex") ? Convert.ToInt32(d["currentPlayerIndex"]) : 0,
            leadSuit           = d.ContainsKey("leadSuit")           ? d["leadSuit"].ToString()                 : "",
            roundNumber        = d.ContainsKey("roundNumber")        ? Convert.ToInt32(d["roundNumber"])        : 1,
            turnStartTime      = d.ContainsKey("turnStartTime")      ? Convert.ToInt64(d["turnStartTime"])      : 0,
            bhabhi             = d.ContainsKey("bhabhi")             ? d["bhabhi"].ToString()                   : "",
            shootoutDrawerId   = d.ContainsKey("shootoutDrawerId")   ? d["shootoutDrawerId"].ToString()         : "",
        };

        // cardsInPlay
        gs.cardsInPlay = new List<PlayedCard>();
        if (d.ContainsKey("cardsInPlay") && d["cardsInPlay"] is List<object> rawPlayed)
            foreach (var item in rawPlayed)
                if (item is Dictionary<string, object> pd)
                    gs.cardsInPlay.Add(PlayedCard.FromDictionary(pd));

        // winners
        gs.winners = new List<string>();
        if (d.ContainsKey("winners") && d["winners"] is List<object> rawWinners)
            foreach (var w in rawWinners) gs.winners.Add(w.ToString());

        // activePlayers
        gs.activePlayers = new List<string>();
        if (d.ContainsKey("activePlayers") && d["activePlayers"] is List<object> rawActive)
            foreach (var a in rawActive) gs.activePlayers.Add(a.ToString());

        // hands
        gs.hands = new Dictionary<string, List<string>>();
        if (d.ContainsKey("hands") && d["hands"] is Dictionary<string, object> rawHands)
            foreach (var kv in rawHands)
            {
                var cards = new List<string>();
                if (kv.Value is List<object> cl)
                    foreach (var c in cl) cards.Add(c.ToString());
                gs.hands[kv.Key] = cards;
            }

        return gs;
    }
}
