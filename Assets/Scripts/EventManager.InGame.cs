using System;
using System.Collections.Generic;

public static partial class EventManager
{
    // ─── Game Ready ───────────────────────────────────────────────────────────
    public static event Action<List<CardData>, List<SlotData>> OnGameReady;
    public static void FireGameReady(List<CardData> hand, List<SlotData> players)
        => OnGameReady?.Invoke(hand, players);

    // ─── Game State ──────────────────────────────────────────────────────────
    public static event Action<GameState> OnGameStateUpdated;
    public static void FireGameStateUpdated(GameState state)
        => OnGameStateUpdated?.Invoke(state);

    // ─── Turn ────────────────────────────────────────────────────────────────
    public static event Action<string> OnLocalCardPlayed;
    public static void FireLocalCardPlayed(string cardCode)
        => OnLocalCardPlayed?.Invoke(cardCode);

    public static event Action<string, string> OnCardPlayed;
    public static void FireCardPlayed(string playerId, string cardCode)
        => OnCardPlayed?.Invoke(playerId, cardCode);

    public static event Action<string> OnPlayerTurnStarted;
    public static void FirePlayerTurnStarted(string playerId)
        => OnPlayerTurnStarted?.Invoke(playerId);

    public static event Action<float> OnTurnTimerUpdated;
    public static void FireTurnTimerUpdated(float secondsRemaining)
        => OnTurnTimerUpdated?.Invoke(secondsRemaining);

    // ─── Hand ────────────────────────────────────────────────────────────────
    // 2-param version — matches GameManager: FireLocalHandUpdated(List<string>, GameState)
    // The list carries raw short-code strings; views convert to CardData themselves.
    public static event Action<List<string>, GameState> OnLocalHandUpdated;
    public static void FireLocalHandUpdated(List<string> hand, GameState gs)
        => OnLocalHandUpdated?.Invoke(hand, gs);

    public static event Action<string, int> OnPlayerHandCountUpdated;
    public static void FirePlayerHandCountUpdated(string playerId, int count)
        => OnPlayerHandCountUpdated?.Invoke(playerId, count);

    // ─── Steal ───────────────────────────────────────────────────────────────
    // Used by GameManager: EventManager.OnStealHandRequested += HandleStealHand
    public static event Action OnStealHandRequested;
    public static void FireStealHandRequested() => OnStealHandRequested?.Invoke();

    // ─── Leave Game (in-game only — distinct from LeaveRoom matchmaking event) ─
    // Used by GameManager, HostWatchdog, InGameManager
    public static event Action OnLeaveGameRequested;
    public static void FireLeaveGameRequested() => OnLeaveGameRequested?.Invoke();

    // ─── Shootout ────────────────────────────────────────────────────────────
    public static event Action<string> OnShootoutStarted;
    public static void FireShootoutStarted(string drawerId)
        => OnShootoutStarted?.Invoke(drawerId);

    public static event Action<CardData> OnShootoutCardDrawn;
    public static void FireShootoutCardDrawn(CardData card)
        => OnShootoutCardDrawn?.Invoke(card);

    // ─── Round ───────────────────────────────────────────────────────────────
    public static event Action OnRoundResolved;
    public static void FireRoundResolved() => OnRoundResolved?.Invoke();

    public static event Action<string> OnThulaResolved;
    public static void FireThulaResolved(string pickupPlayerId)
        => OnThulaResolved?.Invoke(pickupPlayerId);

    // ─── Game End ────────────────────────────────────────────────────────────
    public static event Action<List<string>, string> OnGameFinished;
    public static void FireGameFinished(List<string> winners, string bhabhi)
        => OnGameFinished?.Invoke(winners, bhabhi);

    // ─── Coins Award (host only) ─────────────────────────────────────────────
    public static event Action<List<string>, int> OnAwardGameCoinsRequested;
    public static void FireAwardGameCoinsRequested(List<string> winnerIds, int amount)
        => OnAwardGameCoinsRequested?.Invoke(winnerIds, amount);

    // ─── Card Deal Animation Sync ─────────────────────────────────────────────
    public static event Action OnCardDealAnimationComplete;
    public static void FireCardDealAnimationComplete()
        => OnCardDealAnimationComplete?.Invoke();
}
