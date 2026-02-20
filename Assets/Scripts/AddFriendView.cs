using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI;

public class AddFriendView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private TMP_InputField usernameInputField;

    [SerializeField] private Button addButton;
    [SerializeField] private Button closeButton;

    private bool _isProcessing = false;

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

    private void OnAddClicked()
    {
        if (_isProcessing) return;

        string username = usernameInputField != null ? usernameInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(username))
        {
            ShowPopup("Please enter a username.");
            return;
        }

        if (username == PlayerDataManager.DisplayName)
        {
            ShowPopup("You cannot add yourself as a friend.");
            return;
        }

        _isProcessing = true;
        if (addButton != null)
            addButton.interactable = false;

        EventManager.FireAddFriendRequested(username);
        EventManager.FireHideView(ViewType.AddFriend);
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.AddFriend);
    }

    private void HandleFriendAdded(FriendData friend)
    {
        _isProcessing = false;
        if (addButton != null)
            addButton.interactable = true;

        EventManager.FireHideView(ViewType.AddFriend);
        ShowPopup("Friend added successfully!");
    }

    private void HandleAddFriendFailed(string reason)
    {
        _isProcessing = false;
        if (addButton != null)
            addButton.interactable = true;

        ShowPopup(reason);
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