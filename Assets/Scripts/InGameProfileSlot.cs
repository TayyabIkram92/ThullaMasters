using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InGameProfileSlot : MonoBehaviour
{
    [SerializeField] private Image avatarImage;
    [SerializeField] private Text displayNameTxt;
    [SerializeField] private Text cardCountTxt;
    [SerializeField] private Image timerFillImage; // fillAmount 1→0 over turn duration
    [SerializeField] private GameObject activeHighlight;
    [SerializeField] private Image playedCardImage;
    [SerializeField] private Sprite[] avatarSprites;
    [Header("Testing")]
    [SerializeField] public Transform testCardContainer; // assign in Inspector, inactive by default
    public void Setup(SlotData data)
    {
        if (displayNameTxt) displayNameTxt.text = data.displayName;
        if (avatarImage != null && avatarSprites != null &&
            data.avatarIndex >= 0 && data.avatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[data.avatarIndex];
        if (activeHighlight) activeHighlight.SetActive(false);
        if (timerFillImage) timerFillImage.gameObject.SetActive(false);
        if (playedCardImage) playedCardImage.gameObject.SetActive(false);
        if (cardCountTxt) cardCountTxt.text = "0";    // ← ADD: initialize to 0
    }

    public void SetCardCount(int count)
    {
        if (cardCountTxt) cardCountTxt.text = count.ToString();
    }

    public void SetActive(bool active)
    {
        if (activeHighlight) activeHighlight.SetActive(active);

        // Show timer only during this player's turn; reset to full when turn starts
        if (timerFillImage != null)
        {
            timerFillImage.gameObject.SetActive(active);
            if (active) timerFillImage.fillAmount = 1f;
        }
    }
    public void SetTestCards(List<Sprite> cardSprites)
    {
        if (testCardContainer == null) return;

        // Clear existing test cards
        foreach (Transform child in testCardContainer)
            Destroy(child.gameObject);

        foreach (var sprite in cardSprites)
        {
            var go = new GameObject("TestCard", typeof(Image));
            go.transform.SetParent(testCardContainer, false);
            go.GetComponent<Image>().sprite = sprite;
        }
    }
    public void SetTimerValue(float secondsRemaining, float totalSeconds)
    {
        if (timerFillImage != null && totalSeconds > 0f)
            timerFillImage.fillAmount = Mathf.Clamp01(secondsRemaining / totalSeconds);
    }

    public void ShowPlayedCard(Sprite sprite)
    {
        if (playedCardImage != null)
        {
            playedCardImage.sprite = sprite;
            playedCardImage.gameObject.SetActive(true);
        }
    }

    public void ClearPlayedCard()
    {
        if (playedCardImage) playedCardImage.gameObject.SetActive(false);
    }
}