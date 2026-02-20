using UnityEngine;
using UnityEngine.UI;
using UI;

public class DeleteFriendView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Button deleteButton;

    [SerializeField] private Button cancelButton;
    [SerializeField] private Text confirmationText;

    private string _pendingDeleteFriendId;
    private string _pendingDeleteFriendName;

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

        _pendingDeleteFriendId = PlayerPrefs.GetString("PendingDeleteFriendId", "");
        _pendingDeleteFriendName = PlayerPrefs.GetString("PendingDeleteFriendName", "");

        if (confirmationText != null && !string.IsNullOrEmpty(_pendingDeleteFriendName))
        {
            confirmationText.text = $"Are you sure you want to remove {_pendingDeleteFriendName}?";
        }
    }

    private void OnDisable()
    {
        EventManager.OnFriendRemoved -= HandleFriendRemoved;

        PlayerPrefs.DeleteKey("PendingDeleteFriendId");
        PlayerPrefs.DeleteKey("PendingDeleteFriendName");
    }

    private void OnDestroy()
    {
        if (deleteButton != null) deleteButton.onClick.RemoveAllListeners();
        if (cancelButton != null) cancelButton.onClick.RemoveAllListeners();
    }

    private void OnDeleteClicked()
    {
        if (string.IsNullOrEmpty(_pendingDeleteFriendId))
        {
            EventManager.FireHideView(ViewType.DeleteFriend);
            return;
        }

        EventManager.FireHideView(ViewType.DeleteFriend);
        EventManager.FireRemoveFriendRequested(_pendingDeleteFriendId);
    }

    private void OnCancelClicked()
    {
        EventManager.FireHideView(ViewType.DeleteFriend);
    }

    private void HandleFriendRemoved()
    {
        EventManager.FireHideView(ViewType.DeleteFriend);
        ShowPopup("Friend removed successfully.");
    }

    private void ShowPopup(string message)
    {
        EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
        StartCoroutine(ShowMessageDelayed(message));
    }

    private System.Collections.IEnumerator ShowMessageDelayed(string message)
    {
        yield return null;
        EventManager.FireShowPopUp(message);
    }
}