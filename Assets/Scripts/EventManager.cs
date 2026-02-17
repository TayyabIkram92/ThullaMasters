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
}