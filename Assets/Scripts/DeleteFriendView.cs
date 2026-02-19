using UnityEngine;
using UnityEngine.UI;
using UI;

/// <summary>
/// Confirmation dialogue for removing a friend.
/// Shows as dialogue over FriendsView.
/// </summary>
public class DeleteFriendView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Button deleteButton;

    [SerializeField] private Button cancelButton;
    [SerializeField] private Text confirmationText; // Optional: "Are you sure you want to remove your friend?"

    private string _pendingDeleteFriendId;
    private string _pendingDeleteFriendName;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (deleteButton != null)
            deleteButton.onClick.AddListener(OnDeleteClicked);

        if (cancelButton != null)
            cancelButton.onClick.AddListener(OnCancelClicked);
    }

    private void OnEnable()
    {
        EventManager.OnFriendRemoved += HandleFriendRemoved;

        // Load pending delete info
        _pendingDeleteFriendId = PlayerPrefs.GetString("PendingDeleteFriendId", "");
        _pendingDeleteFriendName = PlayerPrefs.GetString("PendingDeleteFriendName", "");

        // Update confirmation text if available
        if (confirmationText != null && !string.IsNullOrEmpty(_pendingDeleteFriendName))
        {
            confirmationText.text = $"Are you sure you want to remove {_pendingDeleteFriendName}?";
        }
    }

    private void OnDisable()
    {
        EventManager.OnFriendRemoved -= HandleFriendRemoved;

        // Clear pending delete data
        PlayerPrefs.DeleteKey("PendingDeleteFriendId");
        PlayerPrefs.DeleteKey("PendingDeleteFriendName");
    }

    private void OnDestroy()
    {
        if (deleteButton != null) deleteButton.onClick.RemoveAllListeners();
        if (cancelButton != null) cancelButton.onClick.RemoveAllListeners();
    }

    // ── Button Handlers ───────────────────────────────────────────────────────

    private void OnDeleteClicked()
    {
        if (string.IsNullOrEmpty(_pendingDeleteFriendId))
        {
            Debug.LogWarning("[DeleteFriendView] No friend ID to delete.");
            EventManager.FireHideView(ViewType.DeleteFriend);
            return;
        }

        EventManager.FireRemoveFriendRequested(_pendingDeleteFriendId);
    }

    private void OnCancelClicked()
    {
        EventManager.FireHideView(ViewType.DeleteFriend);
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void HandleFriendRemoved()
    {
        // Show success message
        EventManager.FireShowPopUp("Friend removed successfully.");
        EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);

        // Close this dialogue
        EventManager.FireHideView(ViewType.DeleteFriend);
    }
}