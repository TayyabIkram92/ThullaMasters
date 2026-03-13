using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static cache for all player data. All UI reads from here.
/// Only PlayFabManager writes to this via Initialize().
/// Coins are NOT stored here — they are read/written directly via PlayFab.
/// </summary>
public static class PlayerDataManager
{
    // ─── Identity ─────────────────────────────────────────────────────────────
    public static string PlayFabId   { get; private set; }
    public static string DisplayName { get; private set; }

    // ─── Stats ────────────────────────────────────────────────────────────────
    public static int  Trophies              { get; private set; }
    public static int  AvatarIndex           { get; private set; }
    public static bool HasSetupProfile       { get; private set; }
    public static int  LastRewardedMilestone { get; private set; }

    // ─── TitleConfig ──────────────────────────────────────────────────────────
    public static TitleConfigData TitleConfig            { get; private set; }
    public static bool            IsTitleConfigInitialized { get; private set; }

    public static bool IsInitialized { get; private set; }

    // ─── Init ─────────────────────────────────────────────────────────────────
    public static void Initialize(string playFabId, string displayName, PlayerStatsData stats)
    {
        PlayFabId             = playFabId;
        DisplayName           = displayName;
        Trophies              = stats.trophies;
        AvatarIndex           = stats.avatarIndex;
        HasSetupProfile       = stats.hasSetupProfile;
        LastRewardedMilestone = stats.lastRewardedMilestone;
        IsInitialized         = true;
    }

    // ─── Profile Update ───────────────────────────────────────────────────────
    public static void UpdateProfile(string displayName, int avatarIndex)
    {
        DisplayName     = displayName;
        AvatarIndex     = avatarIndex;
        HasSetupProfile = true;
    }

    // ─── Username helper ──────────────────────────────────────────────────────
    public static string GenerateRandomUsername(bool isGuest)
    {
        string[] guestPrefixes  = { "Guest", "GuestPlayer", "Visitor" };
        string[] normalPrefixes = { "Player", "ThullaMaster", "Bhabhi", "Card", "Ace" };

        string prefix = isGuest
            ? guestPrefixes[Random.Range(0, guestPrefixes.Length)]
            : normalPrefixes[Random.Range(0, normalPrefixes.Length)];

        int suffix = Random.Range(1000, 9999);
        return prefix + suffix;
    }

    // ─── Stats Setters ────────────────────────────────────────────────────────
    public static void SetDisplayName(string name)       => DisplayName = name;
    public static void SetAvatarIndex(int idx)           => AvatarIndex = idx;
    public static void SetHasSetupProfile(bool val)      => HasSetupProfile = val;
    public static void SetTrophies(int val)              => Trophies = val;
    public static void SetLastRewardedMilestone(int val) => LastRewardedMilestone = val;

    // ─── Trophy Helpers ───────────────────────────────────────────────────────
    public static int  GetTrophyProgress()      => Trophies % 20;
    public static int  GetCurrentMilestone()    => Trophies / 20;
    public static bool HasUnrewardedMilestone() => GetCurrentMilestone() > LastRewardedMilestone;

    // ─── TitleConfig ──────────────────────────────────────────────────────────
    public static void InitializeTitleConfig(TitleConfigData config)
    {
        TitleConfig              = config;
        IsTitleConfigInitialized = true;
    }

    // ─── Build for saving ─────────────────────────────────────────────────────
    public static PlayerStatsData BuildStatsData()
    {
        return new PlayerStatsData
        {
            trophies              = Trophies,
            avatarIndex           = AvatarIndex,
            hasSetupProfile       = HasSetupProfile,
            lastRewardedMilestone = LastRewardedMilestone
        };
    }

    // ─── Clear (called by PlayFabManager.HandleLogout) ────────────────────────
    public static void Clear()
    {
        PlayFabId                = null;
        DisplayName              = null;
        Trophies                 = 0;
        AvatarIndex              = 0;
        HasSetupProfile          = false;
        LastRewardedMilestone    = 0;
        IsInitialized            = false;
        IsTitleConfigInitialized = false;
        TitleConfig              = null;
    }
}
