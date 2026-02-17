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

    [SerializeField] private Text trophyProgressText; // ← NEW: shows "15/20"

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

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (profileButton != null)
            profileButton.onClick.AddListener(OnProfileClicked);
        if (plusButton != null)
            plusButton.onClick.AddListener(() => Debug.Log("[HomePageView] Plus button clicked."));
        if (buyButton != null)
            buyButton.onClick.AddListener(() => Debug.Log("[HomePageView] Buy button clicked."));
        if (sellButton != null)
            sellButton.onClick.AddListener(() => Debug.Log("[HomePageView] Sell button clicked."));
        if (classicModeButton != null)
            classicModeButton.onClick.AddListener(() => Debug.Log("[HomePageView] Classic Mode clicked."));
        if (playWithFriendsButton != null)
            playWithFriendsButton.onClick.AddListener(() => Debug.Log("[HomePageView] Play With Friends clicked."));
        if (tutorialButton != null)
            tutorialButton.onClick.AddListener(() => Debug.Log("[HomePageView] Tutorial clicked."));
        if (friendsButton != null)
            friendsButton.onClick.AddListener(() => Debug.Log("[HomePageView] Friends clicked."));
        if (settingsButton != null)
            settingsButton.onClick.AddListener(() => Debug.Log("[HomePageView] Settings clicked."));
    }

    private void OnEnable()
    {
        RefreshUI();
        CheckAndAwardTrophyReward();
        CheckFirstTimeProfile();
    }

    private void OnDestroy()
    {
        if (profileButton != null) profileButton.onClick.RemoveAllListeners();
        if (plusButton != null) plusButton.onClick.RemoveAllListeners();
        if (buyButton != null) buyButton.onClick.RemoveAllListeners();
        if (sellButton != null) sellButton.onClick.RemoveAllListeners();
        if (classicModeButton != null) classicModeButton.onClick.RemoveAllListeners();
        if (playWithFriendsButton != null) playWithFriendsButton.onClick.RemoveAllListeners();
        if (tutorialButton != null) tutorialButton.onClick.RemoveAllListeners();
        if (friendsButton != null) friendsButton.onClick.RemoveAllListeners();
        if (settingsButton != null) settingsButton.onClick.RemoveAllListeners();
    }

    // ── UI Refresh ────────────────────────────────────────────────────────────

    public void RefreshUI()
    {
        // Name
        if (nameText != null)
            nameText.text = PlayerDataManager.DisplayName;

        // Avatar
        if (profileAvatarImage != null &&
            PlayerDataManager.AvatarIndex >= 0 &&
            PlayerDataManager.AvatarIndex < avatarSprites.Length &&
            avatarSprites[PlayerDataManager.AvatarIndex] != null)
        {
            profileAvatarImage.sprite = avatarSprites[PlayerDataManager.AvatarIndex];
        }

        // Coins
        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        // Trophy slider + progress text
        if (trophySlider != null)
        {
            int progress = PlayerDataManager.GetTrophyProgress();
            trophySlider.maxValue = 20;
            trophySlider.value = progress;
        }

        if (trophyProgressText != null)
        {
            int progress = PlayerDataManager.GetTrophyProgress();
            trophyProgressText.text = $"{progress}/20";
        }
    }

    // ── Trophy Reward Logic (FIXED) ───────────────────────────────────────────

    private void CheckAndAwardTrophyReward()
    {
        // Only award if there's an unrewarded milestone
        if (PlayerDataManager.HasUnrewardedMilestone())
        {
            int currentMilestone = PlayerDataManager.GetCurrentMilestone();
            Debug.Log(
                $"[HomePageView] Awarding reward for milestone {currentMilestone}. Trophies: {PlayerDataManager.Trophies}");

            EventManager.FireAwardTrophyRewardRequested();
        }
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