using UnityEngine;
using System;

/// <summary>
/// Central cache for all player data fetched from PlayFab.
/// Fetch once on login, read from here everywhere else.
/// Only write back to PlayFab when data changes.
/// </summary>
public static class PlayerDataManager
{
    // ── Player Data ───────────────────────────────────────────────────────────

    public static string DisplayName { get; private set; } = "";
    public static int Coins { get; private set; } = 0;
    public static int Trophies { get; private set; } = 0;
    public static int AvatarIndex { get; private set; } = 0;
    public static bool HasSetupProfile { get; private set; } = false;

    // ── PlayFab ID ────────────────────────────────────────────────────────────

    public static string PlayFabId { get; private set; } = "";

    // ── Initialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after successful login/register.
    /// Populates all fields from PlayFab response.
    /// </summary>
    public static void Initialize(
        string playfabId,
        string displayName,
        int coins,
        PlayerStatsData statsData)
    {
        PlayFabId = playfabId;
        DisplayName = displayName;
        Coins = coins;
        Trophies = statsData.trophies;
        AvatarIndex = statsData.avatarIndex;
        HasSetupProfile = statsData.hasSetupProfile;

        Debug.Log(
            $"[PlayerDataManager] Initialized: {DisplayName}, Coins: {Coins}, Trophies: {Trophies}, Avatar: {AvatarIndex}");
    }

    // ── Update Methods ────────────────────────────────────────────────────────

    public static void UpdateProfile(string newName, int newAvatarIndex)
    {
        DisplayName = newName;
        AvatarIndex = newAvatarIndex;
        HasSetupProfile = true;
    }

    public static void UpdateCoins(int newAmount)
        => Coins = newAmount;

    public static void AddCoins(int amount)
        => Coins += amount;

    public static void UpdateTrophies(int newAmount)
        => Trophies = newAmount;

    public static void AddTrophies(int amount)
        => Trophies += amount;

    // ── Helper ────────────────────────────────────────────────────────────────

    public static int GetTrophyProgress() => Trophies % 20;
    public static int GetTrophyMilestone() => Trophies / 20;

    /// <summary>
    /// Generates a random player name based on login type.
    /// </summary>
    public static string GenerateRandomName(bool isGuest)
    {
        string prefix = isGuest ? "Guest" : "Player";
        int randomNum = UnityEngine.Random.Range(10000, 99999);
        return $"{prefix}{randomNum}";
    }
}

/// <summary>
/// JSON structure for PlayerStats stored in PlayFab UserData.
/// </summary>
[Serializable]
public class PlayerStatsData
{
    public int trophies = 0;
    public int avatarIndex = 0;
    public bool hasSetupProfile = false;
}