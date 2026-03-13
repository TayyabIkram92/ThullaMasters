using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PlayFab;
using PlayFab.ClientModels;

/// <summary>
/// Single class handling ALL PlayFab API calls.
/// No Newtonsoft — uses JsonUtility.
/// Coins are read/written directly from PlayFab. No local coin cache.
/// </summary>
public class PlayFabManager : MonoBehaviour
{
    private const string GuestIdKey = "GuestCustomID";
    private const string SavedEmailKey = "SavedEmail";
    private const string SavedPasswordKey = "SavedPassword";
    private const string PlayerStatsKey = "PlayerStats";
    private const string TitleConfigKey = "AppConfig";
    private const string CoinCurrencyCode = "CO";

    private void OnEnable()
    {
        EventManager.OnAutoLoginRequested += HandleAutoLogin;
        EventManager.OnLoginRequested += HandleLogin;
        EventManager.OnRegisterAndLoginRequested += HandleRegisterAndLogin;
        EventManager.OnGuestLoginRequested += HandleGuestLogin;
        EventManager.OnLogoutRequested += HandleLogout;

        EventManager.OnUpdateProfileRequested += HandleUpdateProfile;
        EventManager.OnCheckUsernameAvailability += HandleCheckUsername;

        EventManager.OnFetchCoinsRequested += HandleFetchCoins;
        EventManager.OnAddCoinsRequested += HandleAddCoins;
        EventManager.OnDeductCoinsRequested += HandleDeductCoins;
        EventManager.OnAwardTrophyRewardRequested += HandleAwardTrophyReward;

        EventManager.OnFetchGameModesRequested += HandleFetchTitleData;
        EventManager.OnFetchTitleDataRequested += HandleFetchTitleData;

        EventManager.OnFetchFriendsRequested += HandleFetchFriends;
        EventManager.OnAddFriendRequested += HandleAddFriend;
        EventManager.OnRemoveFriendRequested += HandleRemoveFriend;
        EventManager.OnGetCoinsRequested += HandleGetCoins;
    }

    private void OnDisable()
    {
        EventManager.OnAutoLoginRequested -= HandleAutoLogin;
        EventManager.OnLoginRequested -= HandleLogin;
        EventManager.OnRegisterAndLoginRequested -= HandleRegisterAndLogin;
        EventManager.OnGuestLoginRequested -= HandleGuestLogin;
        EventManager.OnLogoutRequested -= HandleLogout;

        EventManager.OnUpdateProfileRequested -= HandleUpdateProfile;
        EventManager.OnCheckUsernameAvailability -= HandleCheckUsername;

        EventManager.OnFetchCoinsRequested -= HandleFetchCoins;
        EventManager.OnAddCoinsRequested -= HandleAddCoins;
        EventManager.OnDeductCoinsRequested -= HandleDeductCoins;
        EventManager.OnAwardTrophyRewardRequested -= HandleAwardTrophyReward;

        EventManager.OnFetchGameModesRequested -= HandleFetchTitleData;
        EventManager.OnFetchTitleDataRequested -= HandleFetchTitleData;

        EventManager.OnFetchFriendsRequested -= HandleFetchFriends;
        EventManager.OnAddFriendRequested -= HandleAddFriend;
        EventManager.OnRemoveFriendRequested -= HandleRemoveFriend;
        EventManager.OnGetCoinsRequested -= HandleGetCoins;
    }

    private void HandleGetCoins(Action<int> callback)
    {
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            result =>
            {
                int coins = 0;
                if (result.VirtualCurrency != null &&
                    result.VirtualCurrency.TryGetValue(CoinCurrencyCode, out int c))
                    coins = c;
                CoinsUIManager.UpdateCoins(coins);
                callback?.Invoke(coins);
            },
            err =>
            {
                Debug.LogWarning("[PlayFab] GetCoins failed: " + err.ErrorMessage);
                callback?.Invoke(0);
            });
    }
    // ══════════════════════════════════════════════════════════════════════════
    // AUTH
    // ══════════════════════════════════════════════════════════════════════════

    private void HandleAutoLogin()
    {
        // Try saved email credentials first
        string savedEmail = PlayerPrefs.GetString(SavedEmailKey, string.Empty);
        string savedPassword = PlayerPrefs.GetString(SavedPasswordKey, string.Empty);

        if (!string.IsNullOrEmpty(savedEmail) && !string.IsNullOrEmpty(savedPassword))
        {
            PlayFabClientAPI.LoginWithEmailAddress(
                new LoginWithEmailAddressRequest { Email = savedEmail, Password = savedPassword },
                result => StartCoroutine(FetchPlayerDataAfterLogin(result.PlayFabId)),
                error =>
                {
                    // Saved credentials invalid — clear them and fall back to login screen
                    PlayerPrefs.DeleteKey(SavedEmailKey);
                    PlayerPrefs.DeleteKey(SavedPasswordKey);
                    PlayerPrefs.Save();
                    EventManager.FireAutoLoginChecked(false);
                    EventManager.FireShowView(ViewType.SignUpLogin);
                });
            return;
        }

        // Fall back to guest auto-login
        string customId = PlayerPrefs.GetString(GuestIdKey, string.Empty);
        if (string.IsNullOrEmpty(customId))
        {
            EventManager.FireAutoLoginChecked(false);
            EventManager.FireShowView(ViewType.SignUpLogin);
            return;
        }

        PlayFabClientAPI.LoginWithCustomID(
            new LoginWithCustomIDRequest { CustomId = customId, CreateAccount = false },
            result => StartCoroutine(FetchPlayerDataAfterLogin(result.PlayFabId)),
            error =>
            {
                EventManager.FireAutoLoginChecked(false);
                EventManager.FireShowView(ViewType.SignUpLogin);
            });
    }

    private void HandleLogin(string email, string password)
    {
        PlayFabClientAPI.LoginWithEmailAddress(
            new LoginWithEmailAddressRequest { Email = email, Password = password },
            result =>
            {
                PlayerPrefs.SetString(SavedEmailKey, email);
                PlayerPrefs.SetString(SavedPasswordKey, password);
                PlayerPrefs.Save();
                StartCoroutine(FetchPlayerDataAfterLogin(result.PlayFabId));
            },
            error => { EventManager.FireAuthConflict(true); });
    }

    private void HandleRegisterAndLogin(string email, string password)
    {
        string username = email.Contains("@") ? email.Split('@')[0] : email;

        PlayFabClientAPI.RegisterPlayFabUser(
            new RegisterPlayFabUserRequest
            {
                Email = email,
                Password = password,
                Username = username,
                DisplayName = username
            },
            result => HandleLogin(email, password),
            error => { EventManager.FireAuthConflict(false); });
    }

    private void HandleGuestLogin()
    {
        string customId = PlayerPrefs.GetString(GuestIdKey, string.Empty);
        if (string.IsNullOrEmpty(customId))
        {
            customId = Guid.NewGuid().ToString();
            PlayerPrefs.SetString(GuestIdKey, customId);
            PlayerPrefs.Save();
        }

        PlayFabClientAPI.LoginWithCustomID(
            new LoginWithCustomIDRequest { CustomId = customId, CreateAccount = true },
            result => StartCoroutine(FetchPlayerDataAfterLogin(result.PlayFabId)),
            error => EventManager.FireShowPopUp("Guest login failed: " + error.ErrorMessage));
    }

    private void HandleLogout()
    {
        PlayFabClientAPI.ForgetAllCredentials();
        PlayerPrefs.DeleteKey(SavedEmailKey);
        PlayerPrefs.DeleteKey(SavedPasswordKey);
        PlayerPrefs.Save();
        PlayerDataManager.Clear();
        GameModeManager.Clear();
        FriendsManager.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PLAYER DATA
    // ══════════════════════════════════════════════════════════════════════════

    private IEnumerator FetchPlayerDataAfterLogin(string playFabId)
    {
        yield return null; // one frame

        PlayFabClientAPI.GetPlayerCombinedInfo(
            new GetPlayerCombinedInfoRequest
            {
                InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                {
                    GetUserAccountInfo = true,
                    GetUserVirtualCurrency = true,
                    GetUserData = true
                }
            },
            result =>
            {
                string displayName = result.InfoResultPayload?.AccountInfo?.TitleInfo?.DisplayName ?? "Player";

                int coins = 0;
                if (result.InfoResultPayload?.UserVirtualCurrency != null &&
                    result.InfoResultPayload.UserVirtualCurrency.TryGetValue(CoinCurrencyCode, out int c))
                    coins = c;

                PlayerStatsData stats = new PlayerStatsData();
                if (result.InfoResultPayload?.UserData != null &&
                    result.InfoResultPayload.UserData.TryGetValue(PlayerStatsKey, out var entry))
                {
                    try
                    {
                        stats = JsonUtility.FromJson<PlayerStatsData>(entry.Value) ?? new PlayerStatsData();
                    }
                    catch
                    {
                        stats = new PlayerStatsData();
                    }
                }

                PlayerDataManager.Initialize(playFabId, displayName, stats);
                CoinsUIManager.UpdateCoins(coins);
                HandleFetchTitleData();

                EventManager.FireAuthSuccess();
                EventManager.FirePlayerDataLoaded();
                EventManager.FireAutoLoginChecked(true);

                if (!PlayerDataManager.HasSetupProfile)
                    EventManager.FireShowView(ViewType.Profile, true);
                else
                    EventManager.FireShowView(ViewType.Home);

                if (PlayerDataManager.HasUnrewardedMilestone())
                    EventManager.FireAwardTrophyRewardRequested();
            },
            error =>
            {
                EventManager.FireAutoLoginChecked(false);
                EventManager.FireShowPopUp("Failed to load player data: " + error.ErrorMessage);
                EventManager.FireShowView(ViewType.SignUpLogin);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PROFILE
    // ══════════════════════════════════════════════════════════════════════════

    private void HandleUpdateProfile(string username, int avatarIndex)
    {
        PlayFabClientAPI.UpdateUserTitleDisplayName(
            new UpdateUserTitleDisplayNameRequest { DisplayName = username },
            nameResult =>
            {
                PlayerDataManager.UpdateProfile(username, avatarIndex);
                SavePlayerStats(() => EventManager.FireProfileUpdateSuccess());
            },
            error => EventManager.FireShowPopUp("Profile update failed: " + error.ErrorMessage));
    }

    private void HandleCheckUsername(string username, Action<bool> callback)
    {
        PlayFabClientAPI.GetAccountInfo(
            new GetAccountInfoRequest { TitleDisplayName = username },
            result =>
            {
                bool taken = result.AccountInfo?.TitleInfo?.DisplayName
                    ?.Equals(username, StringComparison.OrdinalIgnoreCase) == true;
                callback?.Invoke(!taken);
            },
            error => callback?.Invoke(true));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // COINS — all reads/writes go directly to PlayFab, UI updated via CoinsUIManager
    // ══════════════════════════════════════════════════════════════════════════

    private void HandleFetchCoins()
    {
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            result =>
            {
                int coins = 0;
                if (result.VirtualCurrency != null &&
                    result.VirtualCurrency.TryGetValue(CoinCurrencyCode, out int c))
                    coins = c;
                CoinsUIManager.UpdateCoins(coins);
            },
            error => Debug.LogWarning("[PlayFab] FetchCoins failed: " + error.ErrorMessage));
    }

    private void HandleAddCoins(int amount, string reason)
    {
        PlayFabClientAPI.AddUserVirtualCurrency(
            new AddUserVirtualCurrencyRequest { VirtualCurrency = CoinCurrencyCode, Amount = amount },
            result =>
            {
                CoinsUIManager.UpdateCoins(result.Balance);
                SavePlayerStats(null);
            },
            err => Debug.LogWarning("[PlayFab] AddCoins failed: " + err.ErrorMessage));
    }

    private void HandleDeductCoins(int amount)
    {
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            result =>
            {
                int live = 0;
                if (result.VirtualCurrency != null &&
                    result.VirtualCurrency.TryGetValue(CoinCurrencyCode, out int c))
                    live = c;

                if (live < amount)
                {
                    EventManager.FireShowPopUp("Not enough coins!");
                    return;
                }

                PlayFabClientAPI.SubtractUserVirtualCurrency(
                    new SubtractUserVirtualCurrencyRequest { VirtualCurrency = CoinCurrencyCode, Amount = amount },
                    subResult =>
                    {
                        CoinsUIManager.UpdateCoins(subResult.Balance);
                        SavePlayerStats(null);
                    },
                    err => Debug.LogWarning("[PlayFab] DeductCoins failed: " + err.ErrorMessage));
            },
            err => Debug.LogWarning("[PlayFab] GetInventory(deduct) failed: " + err.ErrorMessage));
    }

    private void HandleAwardTrophyReward()
    {
        // HandleAddCoins(10, "trophy_milestone_reward");
        PlayerDataManager.SetLastRewardedMilestone(PlayerDataManager.GetCurrentMilestone());
        SavePlayerStats(null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TITLE DATA
    // ══════════════════════════════════════════════════════════════════════════

    private void HandleFetchTitleData()
    {
        if (PlayerDataManager.IsTitleConfigInitialized) return;

        PlayFabClientAPI.GetTitleData(
            new GetTitleDataRequest { Keys = new List<string> { TitleConfigKey } },
            result =>
            {
                if (result.Data != null && result.Data.TryGetValue(TitleConfigKey, out string json))
                {
                    try
                    {
                        var config = JsonUtility.FromJson<TitleConfigData>(json);
                        if (config != null)
                        {
                            PlayerDataManager.InitializeTitleConfig(config);
                            GameModeManager.Initialize(config.entryFees);
                            EventManager.FireGameModesFetched();
                            EventManager.FireTitleDataFetched();
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[PlayFab] Failed to parse TitleConfigData: " + e.Message);
                    }
                }
            },
            error => Debug.LogWarning("[PlayFab] GetTitleData failed: " + error.ErrorMessage));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FRIENDS
    // ══════════════════════════════════════════════════════════════════════════

    private void HandleFetchFriends()
    {
        PlayFabClientAPI.GetFriendsList(
            new GetFriendsListRequest(),
            result =>
            {
                var friends = new List<FriendData>();
                if (result.Friends == null || result.Friends.Count == 0)
                {
                    FriendsManager.Initialize(friends);
                    EventManager.FireFriendsFetched();
                    return;
                }

                int remaining = result.Friends.Count;
                foreach (var f in result.Friends)
                {
                    string friendId = f.FriendPlayFabId;
                    PlayFabClientAPI.GetUserData(
                        new GetUserDataRequest { PlayFabId = friendId, Keys = new List<string> { PlayerStatsKey } },
                        dataResult =>
                        {
                            int trophies = 0, avatar = 0;
                            if (dataResult.Data != null &&
                                dataResult.Data.TryGetValue(PlayerStatsKey, out var dataEntry))
                            {
                                try
                                {
                                    var s = JsonUtility.FromJson<PlayerStatsData>(dataEntry.Value);
                                    if (s != null)
                                    {
                                        trophies = s.trophies;
                                        avatar = s.avatarIndex;
                                    }
                                }
                                catch
                                {
                                }
                            }

                            friends.Add(new FriendData
                            {
                                PlayFabId = friendId,
                                DisplayName = f.TitleDisplayName ?? "Player",
                                Trophies = trophies,
                                AvatarIndex = avatar
                            });
                            if (--remaining <= 0)
                            {
                                FriendsManager.Initialize(friends);
                                EventManager.FireFriendsFetched();
                            }
                        },
                        _ =>
                        {
                            friends.Add(new FriendData
                            {
                                PlayFabId = friendId,
                                DisplayName = f.TitleDisplayName ?? "Player"
                            });
                            if (--remaining <= 0)
                            {
                                FriendsManager.Initialize(friends);
                                EventManager.FireFriendsFetched();
                            }
                        });
                }
            },
            error => Debug.LogWarning("[PlayFab] GetFriendsList failed: " + error.ErrorMessage));
    }

    private void HandleAddFriend(string username)
    {
        PlayFabClientAPI.GetAccountInfo(
            new GetAccountInfoRequest { TitleDisplayName = username },
            result =>
            {
                string friendId = result.AccountInfo?.PlayFabId;
                if (string.IsNullOrEmpty(friendId))
                {
                    EventManager.FireAddFriendFailed("Player not found.");
                    return;
                }

                if (friendId == PlayerDataManager.PlayFabId)
                {
                    EventManager.FireAddFriendFailed("You cannot add yourself.");
                    return;
                }

                PlayFabClientAPI.AddFriend(
                    new AddFriendRequest { FriendPlayFabId = friendId },
                    _ =>
                    {
                        PlayFabClientAPI.GetUserData(
                            new GetUserDataRequest { PlayFabId = friendId, Keys = new List<string> { PlayerStatsKey } },
                            dataResult =>
                            {
                                int trophies = 0, avatar = 0;
                                if (dataResult.Data != null && dataResult.Data.TryGetValue(PlayerStatsKey, out var e2))
                                {
                                    try
                                    {
                                        var s = JsonUtility.FromJson<PlayerStatsData>(e2.Value);
                                        if (s != null)
                                        {
                                            trophies = s.trophies;
                                            avatar = s.avatarIndex;
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }

                                FriendsManager.AddFriend(new FriendData
                                {
                                    PlayFabId = friendId,
                                    DisplayName = result.AccountInfo?.TitleInfo?.DisplayName ?? username,
                                    Trophies = trophies,
                                    AvatarIndex = avatar
                                });
                                EventManager.FireFriendAdded(friendId);
                            },
                            _ =>
                            {
                                FriendsManager.AddFriend(new FriendData
                                {
                                    PlayFabId = friendId,
                                    DisplayName = username
                                });
                                EventManager.FireFriendAdded(friendId);
                            });
                    },
                    error => EventManager.FireAddFriendFailed("Could not add: " + error.ErrorMessage));
            },
            error => EventManager.FireAddFriendFailed("Player not found."));
    }

    private void HandleRemoveFriend(string playFabId)
    {
        PlayFabClientAPI.RemoveFriend(
            new RemoveFriendRequest { FriendPlayFabId = playFabId },
            _ =>
            {
                FriendsManager.RemoveFriend(playFabId);
                EventManager.FireFriendRemoved(playFabId);
            },
            error => Debug.LogWarning("[PlayFab] RemoveFriend failed: " + error.ErrorMessage));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private void SavePlayerStats(Action onComplete)
    {
        var stats = PlayerDataManager.BuildStatsData();
        string json = JsonUtility.ToJson(stats);
        PlayFabClientAPI.UpdateUserData(
            new UpdateUserDataRequest { Data = new Dictionary<string, string> { { PlayerStatsKey, json } } },
            _ => onComplete?.Invoke(),
            error => Debug.LogWarning("[PlayFab] SavePlayerStats failed: " + error.ErrorMessage));
    }
}