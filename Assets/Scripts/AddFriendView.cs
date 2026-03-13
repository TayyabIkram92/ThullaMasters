using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Add friend dialogue. Shown on top of FriendsView (as dialogue).
/// FriendsView is never hidden.
/// </summary>
public class AddFriendView : MonoBehaviour
{
    [SerializeField] private TMP_InputField usernameInput;
    [SerializeField] private Button addButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private Text statusTxt;

    private void OnEnable()
    {
        addButton.onClick.AddListener(OnAddClicked);
        closeButton.onClick.AddListener(OnCloseClicked);

        EventManager.OnFriendAdded += HandleFriendAdded;
        EventManager.OnAddFriendFailed += HandleAddFriendFailed;

        // Reset
        usernameInput.text = "";
        statusTxt.text = "";
        addButton.interactable = true;
    }

    private void OnDisable()
    {
        addButton.onClick.RemoveListener(OnAddClicked);
        closeButton.onClick.RemoveListener(OnCloseClicked);

        EventManager.OnFriendAdded -= HandleFriendAdded;
        EventManager.OnAddFriendFailed -= HandleAddFriendFailed;
    }

    private void OnAddClicked()
    {
        string username = usernameInput.text.Trim();
        if (string.IsNullOrEmpty(username))
        {
            statusTxt.text = "Please enter a username.";
            return;
        }

        addButton.interactable = false;
        statusTxt.text = "Searching...";
        EventManager.FireAddFriendRequested(username);
    }

    private void HandleFriendAdded(string playFabId)
    {
        // Close; FriendsView (still active) will refresh via its own OnFriendAdded handler
        EventManager.FireHideView(ViewType.AddFriend);
    }

    private void HandleAddFriendFailed(string message)
    {
        statusTxt.text = message;
        addButton.interactable = true;
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.AddFriend);
    }
}
