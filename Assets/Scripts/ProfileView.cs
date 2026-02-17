using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI;

public class ProfileView : MonoBehaviour
{
    [Header("Avatar Selection (Left)")] [SerializeField]
    private GameObject[] avatarContainers = new GameObject[16]; // Avatar(1) to Avatar(16)

    [Header("Preview (Right)")] [SerializeField]
    private Image previewAvatarImage;

    [Header("Input")] [SerializeField] private TMP_InputField nameInputField;

    [Header("Cost")] [SerializeField] private GameObject costBG;
    [SerializeField] private Text coinsText;

    [Header("Buttons")] [SerializeField] private Button confirmButton;
    [SerializeField] private Button closeButton;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private int _selectedAvatarIndex = 0;
    private bool _isFirstTime = false;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        confirmButton.onClick.AddListener(OnConfirmClicked);
        closeButton.onClick.AddListener(OnCloseClicked);

        // Wire up avatar selection buttons
        for (int i = 0; i < avatarContainers.Length; i++)
        {
            int index = i; // Capture for closure
            var button = avatarContainers[i].GetComponent<Button>();
            if (button != null)
                button.onClick.AddListener(() => SelectAvatar(index));
        }
    }

    private void OnEnable()
    {
        _isFirstTime = !PlayerDataManager.HasSetupProfile;

        // Show/hide cost
        costBG.SetActive(!_isFirstTime);

        // Load current data
        _selectedAvatarIndex = PlayerDataManager.AvatarIndex;
        nameInputField.text = PlayerDataManager.DisplayName;

        // If first time and name is empty, auto-generate
        if (_isFirstTime && string.IsNullOrEmpty(nameInputField.text))
        {
            bool isGuest = PlayerPrefs.HasKey("GuestCustomID");
            nameInputField.text = PlayerDataManager.GenerateRandomName(isGuest);
        }

        nameInputField.characterLimit = 15;

        // Update UI
        SelectAvatar(_selectedAvatarIndex);
        coinsText.text = PlayerDataManager.Coins.ToString();
    }

    private void OnDestroy()
    {
        confirmButton.onClick.RemoveAllListeners();
        closeButton.onClick.RemoveAllListeners();
    }

    // ── Avatar Selection ──────────────────────────────────────────────────────

    private void SelectAvatar(int index)
    {
        if (index < 0 || index >= avatarContainers.Length) return;

        _selectedAvatarIndex = index;

        // Turn off all "Selected" children
        foreach (var container in avatarContainers)
        {
            var selected = container.transform.Find("selected");
            if (selected != null)
                selected.gameObject.SetActive(false);
        }

        // Turn on selected avatar's "Selected" child
        var selectedChild = avatarContainers[index].transform.Find("selected");
        if (selectedChild != null)
            selectedChild.gameObject.SetActive(true);

        // Update preview
        if (index < avatarSprites.Length)
            previewAvatarImage.sprite = avatarSprites[index];
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────

    private void OnConfirmClicked()
    {
        string chosenName = nameInputField.text.Trim();

        // Validate name
        if (string.IsNullOrEmpty(chosenName))
        {
            Debug.LogWarning("[ProfileView] Name is empty. Using current cached name.");
            chosenName = PlayerDataManager.DisplayName;
        }

        // Check cost (only if not first time)
        if (!_isFirstTime)
        {
            if (PlayerDataManager.Coins < 10)
            {
                Debug.LogWarning("[ProfileView] Not enough coins to update profile.");
                return;
            }

            // Deduct coins locally (PlayFab will be updated via a separate call if you want)
            PlayerDataManager.AddCoins(-10);
        }

        // Update local cache
        PlayerDataManager.UpdateProfile(chosenName, _selectedAvatarIndex);

        // Send to PlayFab
        EventManager.FireUpdateProfileRequested(chosenName, _selectedAvatarIndex);

        // Refresh HomePage if it's active
        var homePage = FindObjectOfType<HomePageView>();
        if (homePage != null)
            homePage.RefreshUI();

        // Close
        EventManager.FireHideView(ViewType.Profile);
    }

    private void OnCloseClicked()
    {
        // If first time and user closes, use auto-generated values
        if (_isFirstTime)
        {
            string autoName = nameInputField.text; // Already auto-filled in OnEnable
            PlayerDataManager.UpdateProfile(autoName, _selectedAvatarIndex);
            EventManager.FireUpdateProfileRequested(autoName, _selectedAvatarIndex);
        }

        EventManager.FireHideView(ViewType.Profile);
    }
}