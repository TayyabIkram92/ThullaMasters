using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shown when the installed app version is below the minimum required version
/// stored in Firestore (appConfig/version).  The view is intentionally
/// non-dismissible: the only action is opening the store URL.
/// </summary>
public class UpdateGameView : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Static payload — set by HomePageView before FireShowView
    // ─────────────────────────────────────────────────────────────

    /// <summary>Message from Firestore to display to the user.</summary>
    public static string PendingMessage    = "Please update to continue using app.";

    /// <summary>Android store / APK URL from Firestore.</summary>
    public static string PendingAndroidUrl = "";

    // ─────────────────────────────────────────────────────────────
    //  Inspector references
    // ─────────────────────────────────────────────────────────────

    [Header("UI")]
    [SerializeField] private Text messageTxt;   // Legacy Text (UnityEngine.UI.Text)
    [SerializeField] private Button updateButton;

    // ─────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        updateButton.onClick.AddListener(OnUpdateClicked);
        ApplyPayload();
    }

    private void OnDisable()
    {
        updateButton.onClick.RemoveListener(OnUpdateClicked);
    }

    // ─────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────

    private void ApplyPayload()
    {
        if (messageTxt != null)
            messageTxt.text = string.IsNullOrEmpty(PendingMessage)
                ? "A new version is available. Please update to continue."
                : PendingMessage;
    }

    private void OnUpdateClicked()
    {
        if (string.IsNullOrEmpty(PendingAndroidUrl))
        {
            Debug.LogWarning("[UpdateGameView] No Android URL set — cannot open store.");
            return;
        }

        Application.OpenURL(PendingAndroidUrl);
    }
}
