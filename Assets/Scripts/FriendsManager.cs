using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Caches friends list fetched from PlayFab.
/// Fetch once per session, update only when friends added/removed.
/// </summary>
public static class FriendsManager
{
    public static List<FriendData> Friends { get; private set; } = new List<FriendData>();
    public static bool IsInitialized { get; private set; } = false;

    public static void Initialize(List<FriendData> friends)
    {
        Friends = friends;
        IsInitialized = true;
        Debug.Log($"[FriendsManager] Initialized with {Friends.Count} friends.");
    }

    public static void AddFriend(FriendData friend)
    {
        if (!Friends.Exists(f => f.PlayFabId == friend.PlayFabId))
        {
            Friends.Add(friend);
            Debug.Log($"[FriendsManager] Added friend: {friend.DisplayName}");
        }
    }

    public static void RemoveFriend(string playfabId)
    {
        int removed = Friends.RemoveAll(f => f.PlayFabId == playfabId);
        if (removed > 0)
            Debug.Log($"[FriendsManager] Removed friend with ID: {playfabId}");
    }

    public static FriendData GetFriendByUsername(string username)
    {
        return Friends.Find(f => f.DisplayName == username);
    }

    public static void Clear()
    {
        Friends.Clear();
        IsInitialized = false;
    }
}