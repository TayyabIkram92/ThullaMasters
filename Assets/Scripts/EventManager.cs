using System;
using UI;

public static partial class EventManager
{
    // ── UI Events ─────────────────────────────────────────────────────────────

    public static event Action<ViewType, bool> OnShowView;
    public static event Action<ViewType> OnHideView;
    public static event Action OnHideAllViews;
    public static event Action<string> OnShowPopUp;

    public static void FireShowView(ViewType viewType, bool showAsDialogue = false)
        => OnShowView?.Invoke(viewType, showAsDialogue);

    public static void FireHideView(ViewType viewType)
        => OnHideView?.Invoke(viewType);

    public static void FireHideAllViews()
        => OnHideAllViews?.Invoke();

    public static void FireShowPopUp(string message)
        => OnShowPopUp?.Invoke(message);

    // ── PlayFab Request Events (UI → PlayFab) ─────────────────────────────────

    public static event Action<string, string> OnLoginRequested;
    public static event Action<string, string> OnRegisterAndLoginRequested;
    public static event Action OnGuestLoginRequested;
    public static event Action OnAutoLoginRequested;

    public static void FireLoginRequested(string email, string password)
        => OnLoginRequested?.Invoke(email, password);

    public static void FireRegisterAndLoginRequested(string email, string password)
        => OnRegisterAndLoginRequested?.Invoke(email, password);

    public static void FireGuestLoginRequested()
        => OnGuestLoginRequested?.Invoke();

    public static void FireAutoLoginRequested()
        => OnAutoLoginRequested?.Invoke();

    // ── Username Validation Events ────────────────────────────────────────────

    /// <summary>Request to check if username is available.</summary>
    public static event Action<string, System.Action<bool>> OnCheckUsernameAvailability;

    public static void FireCheckUsernameAvailability(string username, System.Action<bool> callback)
        => OnCheckUsernameAvailability?.Invoke(username, callback);

    // ── PlayFab Result Events (PlayFab → UI) ──────────────────────────────────

    public static event Action OnAuthSuccess;
    public static event Action<bool> OnAuthConflict;
    public static event Action<bool> OnAutoLoginChecked;

    public static void FireAuthSuccess()
        => OnAuthSuccess?.Invoke();

    public static void FireAuthConflict(bool triedLogin)
        => OnAuthConflict?.Invoke(triedLogin);

    public static void FireAutoLoginChecked(bool isLoggedIn)
        => OnAutoLoginChecked?.Invoke(isLoggedIn);

    // ── Player Data Events ────────────────────────────────────────────────────

    /// <summary>Fired after player data is fetched and cached.</summary>
    public static event Action OnPlayerDataLoaded;

    /// <summary>Request to update profile on PlayFab.</summary>
    public static event Action<string, int> OnUpdateProfileRequested;

    /// <summary>Request to award trophy reward coins.</summary>
    public static event Action OnAwardTrophyRewardRequested;

    public static void FirePlayerDataLoaded()
        => OnPlayerDataLoaded?.Invoke();

    public static void FireUpdateProfileRequested(string displayName, int avatarIndex)
        => OnUpdateProfileRequested?.Invoke(displayName, avatarIndex);

    public static void FireAwardTrophyRewardRequested()
        => OnAwardTrophyRewardRequested?.Invoke();

    // ── Coin Management Events ────────────────────────────────────────────────

    /// <summary>Request to deduct coins from PlayFab.</summary>
    public static event Action<int> OnDeductCoinsRequested;

    public static void FireDeductCoinsRequested(int amount)
        => OnDeductCoinsRequested?.Invoke(amount);

    // ── Game Mode Events ──────────────────────────────────────────────────────

    /// <summary>Request to fetch game mode config from PlayFab.</summary>
    public static event Action OnFetchGameModesRequested;

    /// <summary>Game modes fetched and cached.</summary>
    public static event Action OnGameModesFetched;

    /// <summary>User selected a game mode card.</summary>
    public static event Action<GameModeData> OnGameModeSelected;

    public static void FireFetchGameModesRequested()
        => OnFetchGameModesRequested?.Invoke();

    public static void FireGameModesFetched()
        => OnGameModesFetched?.Invoke();

    public static void FireGameModeSelected(GameModeData modeData)
        => OnGameModeSelected?.Invoke(modeData);

    // ── Friends Events ────────────────────────────────────────────────────────

    /// <summary>Request to fetch friends list from PlayFab.</summary>
    public static event Action OnFetchFriendsRequested;

    /// <summary>Friends list fetched and cached.</summary>
    public static event Action OnFriendsFetched;

    /// <summary>Request to add friend by Player ID.</summary>
    public static event Action<string> OnAddFriendRequested;

    /// <summary>Friend added successfully.</summary>
    public static event Action<FriendData> OnFriendAdded;

    /// <summary>Failed to add friend (invalid ID, etc.).</summary>
    public static event Action<string> OnAddFriendFailed;

    /// <summary>Request to remove friend.</summary>
    public static event Action<string> OnRemoveFriendRequested;

    /// <summary>Friend removed successfully.</summary>
    public static event Action OnFriendRemoved;

    public static void FireFetchFriendsRequested()
        => OnFetchFriendsRequested?.Invoke();

    public static void FireFriendsFetched()
        => OnFriendsFetched?.Invoke();

    public static void FireAddFriendRequested(string playerId)
        => OnAddFriendRequested?.Invoke(playerId);

    public static void FireFriendAdded(FriendData friend)
        => OnFriendAdded?.Invoke(friend);

    public static void FireAddFriendFailed(string reason)
        => OnAddFriendFailed?.Invoke(reason);

    public static void FireRemoveFriendRequested(string playfabId)
        => OnRemoveFriendRequested?.Invoke(playfabId);

    public static void FireFriendRemoved()
        => OnFriendRemoved?.Invoke();

    // ── Sounds Manager Events ────────────────────────────────────────────────────────

    /// <summary>Request to turn music or sound On/Off.</summary>
    public static event Action<bool> OnTurnMusicOnOrOff;

    public static void FireTurnMusicOnOrOff(bool on)
        => OnTurnMusicOnOrOff?.Invoke(on);

    public static event Action<bool> OnTurnSoundOnOrOff;

    public static void FireTurnSoundOnOrOff(bool on)
        => OnTurnSoundOnOrOff?.Invoke(on);
}