using UnityEngine;
using UnityEngine.UI;
using UI;

public class HomePageView : MonoBehaviour
{
    [Header("Profile Section")] [SerializeField]
    private Button profileButton;

    [SerializeField] private Image profileAvatarImage;
    [SerializeField] private Text nameText;

    [Header("Trophy Section")] [SerializeField]
    private Slider trophySlider;

    [Header("Coins Section")] [SerializeField]
    private Text coinsText;

    [SerializeField] private Button plusButton;
    [SerializeField] private Button buyButton;
    [SerializeField] private Button sellButton;

    [Header("Game Mode Buttons")] [SerializeField]
    private Button classicModeButton;

    [SerializeField] private Button playWithFriendsButton;
    [SerializeField] private Button tutorialButton;
    [SerializeField] private Button friendsButton;
    [SerializeField] private Button settingsButton;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private bool _hasCheckedTrophyReward = false;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        profileButton.onClick.AddListener(OnProfileClicked);
        plusButton.onClick.AddListener(() => Debug.Log("[HomePageView] Plus button clicked."));
        buyButton.onClick.AddListener(() => Debug.Log("[HomePageView] Buy button clicked."));
        sellButton.onClick.AddListener(() => Debug.Log("[HomePageView] Sell button clicked."));
        classicModeButton.onClick.AddListener(() =>
            Debug.Log("[HomePageView] Classic Mode clicked. Navigate to GameSelection."));
        playWithFriendsButton.onClick.AddListener(() =>
            Debug.Log("[HomePageView] Play With Friends clicked. Navigate to Rooms."));
        tutorialButton.onClick.AddListener(() => Debug.Log("[HomePageView] Tutorial clicked."));
        friendsButton.onClick.AddListener(() => Debug.Log("[HomePageView] Friends clicked."));
        settingsButton.onClick.AddListener(() => Debug.Log("[HomePageView] Settings clicked."));
    }

    private void OnEnable()
    {
        RefreshUI();
        CheckTrophyReward();
        CheckFirstTimeProfile();
    }

    private void OnDestroy()
    {
        profileButton.onClick.RemoveAllListeners();
        plusButton.onClick.RemoveAllListeners();
        buyButton.onClick.RemoveAllListeners();
        sellButton.onClick.RemoveAllListeners();
        classicModeButton.onClick.RemoveAllListeners();
        playWithFriendsButton.onClick.RemoveAllListeners();
        tutorialButton.onClick.RemoveAllListeners();
        friendsButton.onClick.RemoveAllListeners();
        settingsButton.onClick.RemoveAllListeners();
    }

    // ── UI Refresh ────────────────────────────────────────────────────────────

    public void RefreshUI()
    {
        // Name
        nameText.text = PlayerDataManager.DisplayName;

        // Avatar
        if (PlayerDataManager.AvatarIndex >= 0 && PlayerDataManager.AvatarIndex < avatarSprites.Length)
            profileAvatarImage.sprite = avatarSprites[PlayerDataManager.AvatarIndex];

        // Coins
        coinsText.text = PlayerDataManager.Coins.ToString();

        // Trophy slider (mod 20)
        int progress = PlayerDataManager.GetTrophyProgress();
        trophySlider.maxValue = 20;
        trophySlider.value = progress;
    }

    // ── Trophy Reward Logic ───────────────────────────────────────────────────

    private void CheckTrophyReward()
    {
        if (_hasCheckedTrophyReward) return;
        _hasCheckedTrophyReward = true;

        if (PlayerDataManager.Trophies >= 20)
        {
            Debug.Log($"[HomePageView] Awarding trophy reward. Current trophies: {PlayerDataManager.Trophies}");
            EventManager.FireAwardTrophyRewardRequested();

            // Refresh UI after a frame to let PlayFab callback update PlayerDataManager
            StartCoroutine(RefreshAfterFrame());
        }
    }

    private System.Collections.IEnumerator RefreshAfterFrame()
    {
        yield return null;
        RefreshUI();
    }

    // ── First-Time Profile ────────────────────────────────────────────────────

    private void CheckFirstTimeProfile()
    {
        if (!PlayerDataManager.HasSetupProfile)
        {
            Debug.Log("[HomePageView] First-time user. Showing ProfileView.");
            EventManager.FireShowView(ViewType.Profile, showAsDialogue: true);
        }
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────

    private void OnProfileClicked()
    {
        EventManager.FireShowView(ViewType.Profile, showAsDialogue: true);
    }
}