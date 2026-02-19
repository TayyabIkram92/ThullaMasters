using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI;

/// <summary>
/// Dialogue for adding friends by username.
/// Shows as dialogue over FriendsView.
/// </summary>
public class AddFriendView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private TMP_InputField usernameInputField;

    [SerializeField] private Button addButton;
    [SerializeField] private Button closeButton;

    private bool _isProcessing = false;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (addButton != null)
            addButton.onClick.AddListener(OnAddClicked);

        if (closeButton != null)
            closeButton.onClick.AddListener(OnCloseClicked);
    }

    private void OnEnable()
    {
        EventManager.OnFriendAdded += HandleFriendAdded;
        EventManager.OnAddFriendFailed += HandleAddFriendFailed;

        // Clear input field
        if (usernameInputField != null)
            usernameInputField.text = "";
    }

    private void OnDisable()
    {
        EventManager.OnFriendAdded -= HandleFriendAdded;
        EventManager.OnAddFriendFailed -= HandleAddFriendFailed;
    }

    private void OnDestroy()
    {
        if (addButton != null) addButton.onClick.RemoveAllListeners();
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
    }

    // ── Button Handlers ───────────────────────────────────────────────────────

    private void OnAddClicked()
    {
        if (_isProcessing) return;

        string username = usernameInputField != null ? usernameInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(username))
        {
            EventManager.FireShowPopUp("Please enter a username.");
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            return;
        }

        // Check if trying to add self
        if (username == PlayerDataManager.DisplayName)
        {
            EventManager.FireShowPopUp("You cannot add yourself as a friend.");
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            return;
        }

        _isProcessing = true;
        if (addButton != null)
            addButton.interactable = false;

        EventManager.FireAddFriendRequested(username);
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.AddFriend);
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void HandleFriendAdded(FriendData friend)
    {
        _isProcessing = false;
        if (addButton != null)
            addButton.interactable = true;

        // Show success message
        EventManager.FireShowPopUp($"Added {friend.DisplayName} as friend!");
        EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);

        // Close this dialogue
        EventManager.FireHideView(ViewType.AddFriend);
    }

    private void HandleAddFriendFailed(string reason)
    {
        _isProcessing = false;
        if (addButton != null)
            addButton.interactable = true;

        // Show error
        EventManager.FireShowPopUp(reason);
        EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
    }
}