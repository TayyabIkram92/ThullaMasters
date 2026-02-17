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

    // ── Parse PlayFab Data (DELAYED to avoid race condition) ─────────────────

    private IEnumerator ParseAndCachePlayerDataDelayed(GetPlayerCombinedInfoResultPayload payload, bool isAutoLogin)
    {
        // Wait one frame to guarantee all OnEnable subscriptions are complete
        yield return null;

        // Extract PlayFabId
        string playfabId = payload?.AccountInfo?.PlayFabId ?? "";

        // Extract DisplayName
        string displayName = payload?.PlayerProfile?.DisplayName;

        // Extract Coins
        int coins = 0;
        if (payload?.UserVirtualCurrency != null &&
            payload.UserVirtualCurrency.ContainsKey("CO"))
        {
            coins = payload.UserVirtualCurrency["CO"];
        }

        // Extract PlayerStats
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
                Debug.LogWarning("[PlayFabManager] Failed to parse PlayerStats. Using defaults.");
            }
        }

        // Generate name if missing
        if (string.IsNullOrEmpty(displayName))
        {
            bool isGuest = PlayerPrefs.HasKey(GuestIdKey);
            displayName = PlayerDataManager.GenerateRandomName(isGuest);
            Debug.Log($"[PlayFabManager] No DisplayName found. Generated: {displayName}");
        }

        // Cache everything
        PlayerDataManager.Initialize(playfabId, displayName, coins, statsData);

        // Fire appropriate events
        if (isAutoLogin)
        {
            EventManager.FireAutoLoginChecked(true);
        }

        EventManager.FirePlayerDataLoaded();

        Debug.Log("[PlayFabManager] PlayerDataLoaded event fired.");
    }

    // ── Update Profile ────────────────────────────────────────────────────────

    private void HandleUpdateProfile(string displayName, int avatarIndex)
    {
        // Update DisplayName
        PlayFabClientAPI.UpdateUserTitleDisplayName(
            new UpdateUserTitleDisplayNameRequest { DisplayName = displayName },
            _ => Debug.Log($"[PlayFabManager] DisplayName updated: {displayName}"),
            error => Debug.LogError($"[PlayFabManager] UpdateDisplayName error: {error.GenerateErrorReport()}"));

        // Update PlayerStats
        PlayerDataManager.UpdateProfile(displayName, avatarIndex);
        SavePlayerStatsToPlayFab(); // ← Use shared helper
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
                // Update coins from PlayFab (validation)
                PlayerDataManager.UpdateCoins(result.Balance);
                Debug.Log(
                    $"[PlayFabManager] Trophy reward granted for milestone {currentMilestone}. New balance: {result.Balance}");

                // Mark this milestone as rewarded
                PlayerDataManager.UpdateLastRewardedMilestone(currentMilestone);

                // Save updated milestone to PlayFab
                SavePlayerStatsToPlayFab();

                // Refresh UI
                var homePage = UnityEngine.Object.FindObjectOfType<HomePageView>();
                if (homePage != null)
                    homePage.RefreshUI();
            },
            error => { Debug.LogError($"[PlayFabManager] AwardTrophyReward error: {error.GenerateErrorReport()}"); });
    }

    // ── Helper: Save All Player Stats ────────────────────────────────────────

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
                }
            },
            _ => Debug.Log("[PlayFabManager] PlayerStats saved."),
            error => Debug.LogError($"[PlayFabManager] SavePlayerStats error: {error.GenerateErrorReport()}"));
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
                // Update local cache to match PlayFab
                PlayerDataManager.UpdateCoins(result.Balance);
            },
            error =>
            {
                Debug.LogError($"[PlayFabManager] DeductCoins error: {error.GenerateErrorReport()}");
                // Rollback local deduction on error
                PlayerDataManager.AddCoins(amount);
            });
    }
}