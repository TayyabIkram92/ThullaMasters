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
    public static int LastRewardedMilestone { get; private set; } = 0;

    // ── PlayFab ID ────────────────────────────────────────────────────────────

    public static string PlayFabId { get; private set; } = "";

    // ── Initialization ────────────────────────────────────────────────────────

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
        LastRewardedMilestone = statsData.lastRewardedMilestone;

        Debug.Log($"[PlayerDataManager] Initialized: Name={DisplayName}, Coins={Coins}, Trophies={Trophies}");
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

    public static void UpdateLastRewardedMilestone(int milestone)
        => LastRewardedMilestone = milestone;

    // ── Trophy Helpers ────────────────────────────────────────────────────────

    public static int GetTrophyProgress() => Trophies % 20;
    public static int GetCurrentMilestone() => Trophies / 20;

    public static bool HasUnrewardedMilestone()
    {
        int currentMilestone = GetCurrentMilestone();
        return currentMilestone > LastRewardedMilestone;
    }

    // ── Username Generation ───────────────────────────────────────────────────

    public static string GenerateRandomUsername(bool isGuest)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        System.Random random = new System.Random();
        char[] randomPart = new char[8];

        for (int i = 0; i < 8; i++)
        {
            randomPart[i] = chars[random.Next(chars.Length)];
        }

        string prefix = isGuest ? "Guest" : "Player";
        return $"{prefix}_{new string(randomPart)}";
    }
}

[Serializable]
public class PlayerStatsData
{
    public int trophies = 0;
    public int avatarIndex = 0;
    public bool hasSetupProfile = false;
    public int lastRewardedMilestone = 0;
}