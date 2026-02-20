using UI;
using UnityEngine;
using UnityEngine.UI;

public class FriendCard : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Image avatarImage;

    [SerializeField] private Text nameTxt;
    [SerializeField] private Text trophiesTxt;
    [SerializeField] private Button deleteBtn;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private FriendData _friendData;

    private void Awake()
    {
        if (deleteBtn != null)
            deleteBtn.onClick.AddListener(OnDeleteClicked);
    }

    private void OnDestroy()
    {
        if (deleteBtn != null)
            deleteBtn.onClick.RemoveAllListeners();
    }

    public void Initialize(FriendData friendData)
    {
        _friendData = friendData;

        if (nameTxt != null)
            nameTxt.text = friendData.DisplayName;

        if (trophiesTxt != null)
            trophiesTxt.text = friendData.Trophies.ToString();

        if (avatarImage != null &&
            friendData.AvatarIndex >= 0 &&
            friendData.AvatarIndex < avatarSprites.Length &&
            avatarSprites[friendData.AvatarIndex] != null)
        {
            avatarImage.sprite = avatarSprites[friendData.AvatarIndex];
        }
    }

    private void OnDeleteClicked()
    {
        PlayerPrefs.SetString("PendingDeleteFriendId", _friendData.PlayFabId);
        PlayerPrefs.SetString("PendingDeleteFriendName", _friendData.DisplayName);

        EventManager.FireShowView(ViewType.DeleteFriend, showAsDialogue: true);
    }
}