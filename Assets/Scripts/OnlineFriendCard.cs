using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Friend card shown in OnlineFriendsView with an Invite button.
/// </summary>
public class OnlineFriendCard : MonoBehaviour
{
    [SerializeField] private Image avatarImage;
    [SerializeField] private Text displayNameTxt;
    [SerializeField] private Text friendNumberTxt;
    [SerializeField] private Button inviteButton;
    [SerializeField] private Sprite[] avatarSprites;

    private FriendData _friendData;
    private System.Action<FriendData> _onInviteClicked;

    private void OnEnable()
    {
        inviteButton.onClick.AddListener(OnInviteClicked);
    }

    private void OnDisable()
    {
        inviteButton.onClick.RemoveListener(OnInviteClicked);
        // No coroutine to worry about — cooldown removed since view closes on invite anyway
    }

    public void Setup(FriendData data, int number, System.Action<FriendData> onInvite)
    {
        _friendData = data;
        _onInviteClicked = onInvite;

        displayNameTxt.text = data.DisplayName;
        friendNumberTxt.text = number.ToString();

        // Always reset button state when card is pulled from pool
        inviteButton.interactable = true;

        if (avatarImage != null && avatarSprites != null &&
            data.AvatarIndex >= 0 && data.AvatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[data.AvatarIndex];
    }

    private void OnInviteClicked()
    {
        inviteButton.interactable = false;
        _onInviteClicked?.Invoke(_friendData);
        // No cooldown coroutine — OnlineFriendsView closes immediately after invite
    }
}