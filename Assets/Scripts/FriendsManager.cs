using System.Collections.Generic;
using System.Linq;

public static class FriendsManager
{
    public static List<FriendData> Friends      { get; private set; } = new List<FriendData>();
    public static bool             IsInitialized { get; private set; }

    public static void Initialize(List<FriendData> friends)
    {
        Friends       = friends ?? new List<FriendData>();
        IsInitialized = true;
    }

    public static void AddFriend(FriendData friend)
    {
        if (!Friends.Any(f => f.PlayFabId == friend.PlayFabId))
            Friends.Add(friend);
    }

    public static void RemoveFriend(string playFabId)
        => Friends.RemoveAll(f => f.PlayFabId == playFabId);

    public static FriendData GetFriendByPlayFabId(string playFabId)
        => Friends.FirstOrDefault(f => f.PlayFabId == playFabId);

    public static void Clear()
    {
        Friends.Clear();
        IsInitialized = false;
    }
}
