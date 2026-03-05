using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Caches game mode configurations fetched from PlayFab.
/// Fetch once, read everywhere.
/// Also stores the mode the player selected so MatchmakingView
/// can read it when it opens — avoids the event timing race condition.
/// </summary>
public static class GameModeManager
{
    public static List<GameModeData> AvailableModes { get; private set; } = new List<GameModeData>();
    public static bool               IsInitialized  { get; private set; } = false;

    /// <summary>
    /// The mode the player tapped in GameSelectionView.
    /// Set before navigating to Matchmaking so MatchmakingView
    /// can read it safely in OnEnable/ResetView.
    /// </summary>
    public static GameModeData SelectedMode { get; private set; } = null;

    public static void Initialize(List<int> entryFees)
    {
        AvailableModes.Clear();

        foreach (int fee in entryFees)
            AvailableModes.Add(new GameModeData(fee));

        IsInitialized = true;
        Debug.Log($"[GameModeManager] Initialized with {AvailableModes.Count} game modes.");
    }

    /// <summary>Call this in GameSelectionView before FireShowView(Matchmaking).</summary>
    public static void SetSelectedMode(GameModeData mode)
    {
        SelectedMode = mode;
        Debug.Log($"[GameModeManager] Selected mode: {mode?.EntryFee}");
    }
}
