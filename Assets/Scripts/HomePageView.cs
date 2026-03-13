using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class HomePageView : MonoBehaviour
{
    public const string ModeSelectKey = "HomePageModeSelect";
    public const string ModeClassic = "Classic";
    public const string ModeFriends = "Friends";

    [Header("Coins")] [SerializeField] private Text coinsTxt;

    [Header("Profile")] [SerializeField] private Text displayNameTxt;
    [SerializeField] private Text trophiesTxt;
    [SerializeField] private Image avatarImage;
    [SerializeField] private Sprite[] avatarSprites;

    [Header("Trophy Bar")] [SerializeField]
    private Slider trophyProgressBar;

    [Header("Buttons")] [SerializeField] private Button classicModeButton;
    [SerializeField] private Button playWithFriendsButton;
    [SerializeField] private Button friendsButton;
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button tutorialButton;
    [SerializeField] private Button buyCoinsButton;
    [SerializeField] private Button sellCoinsButton;
    [SerializeField] private Button profileButton;
    [SerializeField] private Button addButton;

    private bool _pendingGameSelection = false;

    private void OnEnable()
    {
        classicModeButton.onClick.AddListener(OnClassicModeClicked);
        playWithFriendsButton.onClick.AddListener(OnPlayWithFriendsClicked);
        friendsButton.onClick.AddListener(OnFriendsClicked);
        settingsButton.onClick.AddListener(OnSettingsClicked);
        tutorialButton.onClick.AddListener(OnTutorialClicked);
        profileButton.onClick.AddListener(ProfileButtonClicked);
        addButton.onClick.AddListener(AddButtonClicked);
        if (buyCoinsButton) buyCoinsButton.onClick.AddListener(OnBuyCoinsClicked);
        if (sellCoinsButton) sellCoinsButton.onClick.AddListener(OnSellCoinsClicked);

        EventManager.OnCoinsUpdated += HandleCoinsUpdated;
        EventManager.OnGameModesFetched += HandleGameModesFetched;

        RefreshUI();
        CheckAndAwardTrophyReward();
        CheckFirstTimeProfile();
    }

    private void OnDisable()
    {
        classicModeButton.onClick.RemoveListener(OnClassicModeClicked);
        playWithFriendsButton.onClick.RemoveListener(OnPlayWithFriendsClicked);
        friendsButton.onClick.RemoveListener(OnFriendsClicked);
        settingsButton.onClick.RemoveListener(OnSettingsClicked);
        tutorialButton.onClick.RemoveListener(OnTutorialClicked);
        profileButton.onClick.AddListener(ProfileButtonClicked);
        addButton.onClick.AddListener(AddButtonClicked);
        if (buyCoinsButton) buyCoinsButton.onClick.RemoveListener(OnBuyCoinsClicked);
        if (sellCoinsButton) sellCoinsButton.onClick.RemoveListener(OnSellCoinsClicked);

        EventManager.OnCoinsUpdated -= HandleCoinsUpdated;
        EventManager.OnGameModesFetched -= HandleGameModesFetched;

        _pendingGameSelection = false;
    }

    private void AddButtonClicked()
    {
        EventManager.FireShowView(ViewType.Buy, true);
    }

    private void ProfileButtonClicked()
    {
        EventManager.FireShowView(ViewType.Profile, true);
    }

    public void RefreshUI()
    {
        if (!PlayerDataManager.IsInitialized) return;

        displayNameTxt.text = PlayerDataManager.DisplayName;
        trophiesTxt.text = PlayerDataManager.Trophies.ToString();

        if (avatarImage != null && avatarSprites != null &&
            PlayerDataManager.AvatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[PlayerDataManager.AvatarIndex];

        if (trophyProgressBar != null)
            trophyProgressBar.value = PlayerDataManager.GetTrophyProgress() / 20f;
    }

    private void HandleCoinsUpdated(int newAmount)
    {
        coinsTxt.text = newAmount.ToString();
    }

    private void HandleGameModesFetched()
    {
        if (_pendingGameSelection)
        {
            _pendingGameSelection = false;
            EventManager.FireShowView(ViewType.GameSelection);
        }
    }

    private void CheckAndAwardTrophyReward()
    {
        if (PlayerDataManager.HasUnrewardedMilestone())
            EventManager.FireAwardTrophyRewardRequested();
    }

    private void CheckFirstTimeProfile()
    {
        if (!PlayerDataManager.HasSetupProfile)
            EventManager.FireShowView(ViewType.Profile, true);
    }

    private void OnClassicModeClicked()
    {
        PlayerPrefs.SetString(ModeSelectKey, ModeClassic);
        PlayerPrefs.Save();
        NavigateToGameSelection();
    }

    private void OnPlayWithFriendsClicked()
    {
        PlayerPrefs.SetString(ModeSelectKey, ModeFriends);
        PlayerPrefs.Save();
        NavigateToGameSelection();
    }

    private void NavigateToGameSelection()
    {
        if (!GameModeManager.IsInitialized)
        {
            _pendingGameSelection = true;
            EventManager.FireFetchGameModesRequested();
            return;
        }

        EventManager.FireShowView(ViewType.GameSelection);
    }

    private void OnFriendsClicked()
    {
        if (!FriendsManager.IsInitialized)
            EventManager.FireFetchFriendsRequested();
        EventManager.FireShowView(ViewType.Friends, true);
    }

    private void OnSettingsClicked()
    {
        EventManager.FireShowView(ViewType.Settings, true);
    }

    private void OnTutorialClicked()
    {
        EventManager.FireShowView(ViewType.Tutorial, true);
    }

    private void OnBuyCoinsClicked()
    {
        EventManager.FireShowView(ViewType.Buy, true);
    }

    private void OnSellCoinsClicked()
    {
        EventManager.FireShowView(ViewType.Sell, true);
    }
}