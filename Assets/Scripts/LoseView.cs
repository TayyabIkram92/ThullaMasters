using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

public class LoseView : MonoBehaviour
{
    [Header("Coins")] [SerializeField] private Text coinsTxt;

    [Header("Player Info")] [SerializeField]
    private Image avatarImage;

    [SerializeField] private Text playerNameTxt; // Legacy Text
    [SerializeField] private Sprite[] avatarSprites;

    [Header("Buttons")] [SerializeField] private Button homeButton;

    private void OnEnable()
    {
        EventManager.FirePlaySound(SoundType.LoseSound);
        homeButton.onClick.AddListener(OnHomeClicked);
        EventManager.OnCoinsUpdated += HandleCoinsUpdated;
        RefreshUI();
    }

    private void OnDisable()
    {
        homeButton.onClick.RemoveListener(OnHomeClicked);
        EventManager.OnCoinsUpdated -= HandleCoinsUpdated;
    }

    private void RefreshUI()
    {
        if (playerNameTxt) playerNameTxt.text = PlayerDataManager.DisplayName;

        if (avatarImage != null && avatarSprites != null &&
            PlayerDataManager.AvatarIndex >= 0 &&
            PlayerDataManager.AvatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[PlayerDataManager.AvatarIndex];
    }

    private void HandleCoinsUpdated(int amount)
    {
        coinsTxt.text = amount.ToString();
    }

    private void OnHomeClicked()
    {
        homeButton.gameObject.SetActive(false);
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