using UnityEngine;
using PlayFab;
using PlayFab.ClientModels;

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
    }

    private void OnDisable()
    {
        EventManager.OnAutoLoginRequested -= HandleAutoLogin;
        EventManager.OnLoginRequested -= HandleLogin;
        EventManager.OnRegisterAndLoginRequested -= HandleRegisterAndLogin;
        EventManager.OnGuestLoginRequested -= HandleGuestLogin;
        EventManager.OnUpdateProfileRequested -= HandleUpdateProfile;
        EventManager.OnAwardTrophyRewardRequested -= HandleAwardTrophyReward;
    }
// ── NEW: Fetch Player Data ────────────────────────────────────────────────

    /// <summary>
    /// Called after any successful login/register.
    /// Fetches DisplayName, Coins, and PlayerStats in one call.
    /// </summary>
    private void FetchPlayerData()
    {
        PlayFabClientAPI.GetPlayerCombinedInfo(
            new GetPlayerCombinedInfoRequest
            {
                InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
                {
                    GetPlayerProfile = true,
                    GetUserVirtualCurrency = true,
                    GetUserData = true
                }
            },
            result =>
            {
                string playfabId = result.InfoResultPayload.AccountInfo.PlayFabId;
                string displayName = result.InfoResultPayload.PlayerProfile?.DisplayName ?? "";
                int coins = result.InfoResultPayload.UserVirtualCurrency.ContainsKey("CO")
                    ? result.InfoResultPayload.UserVirtualCurrency["CO"]
                    : 0;

                // Parse PlayerStats JSON
                PlayerStatsData statsData = new PlayerStatsData();
                if (result.InfoResultPayload.UserData.ContainsKey("PlayerStats"))
                {
                    string json = result.InfoResultPayload.UserData["PlayerStats"].Value;
                    try
                    {
                        statsData = JsonUtility.FromJson<PlayerStatsData>(json);
                    }
                    catch
                    {
                        Debug.LogWarning("[PlayFabManager] Failed to parse PlayerStats. Using defaults.");
                    }
                }

                // If no display name exists, generate one
                if (string.IsNullOrEmpty(displayName))
                {
                    bool isGuest = PlayerPrefs.HasKey(GuestIdKey);
                    displayName = PlayerDataManager.GenerateRandomName(isGuest);
                }

                // Initialize the static cache
                PlayerDataManager.Initialize(playfabId, displayName, coins, statsData);
                EventManager.FirePlayerDataLoaded();
            },
            error => { Debug.LogError($"[PlayFabManager] FetchPlayerData error: {error.GenerateErrorReport()}"); });
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
                    CreateAccount = false
                },
                _ =>
                {
                    Debug.Log("[PlayFabManager] Auto-login success.");
                    EventManager.FireAutoLoginChecked(true);
                    EventManager.FireAutoLoginChecked(true);
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
                Password = password
            },
            _ =>
            {
                Debug.Log("[PlayFabManager] Login success.");
                FetchPlayerData();
                EventManager.FireAuthSuccess();
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
                Password = password
            },
            _ =>
            {
                Debug.Log("[PlayFabManager] Post-register login success.");
                FetchPlayerData();
                EventManager.FireAuthSuccess();
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
                CreateAccount = true
            },
            _ =>
            {
                Debug.Log("[PlayFabManager] Guest login success.");
                FetchPlayerData();
                EventManager.FireAuthSuccess();
            },
            error => { Debug.LogError($"[PlayFabManager] Guest login error: {error.GenerateErrorReport()}"); });
    }

    // ── NEW: Update Profile ───────────────────────────────────────────────────

    private void HandleUpdateProfile(string displayName, int avatarIndex)
    {
        // Update DisplayName
        PlayFabClientAPI.UpdateUserTitleDisplayName(
            new UpdateUserTitleDisplayNameRequest { DisplayName = displayName },
            _ => Debug.Log($"[PlayFabManager] DisplayName updated: {displayName}"),
            error => Debug.LogError($"[PlayFabManager] UpdateDisplayName error: {error.GenerateErrorReport()}"));

        // Update PlayerStats JSON
        var statsData = new PlayerStatsData
        {
            trophies = PlayerDataManager.Trophies,
            avatarIndex = avatarIndex,
            hasSetupProfile = true
        };

        PlayFabClientAPI.UpdateUserData(
            new UpdateUserDataRequest
            {
                Data = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "PlayerStats", JsonUtility.ToJson(statsData) }
                }
            },
            _ => Debug.Log("[PlayFabManager] PlayerStats updated."),
            error => Debug.LogError($"[PlayFabManager] UpdateUserData error: {error.GenerateErrorReport()}"));
    }

// ── NEW: Award Trophy Reward ──────────────────────────────────────────────

    private void HandleAwardTrophyReward()
    {
        PlayFabClientAPI.AddUserVirtualCurrency(
            new AddUserVirtualCurrencyRequest
            {
                VirtualCurrency = "CO",
                Amount = 10
            },
            result =>
            {
                PlayerDataManager.AddCoins(10);
                Debug.Log($"[PlayFabManager] Trophy reward granted. New balance: {result.Balance}");

                // Update trophies to remove the milestone
                int newTrophies = PlayerDataManager.Trophies % 20;
                PlayerDataManager.UpdateTrophies(newTrophies);
                SaveTrophiesToPlayFab(newTrophies);
            },
            error => Debug.LogError($"[PlayFabManager] AwardTrophyReward error: {error.GenerateErrorReport()}"));
    }

    private void SaveTrophiesToPlayFab(int newTrophies)
    {
        var statsData = new PlayerStatsData
        {
            trophies = newTrophies,
            avatarIndex = PlayerDataManager.AvatarIndex,
            hasSetupProfile = PlayerDataManager.HasSetupProfile
        };

        PlayFabClientAPI.UpdateUserData(
            new UpdateUserDataRequest
            {
                Data = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "PlayerStats", JsonUtility.ToJson(statsData) }
                }
            },
            _ => Debug.Log("[PlayFabManager] Trophies saved after reward."),
            error => Debug.LogError($"[PlayFabManager] SaveTrophies error: {error.GenerateErrorReport()}"));
    }
}