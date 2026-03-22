using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class WinView : MonoBehaviour
{
    [Header("Prize")] [SerializeField] private Text prizeTxt;

    [Header("Player Info")] [SerializeField]
    private Image avatarImage;

    [SerializeField] private Text playerNameTxt;
    [SerializeField] private Sprite[] avatarSprites;

    [Header("Buttons")] [SerializeField] private Button claimButton;

    private int _prizeAmount;
    private bool _claimed = false;

    private void OnEnable()
    {
        EventManager.FirePlaySound(SoundType.WinSound);
        claimButton.onClick.AddListener(OnClaimClicked);
        RefreshUI();
    }

    private void OnDisable()
    {
        claimButton.onClick.RemoveListener(OnClaimClicked);
    }

    /// <summary>Call before showing this view to set prize amount.</summary>
    public void Setup(int prizeAmount)
    {
        _prizeAmount = prizeAmount;
        _claimed = false;
        claimButton.gameObject.SetActive(true);
        claimButton.interactable = true;
        if (prizeTxt) prizeTxt.text = prizeAmount.ToString();
    }

    private void RefreshUI()
    {
        if (playerNameTxt) playerNameTxt.text = PlayerDataManager.DisplayName;

        if (avatarImage != null && avatarSprites != null &&
            PlayerDataManager.AvatarIndex >= 0 &&
            PlayerDataManager.AvatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[PlayerDataManager.AvatarIndex];

        prizeTxt.text = _prizeAmount.ToString();
    }

    private void OnClaimClicked()
    {
        if (_claimed) return;
        _claimed = true;

        claimButton.gameObject.SetActive(false);

        if (_prizeAmount > 0)
        {
            EventManager.FireAddCoinsRequested(_prizeAmount, "game_win");
            PlayerPrefs.SetInt("Winning", PlayerPrefs.GetInt("Winning", 0) + _prizeAmount);
        }

        EventManager.FireLeaveRoomRequested();
        StartCoroutine(ReloadSceneAsync());
    }

    private IEnumerator ReloadSceneAsync()
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().buildIndex);
        op.allowSceneActivation = false;
        while (op.progress < 0.9f)
            yield return null;
        op.allowSceneActivation = true;
    }
}