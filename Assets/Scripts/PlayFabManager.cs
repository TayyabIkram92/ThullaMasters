using UnityEngine;
using PlayFab;
using PlayFab.ClientModels;
using System.Collections;
using System.Collections.Generic;

public class PlayFabManager : MonoBehaviour
{
    private const string GuestIdKey = "GuestCustomID";

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        EventManager.OnAutoLoginRequested += HandleAutoLogin;
        EventManager.OnLoginRequested += HandleLogin;
        EventManager.OnRegisterAndLoginRequested += HandleRegisterAndLogin;
        EventManager.OnGuestLoginRequested += HandleGuestLogin;
        EventManager.OnUpdateProfileRequested += HandleUpdateProfile;
        EventManager.OnAwardTrophyRewardRequested += HandleAwardTrophyReward;
        EventManager.OnDeductCoinsRequested += HandleDeductCoins;
        EventManager.OnFetchGameModesRequested += HandleFetchGameModes;
        EventManager.OnCheckUsernameAvailability += HandleCheckUsernameAvailability;
        EventManager.OnFetchFriendsRequested += HandleFetchFriends;
        EventManager.OnAddFriendRequested += HandleAddFriend;
        EventManager.OnRemoveFriendRequested += HandleRemoveFriend;
    }

    private void OnDisable()
    {
        EventManager.OnAutoLoginRequested -= HandleAutoLogin;
        EventManager.OnLoginRequested -= HandleLogin;
        EventManager.OnRegisterAndLoginRequested -= HandleRegisterAndLogin;
        EventManager.OnGuestLoginRequested -= HandleGuestLogin;
        EventManager.OnUpdateProfileRequested -= HandleUpdateProfile;
        EventManager.OnAwardTrophyRewardRequested -= HandleAwardTrophyReward;
        EventManager.OnDeductCoinsRequested -= HandleDeductCoins;
        EventManager.OnFetchGameModesRequested -= HandleFetchGameModes;
        EventManager.OnCheckUsernameAvailability -= HandleCheckUsernameAvailability;
        EventManager.OnFetchFriendsRequested -= HandleFetchFriends;
        EventManager.OnAddFriendRequested -= HandleAddFriend;
        EventManager.OnRemoveFriendRequested -= HandleRemoveFriend;
    }

    // ── Auto Login ────────────────────────────────────────────────────────────

    private void HandleAutoLogin()
    {
        if (PlayerPrefs.HasKey(GuestIdKey))
        {
            PlayFabClientAPI.LoginWithCustomID(
                new LoginWithCustomIDRequest
                {
                    CustomId = PlayerPrefs.GetString(GuestIdKey),
                    CreateAccount = false,
                    InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                    {
                        GetPlayerProfile = true,
                        GetUserVirtualCurrency = true,
                        GetUserData = true
                    }
                },
                result =>
                {
                    Debug.Log("[PlayFabManager] Auto-login success.");
                    StartCoroutine(ParseAndCachePlayerDataDelayed(result.InfoResultPayload, true));
                },
                error =>
                {
                    Debug.Log($"[PlayFabManager] Auto-login failed: {error.ErrorMessage}");
                    EventManager.FireAutoLoginChecked(false);
                });
        }
        else
        {
            EventManager.FireAutoLoginChecked(false);
        }
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    private void HandleLogin(string email, string password)
    {
        PlayFabClientAPI.LoginWithEmailAddress(
            new LoginWithEmailAddressRequest
            {
                Email = email,
                Password = password,
                InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                {
                    GetPlayerProfile = true,
                    GetUserVirtualCurrency = true,
                    GetUserData = true
                }
            },
            result =>
            {
                Debug.Log("[PlayFabManager] Login success.");
                StartCoroutine(ParseAndCachePlayerDataDelayed(result.InfoResultPayload, false));
            },
            error =>
            {
                if (error.Error == PlayFabErrorCode.AccountNotFound ||
                    error.Error == PlayFabErrorCode.InvalidEmailOrPassword)
                {
                    EventManager.FireAuthConflict(triedLogin: true);
                }
                else
                {
                    Debug.LogError($"[PlayFabManager] Login error: {error.GenerateErrorReport()}");
                }
            });
    }

    // ── Register & Login ──────────────────────────────────────────────────────

    private void HandleRegisterAndLogin(string email, string password)
    {
        PlayFabClientAPI.RegisterPlayFabUser(
            new RegisterPlayFabUserRequest
            {
                Email = email,
                Password = password,
                RequireBothUsernameAndEmail = false
            },
            _ =>
            {
                Debug.Log("[PlayFabManager] Registration success. Logging in...");
                LoginAfterRegister(email, password);
            },
            error =>
            {
                if (error.Error == PlayFabErrorCode.EmailAddressNotAvailable)
                {
                    EventManager.FireAuthConflict(triedLogin: false);
                }
                else
                {
                    Debug.LogError($"[PlayFabManager] Register error: {error.GenerateErrorReport()}");
                }
            });
    }

    private void LoginAfterRegister(string email, string password)
    {
        PlayFabClientAPI.LoginWithEmailAddress(
            new LoginWithEmailAddressRequest
            {
                Email = email,
                Password = password,
                InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                {
                    GetPlayerProfile = true,
                    GetUserVirtualCurrency = true,
                    GetUserData = true
                }
            },
            result =>
            {
                Debug.Log("[PlayFabManager] Post-register login success.");
                StartCoroutine(ParseAndCachePlayerDataDelayed(result.InfoResultPayload, false));
            },
            error => { Debug.LogError($"[PlayFabManager] Post-register login error: {error.GenerateErrorReport()}"); });
    }

    // ── Guest Login ───────────────────────────────────────────────────────────

    private void HandleGuestLogin()
    {
        if (!PlayerPrefs.HasKey(GuestIdKey))
            PlayerPrefs.SetString(GuestIdKey, System.Guid.NewGuid().ToString());

        PlayFabClientAPI.LoginWithCustomID(
            new LoginWithCustomIDRequest
            {
                CustomId = PlayerPrefs.GetString(GuestIdKey),
                CreateAccount = true,
                InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                {
                    GetPlayerProfile = true,
                    GetUserVirtualCurrency = true,
                    GetUserData = true
                }
            },
            result =>
            {
                Debug.Log("[PlayFabManager] Guest login success.");
                StartCoroutine(ParseAndCachePlayerDataDelayed(result.InfoResultPayload, false));
            },
            error => { Debug.LogError($"[PlayFabManager] Guest login error: {error.GenerateErrorReport()}"); });
    }

    // ── Parse PlayFab Data ────────────────────────────────────────────────────

    private IEnumerator ParseAndCachePlayerDataDelayed(GetPlayerCombinedInfoResultPayload payload, bool isAutoLogin)
    {
        yield return null;

        string playfabId = payload?.AccountInfo?.PlayFabId ?? "";
        string displayName = payload?.PlayerProfile?.DisplayName;

        int coins = 0;
        if (payload?.UserVirtualCurrency != null &&
            payload.UserVirtualCurrency.ContainsKey("CO"))
        {
            coins = payload.UserVirtualCurrency["CO"];
        }

        PlayerStatsData statsData = new PlayerStatsData();

        if (payload?.UserData != null &&
            payload.UserData.ContainsKey("PlayerStats"))
        {
            try
            {
                string json = payload.UserData["PlayerStats"].Value;
                statsData = JsonUtility.FromJson<PlayerStatsData>(json);
            }
            catch
            {
                Debug.LogWarning("[PlayFabManager] Failed to parse PlayerStats.");
            }
        }
        else
        {
            Debug.Log("[PlayFabManager] No PlayerStats found. Saving defaults.");
            SavePlayerStatsToPlayFab();
        }

        if (string.IsNullOrEmpty(displayName))
        {
            bool isGuest = PlayerPrefs.HasKey(GuestIdKey);
            displayName = PlayerDataManager.GenerateRandomUsername(isGuest);
        }

        PlayerDataManager.Initialize(playfabId, displayName, coins, statsData);

        if (isAutoLogin)
        {
            EventManager.FireAutoLoginChecked(true);
        }

        EventManager.FirePlayerDataLoaded();
    }

    // ── Update Profile ────────────────────────────────────────────────────────

    private void HandleUpdateProfile(string displayName, int avatarIndex)
    {
        PlayFabClientAPI.UpdateUserTitleDisplayName(
            new UpdateUserTitleDisplayNameRequest { DisplayName = displayName },
            _ => Debug.Log($"[PlayFabManager] DisplayName updated: {displayName}"),
            error => Debug.LogError($"[PlayFabManager] UpdateDisplayName error: {error.GenerateErrorReport()}"));

        PlayerDataManager.UpdateProfile(displayName, avatarIndex);
        SavePlayerStatsToPlayFab();
    }

    // ── Award Trophy Reward ───────────────────────────────────────────────────

    private void HandleAwardTrophyReward()
    {
        int currentMilestone = PlayerDataManager.GetCurrentMilestone();

        PlayFabClientAPI.AddUserVirtualCurrency(
            new AddUserVirtualCurrencyRequest
            {
                VirtualCurrency = "CO",
                Amount = 10
            },
            result =>
            {
                PlayerDataManager.UpdateCoins(result.Balance);
                Debug.Log(
                    $"[PlayFabManager] Trophy reward granted for milestone {currentMilestone}. New balance: {result.Balance}");

                PlayerDataManager.UpdateLastRewardedMilestone(currentMilestone);
                SavePlayerStatsToPlayFab();

                var homePage = UnityEngine.Object.FindObjectOfType<HomePageView>();
                if (homePage != null)
                    homePage.RefreshUI();
            },
            error => { Debug.LogError($"[PlayFabManager] AwardTrophyReward error: {error.GenerateErrorReport()}"); });
    }

    // ── Deduct Coins ──────────────────────────────────────────────────────────

    private void HandleDeductCoins(int amount)
    {
        PlayFabClientAPI.SubtractUserVirtualCurrency(
            new SubtractUserVirtualCurrencyRequest
            {
                VirtualCurrency = "CO",
                Amount = amount
            },
            result =>
            {
                Debug.Log($"[PlayFabManager] Deducted {amount} coins. New balance: {result.Balance}");
                PlayerDataManager.UpdateCoins(result.Balance);
            },
            error =>
            {
                Debug.LogError($"[PlayFabManager] DeductCoins error: {error.GenerateErrorReport()}");
                PlayerDataManager.AddCoins(amount);
            });
    }

    // ── Fetch Game Modes ──────────────────────────────────────────────────────

    private void HandleFetchGameModes()
    {
        PlayFabClientAPI.GetTitleData(
            new GetTitleDataRequest
            {
                Keys = new List<string> { "GameModeConfig" }
            },
            result =>
            {
                if (result.Data.ContainsKey("GameModeConfig"))
                {
                    string json = result.Data["GameModeConfig"];

                    try
                    {
                        GameModeConfig config = JsonUtility.FromJson<GameModeConfig>(json);
                        GameModeManager.Initialize(config.entryFees);
                        EventManager.FireGameModesFetched();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[PlayFabManager] Failed to parse GameModeConfig: {e.Message}");
                        GameModeManager.Initialize(new List<int> { 120, 300, 600 });
                        EventManager.FireGameModesFetched();
                    }
                }
                else
                {
                    Debug.LogWarning("[PlayFabManager] GameModeConfig not found. Using defaults.");
                    GameModeManager.Initialize(new List<int> { 120, 300, 600 });
                    EventManager.FireGameModesFetched();
                }
            },
            error =>
            {
                Debug.LogError($"[PlayFabManager] FetchGameModes error: {error.GenerateErrorReport()}");
                GameModeManager.Initialize(new List<int> { 120, 300, 600 });
                EventManager.FireGameModesFetched();
            });
    }

    // ── Username Validation ───────────────────────────────────────────────────

    private void HandleCheckUsernameAvailability(string username, System.Action<bool> callback)
    {
        PlayFabClientAPI.GetAccountInfo(
            new GetAccountInfoRequest
            {
                TitleDisplayName = username
            },
            result => { callback?.Invoke(false); },
            error =>
            {
                if (error.Error == PlayFabErrorCode.AccountNotFound)
                {
                    callback?.Invoke(true);
                }
                else
                {
                    Debug.LogError($"[PlayFabManager] CheckUsername error: {error.GenerateErrorReport()}");
                    callback?.Invoke(false);
                }
            });
    }

    // ── Fetch Friends ─────────────────────────────────────────────────────────

    private void HandleFetchFriends()
    {
        PlayFabClientAPI.GetFriendsList(
            new GetFriendsListRequest
            {
                ProfileConstraints = new PlayerProfileViewConstraints
                {
                    ShowDisplayName = true
                }
            },
            result => { StartCoroutine(FetchFriendDataCoroutine(result.Friends)); },
            error =>
            {
                Debug.LogError($"[PlayFabManager] GetFriendsList error: {error.GenerateErrorReport()}");
                FriendsManager.Initialize(new List<FriendData>());
                EventManager.FireFriendsFetched();
            });
    }

    private IEnumerator FetchFriendDataCoroutine(List<FriendInfo> friendInfos)
    {
        List<FriendData> friends = new List<FriendData>();

        foreach (var friendInfo in friendInfos)
        {
            bool dataFetched = false;
            FriendData friendData = null;

            PlayFabClientAPI.GetUserData(
                new GetUserDataRequest
                {
                    PlayFabId = friendInfo.FriendPlayFabId,
                    Keys = new List<string> { "PlayerStats" }
                },
                result =>
                {
                    PlayerStatsData stats = new PlayerStatsData();

                    if (result.Data != null && result.Data.ContainsKey("PlayerStats"))
                    {
                        try
                        {
                            stats = JsonUtility.FromJson<PlayerStatsData>(result.Data["PlayerStats"].Value);
                        }
                        catch
                        {
                        }
                    }

                    friendData = new FriendData(
                        friendInfo.FriendPlayFabId,
                        friendInfo.Profile?.DisplayName ?? "Unknown",
                        stats.trophies,
                        stats.avatarIndex
                    );

                    dataFetched = true;
                },
                error =>
                {
                    friendData = new FriendData(
                        friendInfo.FriendPlayFabId,
                        friendInfo.Profile?.DisplayName ?? "Unknown",
                        0,
                        0
                    );
                    dataFetched = true;
                });

            while (!dataFetched)
                yield return null;

            if (friendData != null)
                friends.Add(friendData);
        }

        FriendsManager.Initialize(friends);
        EventManager.FireFriendsFetched();
    }

    // ── Add Friend ────────────────────────────────────────────────────────────

    private void HandleAddFriend(string username)
    {
        // 1) Resolve username -> PlayFabId
        PlayFabClientAPI.GetAccountInfo(
            new GetAccountInfoRequest { TitleDisplayName = username },
            acc =>
            {
                var friendId = acc.AccountInfo?.PlayFabId;
                if (string.IsNullOrEmpty(friendId))
                {
                    EventManager.FireAddFriendFailed($"Username '{username}' not found.");
                    return;
                }

                // 2) Add friend by PlayFabId (more reliable)
                PlayFabClientAPI.AddFriend(
                    new AddFriendRequest { FriendPlayFabId = friendId },
                    _ =>
                    {
                        // 3) Fetch and cache that single friend + notify UI
                        FetchSingleFriendData(friendId); // this already does FireFriendAdded(friendData)

                        // Optional: refresh full list in background
                        // HandleFetchFriends();
                    },
                    error =>
                    {
                        if (error.Error == PlayFabErrorCode.UsersAlreadyFriends)
                            EventManager.FireAddFriendFailed("Already friends with this user.");
                        else
                            EventManager.FireAddFriendFailed($"Error: {error.ErrorMessage}");
                    }
                );
            },
            error =>
            {
                if (error.Error == PlayFabErrorCode.AccountNotFound)
                    EventManager.FireAddFriendFailed($"Username '{username}' not found.");
                else
                    EventManager.FireAddFriendFailed($"Error: {error.ErrorMessage}");
            }
        );
    }

    private IEnumerator DelayedFriendRefresh(string username)
    {
        // Wait a moment for PlayFab to sync
        yield return new WaitForSeconds(0.5f);

        // Fetch updated friends list
        HandleFetchFriends();
    }

    private void RefreshFriendsListAfterAdd()
    {
        // Fetch friends list again to get the new friend's data
        HandleFetchFriends();
    }

    private void FetchSingleFriendData(string playfabId)
    {
        PlayFabClientAPI.GetUserData(
            new GetUserDataRequest
            {
                PlayFabId = playfabId,
                Keys = new List<string> { "PlayerStats" }
            },
            result =>
            {
                PlayerStatsData stats = new PlayerStatsData();

                if (result.Data != null && result.Data.ContainsKey("PlayerStats"))
                {
                    try
                    {
                        stats = JsonUtility.FromJson<PlayerStatsData>(result.Data["PlayerStats"].Value);
                    }
                    catch
                    {
                    }
                }

                PlayFabClientAPI.GetAccountInfo(
                    new GetAccountInfoRequest { PlayFabId = playfabId },
                    accountResult =>
                    {
                        FriendData friendData = new FriendData(
                            playfabId,
                            accountResult.AccountInfo.TitleInfo?.DisplayName ?? "Unknown",
                            stats.trophies,
                            stats.avatarIndex
                        );

                        FriendsManager.AddFriend(friendData);
                        EventManager.FireFriendAdded(friendData);
                    },
                    error => Debug.LogError(error.GenerateErrorReport())
                );
            },
            error => Debug.LogError(error.GenerateErrorReport())
        );
    }

    // ── Remove Friend ─────────────────────────────────────────────────────────

    private void HandleRemoveFriend(string playfabId)
    {
        PlayFabClientAPI.RemoveFriend(
            new RemoveFriendRequest { FriendPlayFabId = playfabId },
            result =>
            {
                FriendsManager.RemoveFriend(playfabId);
                EventManager.FireFriendRemoved();
                Debug.Log($"[PlayFabManager] Friend removed: {playfabId}");
            },
            error => { Debug.LogError($"[PlayFabManager] RemoveFriend error: {error.GenerateErrorReport()}"); });
    }

    // ── Helper: Save Player Stats ─────────────────────────────────────────────

    private void SavePlayerStatsToPlayFab()
    {
        var statsData = new PlayerStatsData
        {
            trophies = PlayerDataManager.Trophies,
            avatarIndex = PlayerDataManager.AvatarIndex,
            hasSetupProfile = PlayerDataManager.HasSetupProfile,
            lastRewardedMilestone = PlayerDataManager.LastRewardedMilestone
        };

        PlayFabClientAPI.UpdateUserData(
            new UpdateUserDataRequest
            {
                Data = new Dictionary<string, string>
                {
                    { "PlayerStats", JsonUtility.ToJson(statsData) }
                },
                Permission = UserDataPermission.Public
            },
            _ => Debug.Log("[PlayFabManager] PlayerStats saved."),
            error => Debug.LogError($"[PlayFabManager] SavePlayerStats error: {error.GenerateErrorReport()}"));
    }
}