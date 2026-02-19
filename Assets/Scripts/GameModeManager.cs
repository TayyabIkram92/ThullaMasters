using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Caches game mode configurations fetched from PlayFab.
/// Fetch once, read everywhere.
/// </summary>
public static class GameModeManager
{
    public static List<GameModeData> AvailableModes { get; private set; } = new List<GameModeData>();
    public static bool IsInitialized { get; private set; } = false;

    /// <summary>
    /// Call this after fetching from PlayFab Title Data.
    /// </summary>
    public static void Initialize(List<int> entryFees)
    {
        AvailableModes.Clear();

        foreach (int fee in entryFees)
        {
            AvailableModes.Add(new GameModeData(fee));
        }

        IsInitialized = true;
        Debug.Log($"[GameModeManager] Initialized with {AvailableModes.Count} game modes.");
    }
}