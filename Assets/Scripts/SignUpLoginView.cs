using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SignUpLoginView : MonoBehaviour
{
    [Header("Input Fields")] [SerializeField]
    private TMP_InputField emailInputField;

    [SerializeField] private TMP_InputField passwordInputField;

    [Header("Buttons")] [SerializeField] private Button loginButton;
    [SerializeField] private Button registerAndLoginButton;
    [SerializeField] private Button loginAsGuestButton;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        loginButton.onClick.AddListener(OnLoginClicked);
        registerAndLoginButton.onClick.AddListener(OnRegisterAndLoginClicked);
        loginAsGuestButton.onClick.AddListener(OnLoginAsGuestClicked);
    }

    private void OnEnable()
    {
        ResetView();
        EventManager.OnAuthSuccess += HandleAuthSuccess;
        EventManager.OnAuthConflict += HandleAuthConflict;
        EventManager.OnPlayerDataLoaded += HandlePlayerDataLoaded;
    }

    private void OnDisable()
    {
        EventManager.OnAuthSuccess -= HandleAuthSuccess;
        EventManager.OnAuthConflict -= HandleAuthConflict;
        EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
    }

    private void OnDestroy()
    {
        loginButton.onClick.RemoveListener(OnLoginClicked);
        registerAndLoginButton.onClick.RemoveListener(OnRegisterAndLoginClicked);
        loginAsGuestButton.onClick.RemoveListener(OnLoginAsGuestClicked);
    }

    private void ResetView()
    {
        emailInputField.text = "";
        passwordInputField.text = "";
    }

    // ── Button Callbacks ──────────────────────────────────────────────────────
    private void OnLoginClicked()
    {
        if (!ValidateInputs()) return;
        SetButtonsInteractable(false);
        EventManager.FireLoginRequested(
            emailInputField.text.Trim(),
            passwordInputField.text);
    }

    private void OnRegisterAndLoginClicked()
    {
        if (!ValidateInputs()) return;
        SetButtonsInteractable(false);
        // 2-param version: (email, password)
        EventManager.FireRegisterAndLoginRequested(
            emailInputField.text.Trim(),
            passwordInputField.text);
    }

    private void OnLoginAsGuestClicked()
    {
        EventManager.FireShowView(ViewType.GuestConfirmation, showAsDialogue: true);
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void HandleAuthSuccess()
    {
        SetButtonsInteractable(true);
    }

    // Matches Action<bool> — bool = triedLogin (true = tried to login but not registered,
    // false = tried to register but already registered)
    private void HandleAuthConflict(bool triedLogin)
    {
        SetButtonsInteractable(true);

        string message = triedLogin
            ? "You are not registered.\nPlease click \"Register & Login\" to create an account."
            : "You are already registered.\nPlease click \"Login\" to sign in.";

        EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
        EventManager.FireShowPopUp(message);
    }

    private void HandlePlayerDataLoaded()
    {
        EventManager.FireShowView(ViewType.Home);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(emailInputField.text) ||
            string.IsNullOrWhiteSpace(passwordInputField.text))
        {
            Debug.LogWarning("[SignUpLoginView] Email or password is empty.");
            return false;
        }

        return true;
    }

    private void SetButtonsInteractable(bool state)
    {
        loginButton.interactable = state;
        registerAndLoginButton.interactable = state;
        loginAsGuestButton.interactable = state;
    }
}