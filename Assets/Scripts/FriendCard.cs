using UI;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a single friend's data in the friends list.
/// </summary>
public class FriendCard : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Image avatarImage;

    [SerializeField] private Text nameTxt;
    [SerializeField] private Text trophiesTxt;
    [SerializeField] private Text coinsTxt;
    [SerializeField] private Button deleteBtn;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private FriendData _friendData;

    // ── Unity ─────────────────────────────────────────────────────────────────

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

    // ── Initialization ────────────────────────────────────────────────────────

    public void Initialize(FriendData friendData)
    {
        _friendData = friendData;

        // Name
        if (nameTxt != null)
            nameTxt.text = friendData.DisplayName;

        // Trophies
        if (trophiesTxt != null)
            trophiesTxt.text = friendData.Trophies.ToString();

        // Coins
        if (coinsTxt != null)
            coinsTxt.text = friendData.Coins.ToString();

        // Avatar
        if (avatarImage != null &&
            friendData.AvatarIndex >= 0 &&
            friendData.AvatarIndex < avatarSprites.Length &&
            avatarSprites[friendData.AvatarIndex] != null)
        {
            avatarImage.sprite = avatarSprites[friendData.AvatarIndex];
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnDeleteClicked()
    {
        // Store friend data and show delete confirmation
        PlayerPrefs.SetString("PendingDeleteFriendId", _friendData.PlayFabId);
        PlayerPrefs.SetString("PendingDeleteFriendName", _friendData.DisplayName);

        EventManager.FireShowView(ViewType.DeleteFriend, showAsDialogue: true);
    }
}