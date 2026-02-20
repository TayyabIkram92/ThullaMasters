using System;

[Serializable]
public class FriendData
{
    public string PlayFabId { get; set; }
    public string DisplayName { get; set; }
    public int Trophies { get; set; }
    public int AvatarIndex { get; set; }

    public FriendData(string playfabId, string displayName, int trophies, int avatarIndex)
    {
        PlayFabId = playfabId;
        DisplayName = displayName;
        Trophies = trophies;
        AvatarIndex = avatarIndex;
    }
}