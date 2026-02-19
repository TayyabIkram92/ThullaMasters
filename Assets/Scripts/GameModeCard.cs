using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Controls a single game mode card.
/// Handles Front/Back flip and displays entry fee + prizes.
/// </summary>
public class GameModeCard : MonoBehaviour
{
    [Header("Card Sides")] [SerializeField]
    private GameObject frontSide;

    [SerializeField] private GameObject backSide;

    [Header("Front Side UI")] [SerializeField]
    private Text entryFeeTxt;

    [SerializeField] private Text prizeTxt;
    [SerializeField] private Button infoButton;

    [Header("Back Side UI")] [SerializeField]
    private Text prize1stTxt;

    [SerializeField] private Text prize2ndTxt;
    [SerializeField] private Text prize3rdTxt;
    [SerializeField] private Button backButton;

    [Header("Card Button (entire front is clickable)")] [SerializeField]
    private Button cardButton;

    private GameModeData _modeData;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (infoButton != null)
            infoButton.onClick.AddListener(ShowBack);

        if (backButton != null)
            backButton.onClick.AddListener(ShowFront);

        if (cardButton != null)
            cardButton.onClick.AddListener(OnCardClicked);
    }

    private void OnDestroy()
    {
        if (infoButton != null) infoButton.onClick.RemoveAllListeners();
        if (backButton != null) backButton.onClick.RemoveAllListeners();
        if (cardButton != null) cardButton.onClick.RemoveAllListeners();
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public void Initialize(GameModeData modeData)
    {
        _modeData = modeData;

        // Front side
        if (entryFeeTxt != null)
            entryFeeTxt.text = $"{modeData.EntryFee}";

        if (prizeTxt != null)
            prizeTxt.text = $"{modeData.Prize}";

        // Back side
        if (prize1stTxt != null)
            prize1stTxt.text = $"{modeData.Prize1st}";

        if (prize2ndTxt != null)
            prize2ndTxt.text = $"{modeData.Prize2nd}";

        if (prize3rdTxt != null)
            prize3rdTxt.text = $"{modeData.Prize3rd}";

        // Start with front visible
        ShowFront();
    }

    // ── Card Flip ─────────────────────────────────────────────────────────────

    private void ShowFront()
    {
        if (frontSide != null) frontSide.SetActive(true);
        if (backSide != null) backSide.SetActive(false);
    }

    private void ShowBack()
    {
        if (frontSide != null) frontSide.SetActive(false);
        if (backSide != null) backSide.SetActive(true);
    }

    // ── User Interaction ──────────────────────────────────────────────────────

    private void OnCardClicked()
    {
        // Only allow click if showing front side
        if (frontSide != null && !frontSide.activeSelf)
            return;

        Debug.Log($"[GameModeCard] Selected mode with entry fee: {_modeData.EntryFee}");
        EventManager.FireGameModeSelected(_modeData);
    }
}