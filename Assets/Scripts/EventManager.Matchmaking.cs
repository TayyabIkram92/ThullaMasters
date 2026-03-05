using System;
using System.Collections.Generic;

/// <summary>
/// EventManager partial — Matchmaking, Firebase, and InGame events.
/// Add this file alongside the existing EventManager.cs.
/// </summary>
public static partial class EventManager
{
    // ── Firebase ──────────────────────────────────────────────────────────────

    public static event Action OnFirebaseReady;
    public static void FireFirebaseReady() => OnFirebaseReady?.Invoke();

    // ── Matchmaking ───────────────────────────────────────────────────────────

    public static event Action<GameModeData> OnMatchmakingStartRequested;
    public static void FireMatchmakingStartRequested(GameModeData mode)
        => OnMatchmakingStartRequested?.Invoke(mode);

    public static event Action<RoomData> OnRoomUpdated;
    public static void FireRoomUpdated(RoomData room) => OnRoomUpdated?.Invoke(room);

    public static event Action<RoomData> OnMatchFound;
    public static void FireMatchFound(RoomData room) => OnMatchFound?.Invoke(room);

    public static event Action OnMatchmakingCancelled;
    public static void FireMatchmakingCancelled() => OnMatchmakingCancelled?.Invoke();

    public static event Action<string> OnMatchmakingError;
    public static void FireMatchmakingError(string msg) => OnMatchmakingError?.Invoke(msg);

    // ── InGame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired by InGameManager once cards are dealt and the view is ready to show.
    /// Carries the local player's hand and the ordered seat list.
    /// </summary>
    public static event Action<List<CardData>, List<SlotData>> OnGameReady;
    public static void FireGameReady(List<CardData> localHand, List<SlotData> seatedPlayers)
        => OnGameReady?.Invoke(localHand, seatedPlayers);

    /// <summary>Fired when player presses Leave/Exit in InGame.</summary>
    public static event Action OnLeaveGameRequested;
    public static void FireLeaveGameRequested() => OnLeaveGameRequested?.Invoke();
}
