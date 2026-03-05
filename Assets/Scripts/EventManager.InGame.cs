using System;
using System.Collections.Generic;

/// <summary>
/// EventManager partial — all InGame gameplay events.
/// Matches exactly what GameManager.cs and InGameView.cs use.
/// </summary>
public static partial class EventManager
{
    // ── Game State ────────────────────────────────────────────────────────────

    /// <summary>Fired whenever GameState changes (from Firestore or host logic).</summary>
    public static event Action<GameState> OnGameStateUpdated;
    public static void FireGameStateUpdated(GameState gs)
        => OnGameStateUpdated?.Invoke(gs);

    /// <summary>Fired when it becomes the local player's turn.</summary>
    public static event Action<GameState> OnMyTurnStarted;
    public static void FireMyTurnStarted(GameState gs)
        => OnMyTurnStarted?.Invoke(gs);

    /// <summary>Fired by InGameView when local player taps a card.</summary>
    public static event Action<string> OnLocalCardPlayed;   // cardCode e.g. "AS"
    public static void FireLocalCardPlayed(string cardCode)
        => OnLocalCardPlayed?.Invoke(cardCode);

    /// <summary>Fired by InGameView when local player taps a flipped card in shootout.</summary>
    public static event Action OnShootoutCardChosen;
    public static void FireShootoutCardChosen()
        => OnShootoutCardChosen?.Invoke();

    /// <summary>Fired when any player plays a card. UI shows it on the table.</summary>
    public static event Action<string, string> OnCardPlayed;  // (playerId, cardCode)
    public static void FireCardPlayed(string pid, string card)
        => OnCardPlayed?.Invoke(pid, card);

    /// <summary>Fired when a round is discarded. nextLeadPlayerId leads next round.</summary>
    public static event Action<string> OnRoundDiscarded;
    public static void FireRoundDiscarded(string nextLeadPlayerId)
        => OnRoundDiscarded?.Invoke(nextLeadPlayerId);

    /// <summary>Fired when a player picks up cards after an out-of-suit play.</summary>
    public static event Action<string, List<string>> OnCardsPickedUp; // (playerId, cards)
    public static void FireCardsPickedUp(string pid, List<string> cards)
        => OnCardsPickedUp?.Invoke(pid, cards);

    /// <summary>Fired when a player runs out of cards and wins.</summary>
    public static event Action<string> OnPlayerWon;
    public static void FirePlayerWon(string pid)
        => OnPlayerWon?.Invoke(pid);

    /// <summary>
    /// Fired when the game is finished.
    /// winners = ordered list of winner playerIds, bhabhi = loser playerId.
    /// Matches GameManager: FireGameFinished(_gs.winners, _gs.bhabhi)
    /// </summary>
    public static event Action<List<string>, string> OnGameFinished; // (winners, bhabhiId)
    public static void FireGameFinished(List<string> winners, string bhabhi)
        => OnGameFinished?.Invoke(winners, bhabhi);

    /// <summary>Fired by InGameView steal button. GameManager handles the logic.</summary>
    public static event Action OnStealHandRequested;
    public static void FireStealHandRequested()
        => OnStealHandRequested?.Invoke();

    /// <summary>
    /// Fired when shootout starts. int = number of cards in responder's hand
    /// (so InGameView knows how many FlippedCard prefabs to spawn).
    /// </summary>
    public static event Action<int> OnShootoutStarted;
    public static void FireShootoutStarted(int responderCardCount)
        => OnShootoutStarted?.Invoke(responderCardCount);

    /// <summary>Fired when local player taps a flipped card. int = index tapped.</summary>
    public static event Action<int> OnFlippedCardPicked;
    public static void FireFlippedCardPicked(int index)
        => OnFlippedCardPicked?.Invoke(index);

    // ── Timer ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired every frame during a turn.
    /// fill = 0..1 (1=full time, 0=expired).
    /// pid  = whose turn it is (used to update the correct profile timer image).
    /// </summary>
    public static event Action<float, string> OnTimerTick;
    public static void FireTimerTick(float fill, string pid)
        => OnTimerTick?.Invoke(fill, pid);

    // ── Coins ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired by host at game end to award coins to winners.
    /// PlayFabManager listens and calls AddUserVirtualCurrency for each winner.
    /// </summary>
    public static event Action<List<string>, int> OnAwardGameCoinsRequested; // (winnerIds, amount)
    public static void FireAwardGameCoinsRequested(List<string> winnerIds, int amount)
        => OnAwardGameCoinsRequested?.Invoke(winnerIds, amount);

    // ── Hand Updates ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fired whenever the local player's hand changes (card played, pickup, steal, shootout).
    /// Carries both the new hand AND the latest GameState so InGameView can update
    /// _gs before calling RefreshCardInteractability — avoiding stale state bugs.
    /// </summary>
    public static event Action<List<string>, GameState> OnLocalHandUpdated;
    public static void FireLocalHandUpdated(List<string> hand, GameState gs)
        => OnLocalHandUpdated?.Invoke(hand, gs);
}
