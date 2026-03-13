using UnityEngine;
using UnityEngine.UI;

public class GuestConfirmationDialogue : MonoBehaviour
{
    [SerializeField] private Button confirmButton;
    [SerializeField] private Button cancelButton;

    private void Awake()
    {
        confirmButton.onClick.AddListener(OnConfirmClicked);
        if (cancelButton != null)
            cancelButton.onClick.AddListener(OnCancelClicked);
    }

    private void OnEnable()
    {
        EventManager.OnAuthSuccess += HandleAuthSuccess;
        EventManager.OnPlayerDataLoaded += HandlePlayerDataLoaded;
    }

    private void OnDisable()
    {
        EventManager.OnAuthSuccess -= HandleAuthSuccess;
        EventManager.OnPlayerDataLoaded -= HandlePlayerDataLoaded;
    }

    private void OnDestroy()
    {
        confirmButton.onClick.RemoveListener(OnConfirmClicked);
        if (cancelButton != null)
            cancelButton.onClick.RemoveListener(OnCancelClicked);
    }

    private void OnConfirmClicked()
    {
        SetButtonsInteractable(false);
        EventManager.FireGuestLoginRequested();
    }

    private void OnCancelClicked()
        => EventManager.FireHideView(ViewType.GuestConfirmation);

    private void HandleAuthSuccess()
    {
        SetButtonsInteractable(true);
        // EventManager.FireShowView(ViewType.Home);
    }

    private void SetButtonsInteractable(bool state)
    {
        confirmButton.interactable = state;
        if (cancelButton != null)
            cancelButton.interactable = state;
    }

    private void HandlePlayerDataLoaded() // ← NEW
    {
        EventManager.FireShowView(ViewType.Home);
    }
}