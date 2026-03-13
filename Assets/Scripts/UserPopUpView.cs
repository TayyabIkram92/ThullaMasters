using UnityEngine;
using UnityEngine.UI;

public class UserPopUpView : MonoBehaviour
{
    [SerializeField] private Text messageText;
    [SerializeField] private Button closeButton;

    private void Awake()
    {
        closeButton.onClick.AddListener(OnCloseClicked);
    }

    private void OnEnable()
    {
        EventManager.OnShowPopUp += HandleShowPopUp;
    }

    private void OnDisable()
    {
        EventManager.OnShowPopUp -= HandleShowPopUp;
    }

    private void OnDestroy()
    {
        closeButton.onClick.RemoveListener(OnCloseClicked);
    }

    private void HandleShowPopUp(string message)
    {
        if (messageText != null)
            messageText.text = message;
    }

    private void OnCloseClicked()
        => EventManager.FireHideView(ViewType.UserPopUp);
}