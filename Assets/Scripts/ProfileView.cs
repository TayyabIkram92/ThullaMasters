using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ProfileView : MonoBehaviour
{
    [Header("Avatar Selection (Left) - Avatar(1) to Avatar(16)")] [SerializeField]
    private GameObject[] avatarContainers = new GameObject[16];

    [Header("Preview (Right)")] [SerializeField]
    private Image previewAvatarImage;

    [Header("Input")] [SerializeField] private TMP_InputField nameInputField;

    [Header("Cost")] [SerializeField] private GameObject costBG;
    [SerializeField] private Text costText;

    [Header("Buttons")] [SerializeField] private Button confirmButton;
    [SerializeField] private Button closeButton;

    [Header("Avatar Sprites (0-15)")] [SerializeField]
    private Sprite[] avatarSprites = new Sprite[16];

    private int _selectedAvatarIndex = 0;
    private bool _isFirstTime = false;
    private bool _isValidatingUsername = false;
    private Image[] _avatarImages = new Image[16];

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmClicked);
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);

        for (int i = 0; i < avatarContainers.Length; i++)
        {
            if (avatarContainers[i] == null) continue;
            int index = i;
            Button btn = avatarContainers[i].GetComponent<Button>();
            if (btn == null) btn = avatarContainers[i].AddComponent<Button>();
            btn.onClick.AddListener(() => SelectAvatar(index));

            Transform avatarChild = avatarContainers[i].transform.Find("Avatar");
            if (avatarChild != null)
                _avatarImages[i] = avatarChild.GetComponent<Image>();
        }
    }

    private void OnEnable()
    {
        _isFirstTime = !PlayerDataManager.HasSetupProfile;

        if (costBG != null) costBG.SetActive(!_isFirstTime);

        _selectedAvatarIndex = PlayerDataManager.AvatarIndex;

        if (nameInputField != null)
        {
            nameInputField.text = PlayerDataManager.DisplayName;
            nameInputField.characterLimit = 15;

            if (_isFirstTime && string.IsNullOrEmpty(nameInputField.text))
                GenerateUniqueUsername();
        }

        UpdateAvatarSprites();
        SelectAvatar(_selectedAvatarIndex);

        if (costText != null) costText.text = "10";
    }

    private void OnDestroy()
    {
        if (confirmButton != null) confirmButton.onClick.RemoveAllListeners();
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
    }

    // ── Username Generation ───────────────────────────────────────────────────

    private void GenerateUniqueUsername()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new System.Random();
        var suffix = new System.Text.StringBuilder(9);
        for (int i = 0; i < 9; i++)
            suffix.Append(chars[random.Next(chars.Length)]);
        string candidateName = "player" + suffix.ToString();

        // Bypass ValidateUsername to avoid early-return when candidateName == DisplayName
        EventManager.FireCheckUsernameAvailability(candidateName, isAvailable =>
        {
            if (isAvailable)
            {
                if (nameInputField != null)
                    nameInputField.text = candidateName;
            }
            else
            {
                GenerateUniqueUsername();
            }
        });
    }

    // ── Avatar Display ────────────────────────────────────────────────────────

    private void UpdateAvatarSprites()
    {
        for (int i = 0; i < _avatarImages.Length; i++)
            if (_avatarImages[i] != null && i < avatarSprites.Length && avatarSprites[i] != null)
                _avatarImages[i].sprite = avatarSprites[i];
    }

    private void SelectAvatar(int index)
    {
        if (index < 0 || index >= avatarContainers.Length) return;
        _selectedAvatarIndex = index;

        foreach (var container in avatarContainers)
        {
            if (container == null) continue;
            Transform selected = container.transform.Find("selected");
            if (selected != null) selected.gameObject.SetActive(false);
        }

        if (avatarContainers[index] != null)
        {
            Transform selectedChild = avatarContainers[index].transform.Find("selected");
            if (selectedChild != null) selectedChild.gameObject.SetActive(true);
        }

        if (previewAvatarImage != null && index < avatarSprites.Length && avatarSprites[index] != null)
            previewAvatarImage.sprite = avatarSprites[index];
    }

    // ── Username Validation ───────────────────────────────────────────────────

    private void ValidateUsername(string username, System.Action<bool> callback)
    {
        if (username == PlayerDataManager.DisplayName)
        {
            callback?.Invoke(true);
            return;
        }

        // FireCheckUsernameAvailability(string, Action<bool>) — 2 params
        EventManager.FireCheckUsernameAvailability(username, callback);
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────

    private void OnConfirmClicked()
    {
        if (_isValidatingUsername) return;

        string chosenName = nameInputField != null ? nameInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(chosenName))
        {
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            EventManager.FireShowPopUp("Please enter a username.");
            return;
        }

        if (chosenName.Length < 3)
        {
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            EventManager.FireShowPopUp("Username must be at least 3 characters.");
            return;
        }

        if (!_isFirstTime)
        {
            EventManager.FireGetCoinsRequested(coins =>
            {
                if (coins < 10)
                {
                    EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
                    EventManager.FireShowPopUp("You need 10 coins to update your profile.");
                    return;
                }

                ProceedToValidate(chosenName);
            });
            return;
        }

        ProceedToValidate(chosenName);
    }

    private void ProceedToValidate(string chosenName)
    {
        _isValidatingUsername = true;
        if (confirmButton != null) confirmButton.interactable = false;

        ValidateUsername(chosenName, isAvailable =>
        {
            _isValidatingUsername = false;
            if (confirmButton != null) confirmButton.interactable = true;

            if (!isAvailable)
            {
                EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
                EventManager.FireShowPopUp($"Username '{chosenName}' is already taken.\nPlease try another one.");
                return;
            }

            ConfirmProfileUpdate(chosenName);
        });
    }

    private void ConfirmProfileUpdate(string username)
    {
        if (!_isFirstTime)
        {
            // FireDeductCoinsRequested(int amount) — 1 param
            EventManager.FireDeductCoinsRequested(10);
        }

        PlayerDataManager.UpdateProfile(username, _selectedAvatarIndex);
        EventManager.FireUpdateProfileRequested(username, _selectedAvatarIndex);

        HomePageView homePage = FindObjectOfType<HomePageView>();
        if (homePage != null)
            homePage.RefreshUI(); // RefreshUI() is public on HomePageView

        EventManager.FireHideView(ViewType.Profile);
    }

    private void OnCloseClicked()
    {
        if (!_isFirstTime)
        {
            EventManager.FireHideView(ViewType.Profile);
            return;
        }

        if (_isValidatingUsername) return;

        string chosenName = nameInputField != null ? nameInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(chosenName) || chosenName.Length < 3)
        {
            GenerateUniqueUsername();
            return;
        }

        _isValidatingUsername = true;
        if (confirmButton != null) confirmButton.interactable = false;
        if (closeButton != null) closeButton.interactable = false;

        // Always go straight to PlayFab — skip the DisplayName equality shortcut
        EventManager.FireCheckUsernameAvailability(chosenName, isAvailable =>
        {
            _isValidatingUsername = false;
            if (confirmButton != null) confirmButton.interactable = true;
            if (closeButton != null) closeButton.interactable = true;

            if (!isAvailable)
            {
                EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
                EventManager.FireShowPopUp($"Username '{chosenName}' is already taken.\nPlease try another one.");
                GenerateUniqueUsername();
                return;
            }

            PlayerDataManager.UpdateProfile(chosenName, _selectedAvatarIndex);
            EventManager.FireUpdateProfileRequested(chosenName, _selectedAvatarIndex);
            EventManager.FireHideView(ViewType.Profile);
        });
    }
}