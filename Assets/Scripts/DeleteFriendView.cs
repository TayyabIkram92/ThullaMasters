using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Confirm-delete friend dialogue. Shown on top of FriendsView.
/// FriendsView never hidden.
/// </summary>
public class DeleteFriendView : MonoBehaviour
{
    [SerializeField] private Text friendNameTxt;
    [SerializeField] private Button confirmButton;
    [SerializeField] private Button cancelButton;

    private string _pendingFriendId;

    private void OnEnable()
    {
        confirmButton.onClick.AddListener(OnConfirmClicked);
        cancelButton.onClick.AddListener(OnCancelClicked);

        // Subscribe BEFORE reading prefs so we catch the event
        EventManager.OnFriendRemoved += HandleFriendRemoved;

        _pendingFriendId = PlayerPrefs.GetString("PendingDeleteFriendId", "");
        string friendName = PlayerPrefs.GetString("PendingDeleteFriendName", "this friend");
        friendNameTxt.text = "Are you sure you want to remove \n" + friendName + "?";

        confirmButton.interactable = !string.IsNullOrEmpty(_pendingFriendId);
    }

    private void OnDisable()
    {
        confirmButton.onClick.RemoveListener(OnConfirmClicked);
        cancelButton.onClick.RemoveListener(OnCancelClicked);

        EventManager.OnFriendRemoved -= HandleFriendRemoved;
    }

    private void OnConfirmClicked()
    {
        if (string.IsNullOrEmpty(_pendingFriendId)) return;
        confirmButton.interactable = false;
        EventManager.FireRemoveFriendRequested(_pendingFriendId);
    }

    private void HandleFriendRemoved(string playFabId)
    {
        PlayerPrefs.DeleteKey("PendingDeleteFriendId");
        PlayerPrefs.DeleteKey("PendingDeleteFriendName");
        PlayerPrefs.Save();
        EventManager.FireHideView(ViewType.DeleteFriend);
    }

    private void OnCancelClicked()
    {
        EventManager.FireHideView(ViewType.DeleteFriend);
    }
}
