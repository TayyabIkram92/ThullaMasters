using System;
using System.Collections.Generic;

// Partial class: UI, Auth, PlayerData, Coins, GameModes, Friends, Sounds, TitleData
public static partial class EventManager
{
    public static event Action<string> OnUpdateCoinsUI;
    public static void FireUpdateCoinsUI(string coins) => OnUpdateCoinsUI?.Invoke(coins);

    // ─── UI ───────────────────────────────────────────────────────────────────
    public static event Action<ViewType, bool> OnShowView;

    public static void FireShowView(ViewType viewType, bool showAsDialogue = false)
        => OnShowView?.Invoke(viewType, showAsDialogue);

    public static event Action<ViewType> OnHideView;

    public static void FireHideView(ViewType viewType)
        => OnHideView?.Invoke(viewType);

    public static event Action<string> OnShowPopUp;

    public static void FireShowPopUp(string message) => OnShowPopUp?.Invoke(message);
    public static event Action OnInviteSent;
    public static void FireInviteSent() => OnInviteSent?.Invoke();
// (OnInviteAccepted, OnInviteRejected, OnInviteResponseRequested already exist from previous session)

// Events
    public static event Action<string, string, bool> OnInviteResponseRequested;
    public static event Action<string> OnInviteAccepted;
    public static event Action<string> OnInviteRejected;

// Fire methods
    public static void FireInviteResponseRequested(string roomId, string senderId, bool accepted)
        => OnInviteResponseRequested?.Invoke(roomId, senderId, accepted);

    public static void FireInviteAccepted(string playFabId)
        => OnInviteAccepted?.Invoke(playFabId);

    public static void FireInviteRejected(string playFabId)
        => OnInviteRejected?.Invoke(playFabId);

    // ─── Auth ─────────────────────────────────────────────────────────────────
    public static event Action OnAutoLoginRequested;
    public static void FireAutoLoginRequested() => OnAutoLoginRequested?.Invoke();

    // bool = isLoggedIn — matches LoadingView: void HandleAutoLoginChecked(bool isLoggedIn)
    public static event Action<bool> OnAutoLoginChecked;
    public static void FireAutoLoginChecked(bool isLoggedIn) => OnAutoLoginChecked?.Invoke(isLoggedIn);

    public static event Action<string, string> OnLoginRequested;

    public static void FireLoginRequested(string email, string password)
        => OnLoginRequested?.Invoke(email, password);

    // 2 params — matches SignUpLoginView: FireRegisterAndLoginRequested(email, password)
    public static event Action<string, string> OnRegisterAndLoginRequested;

    public static void FireRegisterAndLoginRequested(string email, string password)
        => OnRegisterAndLoginRequested?.Invoke(email, password);

    public static event Action OnGuestLoginRequested;
    public static void FireGuestLoginRequested() => OnGuestLoginRequested?.Invoke();

    public static event Action OnLogoutRequested;
    public static void FireLogoutRequested() => OnLogoutRequested?.Invoke();

    public static event Action OnAuthSuccess;
    public static void FireAuthSuccess() => OnAuthSuccess?.Invoke();

    // bool = triedLogin — matches SignUpLoginView: void HandleAuthConflict(bool triedLogin)
    public static event Action<bool> OnAuthConflict;
    public static void FireAuthConflict(bool triedLogin) => OnAuthConflict?.Invoke(triedLogin);

    // ─── PlayerData ───────────────────────────────────────────────────────────
    public static event Action OnPlayerDataLoaded;
    public static void FirePlayerDataLoaded() => OnPlayerDataLoaded?.Invoke();

    public static event Action<string, int> OnUpdateProfileRequested;

    public static void FireUpdateProfileRequested(string username, int avatarIndex)
        => OnUpdateProfileRequested?.Invoke(username, avatarIndex);

    // 2 params — matches ProfileView: FireCheckUsernameAvailability(username, callback)
    public static event Action<string, Action<bool>> OnCheckUsernameAvailability;

    public static void FireCheckUsernameAvailability(string username, Action<bool> callback)
        => OnCheckUsernameAvailability?.Invoke(username, callback);

    public static event Action OnProfileUpdateSuccess;

    public static void FireProfileUpdateSuccess() => OnProfileUpdateSuccess?.Invoke();

// Action<Action<int>> — caller passes a callback, receives coin amount
    public static event Action<Action<int>> OnGetCoinsRequested;

    public static void FireGetCoinsRequested(Action<int> callback) => OnGetCoinsRequested?.Invoke(callback);

    // ─── Coins ────────────────────────────────────────────────────────────────
    public static event Action OnFetchCoinsRequested;
    public static void FireFetchCoinsRequested() => OnFetchCoinsRequested?.Invoke();

    public static event Action<int> OnCoinsUpdated;
    public static void FireCoinsUpdated(int newAmount) => OnCoinsUpdated?.Invoke(newAmount);

    public static event Action<int, string> OnAddCoinsRequested;

    public static void FireAddCoinsRequested(int amount, string reason)
        => OnAddCoinsRequested?.Invoke(amount, reason);

    // 1 param — matches ProfileView: FireDeductCoinsRequested(10)
    public static event Action<int> OnDeductCoinsRequested;

    public static void FireDeductCoinsRequested(int amount)
        => OnDeductCoinsRequested?.Invoke(amount);

    public static event Action OnAwardTrophyRewardRequested;
    public static void FireAwardTrophyRewardRequested() => OnAwardTrophyRewardRequested?.Invoke();

    // ─── GameModes ────────────────────────────────────────────────────────────
    public static event Action OnFetchGameModesRequested;
    public static void FireFetchGameModesRequested() => OnFetchGameModesRequested?.Invoke();

    public static event Action OnGameModesFetched;
    public static void FireGameModesFetched() => OnGameModesFetched?.Invoke();

    public static event Action<GameModeData> OnGameModeSelected;

    public static void FireGameModeSelected(GameModeData mode)
        => OnGameModeSelected?.Invoke(mode);

    // ─── Friends ──────────────────────────────────────────────────────────────
    public static event Action OnFetchFriendsRequested;
    public static void FireFetchFriendsRequested() => OnFetchFriendsRequested?.Invoke();

    public static event Action OnFriendsFetched;
    public static void FireFriendsFetched() => OnFriendsFetched?.Invoke();

    public static event Action<string> OnAddFriendRequested;

    public static void FireAddFriendRequested(string username)
        => OnAddFriendRequested?.Invoke(username);

    public static event Action<string> OnRemoveFriendRequested;

    public static void FireRemoveFriendRequested(string playFabId)
        => OnRemoveFriendRequested?.Invoke(playFabId);

    public static event Action<string> OnFriendAdded;
    public static void FireFriendAdded(string playFabId) => OnFriendAdded?.Invoke(playFabId);

    public static event Action<string> OnFriendRemoved;
    public static void FireFriendRemoved(string playFabId) => OnFriendRemoved?.Invoke(playFabId);

    public static event Action<string> OnAddFriendFailed;
    public static void FireAddFriendFailed(string message) => OnAddFriendFailed?.Invoke(message);

    // ─── Sounds ───────────────────────────────────────────────────────────────

    public static event Action<SoundType> OnPlaySound;
    public static void FirePlaySound(SoundType soundType) => OnPlaySound?.Invoke(soundType);

    public static event Action<bool> OnTurnMusicOnOrOff;
    public static void FireTurnMusicOnOrOff(bool on) => OnTurnMusicOnOrOff?.Invoke(on);

    public static event Action<bool> OnTurnSoundOnOrOff;
    public static void FireTurnSoundOnOrOff(bool on) => OnTurnSoundOnOrOff?.Invoke(on);

    public static event Action<bool> OnMusicStateChanged;
    public static void FireMusicStateChanged(bool isOn) => OnMusicStateChanged?.Invoke(isOn);

    public static event Action<bool> OnSoundStateChanged;
    public static void FireSoundStateChanged(bool isOn) => OnSoundStateChanged?.Invoke(isOn);

    // ─── TitleData / Config ───────────────────────────────────────────────────
    public static event Action OnFetchTitleDataRequested;
    public static void FireFetchTitleDataRequested() => OnFetchTitleDataRequested?.Invoke();

    public static event Action OnTitleDataFetched;
    public static void FireTitleDataFetched() => OnTitleDataFetched?.Invoke();
}