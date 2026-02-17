using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI;

public class ProfileView : MonoBehaviour
{
    [Header("Avatar Selection (Left) - Avatar(1) to Avatar(16)")] [SerializeField]
    private GameObject[] avatarContainers = new GameObject[16];

    [Header("Preview (Right)")] [SerializeField]
    private Image previewAvatarImage;

    [Header("Input")] [SerializeField] private TMP_InputField nameInputField;

    [Header("Cost (Shows only after first time)")] [SerializeField]
    private GameObject costBG;

    [SerializeField] private Text costText; // ← RENAMED: This is the "10" cost text, NOT player's coins

    [Header("Buttons")] [SerializeField] private Button confirmButton;
    [SerializeField] private Button closeButton;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private int _selectedAvatarIndex = 0;
    private bool _isFirstTime = false;
    private Image[] _avatarImages = new Image[16];

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (confirmButton != null)
            confirmButton.onClick.AddListener(OnConfirmClicked);
        if (closeButton != null)
            closeButton.onClick.AddListener(OnCloseClicked);

        // Wire up avatar buttons and cache their Image components
        for (int i = 0; i < avatarContainers.Length; i++)
        {
            if (avatarContainers[i] == null) continue;

            int index = i;

            // Add Button component if missing
            Button btn = avatarContainers[i].GetComponent<Button>();
            if (btn == null)
                btn = avatarContainers[i].AddComponent<Button>();

            btn.onClick.AddListener(() => SelectAvatar(index));

            // Cache the child "Avatar" Image component
            Transform avatarChild = avatarContainers[i].transform.Find("Avatar");
            if (avatarChild != null)
                _avatarImages[i] = avatarChild.GetComponent<Image>();
        }
    }

    private void OnEnable()
    {
        _isFirstTime = !PlayerDataManager.HasSetupProfile;

        // Show/hide cost
        if (costBG != null)
            costBG.SetActive(!_isFirstTime);

        // Load current data
        _selectedAvatarIndex = PlayerDataManager.AvatarIndex;

        if (nameInputField != null)
        {
            nameInputField.text = PlayerDataManager.DisplayName;
            nameInputField.characterLimit = 15;

            // Auto-generate if first time and empty
            if (_isFirstTime && string.IsNullOrEmpty(nameInputField.text))
            {
                bool isGuest = PlayerPrefs.HasKey("GuestCustomID");
                nameInputField.text = PlayerDataManager.GenerateRandomName(isGuest);
            }
        }

        // Update UI
        UpdateAvatarSprites();
        SelectAvatar(_selectedAvatarIndex);

        // The costText should always show "10" (it's the price, not player's balance)
        if (costText != null)
            costText.text = "10";
    }

    private void OnDestroy()
    {
        if (confirmButton != null) confirmButton.onClick.RemoveAllListeners();
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
    }

    // ── Avatar Display ────────────────────────────────────────────────────────

    private void UpdateAvatarSprites()
    {
        // Assign sprites to all avatar Image components
        for (int i = 0; i < _avatarImages.Length; i++)
        {
            if (_avatarImages[i] != null && i < avatarSprites.Length && avatarSprites[i] != null)
            {
                _avatarImages[i].sprite = avatarSprites[i];
            }
        }
    }

    // ── Avatar Selection ──────────────────────────────────────────────────────

    private void SelectAvatar(int index)
    {
        if (index < 0 || index >= avatarContainers.Length) return;

        _selectedAvatarIndex = index;

        // Turn off all "selected" children
        foreach (var container in avatarContainers)
        {
            if (container == null) continue;
            Transform selected = container.transform.Find("selected");
            if (selected != null)
                selected.gameObject.SetActive(false);
        }

        // Turn on selected avatar's "selected" child
        if (avatarContainers[index] != null)
        {
            Transform selectedChild = avatarContainers[index].transform.Find("selected");
            if (selectedChild != null)
                selectedChild.gameObject.SetActive(true);
        }

        // Update preview
        if (previewAvatarImage != null && index < avatarSprites.Length && avatarSprites[index] != null)
        {
            previewAvatarImage.sprite = avatarSprites[index];
        }
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────

    private void OnConfirmClicked()
    {
        string chosenName = nameInputField != null ? nameInputField.text.Trim() : "";

        // Validate name
        if (string.IsNullOrEmpty(chosenName))
        {
            Debug.LogWarning("[ProfileView] Name is empty. Using cached name.");
            chosenName = PlayerDataManager.DisplayName;
        }

        // Check cost (only if not first time)
        if (!_isFirstTime)
        {
            if (PlayerDataManager.Coins < 10)
            {
                Debug.LogWarning("[ProfileView] Not enough coins to update profile.");
                EventManager.FireShowPopUp("You need 10 coins to update your profile!");
                EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
                return;
            }

            // Deduct coins locally (optimistic update)
            PlayerDataManager.AddCoins(-10);

            // Request PlayFab to deduct coins
            EventManager.FireDeductCoinsRequested(10); // ← NEW
        }

        // Update cache
        PlayerDataManager.UpdateProfile(chosenName, _selectedAvatarIndex);

        // Send to PlayFab
        EventManager.FireUpdateProfileRequested(chosenName, _selectedAvatarIndex);

        // Refresh HomePage if active
        HomePageView homePage = FindObjectOfType<HomePageView>();
        if (homePage != null)
            homePage.RefreshUI();

        // Close
        EventManager.FireHideView(ViewType.Profile);
    }

    private void OnCloseClicked()
    {
        // If first time, save auto-generated values
        if (_isFirstTime)
        {
            string autoName = nameInputField != null ? nameInputField.text : PlayerDataManager.DisplayName;
            PlayerDataManager.UpdateProfile(autoName, _selectedAvatarIndex);
            EventManager.FireUpdateProfileRequested(autoName, _selectedAvatarIndex);
        }

        EventManager.FireHideView(ViewType.Profile);
    }
}