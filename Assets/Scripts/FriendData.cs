using System;

/// <summary>
/// Represents a friend's public data.
/// Cached locally to avoid repeated PlayFab calls.
/// </summary>
[Serializable]
public class FriendData
{
    public string PlayFabId { get; set; }
    public string DisplayName { get; set; }
    public int Trophies { get; set; }
    public int Coins { get; set; }
    public int AvatarIndex { get; set; }

    public FriendData(string playfabId, string displayName, int trophies, int coins, int avatarIndex)
    {
        PlayFabId = playfabId;
        DisplayName = displayName;
        Trophies = trophies;
        Coins = coins;
        AvatarIndex = avatarIndex;
    }
}