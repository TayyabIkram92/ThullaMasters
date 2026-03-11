using UnityEngine;
using UnityEngine.UI;
using UI;
using System.Collections.Generic;
using DG.Tweening;

/// <summary>
/// Displays all available game mode cards.
/// Instantiates cards dynamically based on GameModeManager data.
/// </summary>
public class GameSelectionView : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private Transform  cardContainer;
    [SerializeField] private GameObject cardPrefab;
    [SerializeField] private Button     backButton;
    [SerializeField] private Text       coinsText;

    private List<GameObject> _instantiatedCards = new List<GameObject>();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (backButton != null)
            backButton.onClick.AddListener(OnBackClicked);
    }

    private void OnEnable()
    {
        EventManager.OnGameModeSelected += HandleGameModeSelected;

        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        SpawnGameModeCards();
    }

    private void OnDisable()
    {
        EventManager.OnGameModeSelected -= HandleGameModeSelected;
        DestroyAllCards();
    }

    private void OnDestroy()
    {
        if (backButton != null) backButton.onClick.RemoveAllListeners();
    }

    // ── Card Instantiation ────────────────────────────────────────────────────

    private void SpawnGameModeCards()
    {
        DestroyAllCards();

        if (!GameModeManager.IsInitialized)
        {
            Debug.LogWarning("[GameSelectionView] GameModeManager not initialized. Fetching...");
            EventManager.FireFetchGameModesRequested();
            EventManager.OnGameModesFetched += SpawnGameModeCards;
            return;
        }

        EventManager.OnGameModesFetched -= SpawnGameModeCards;

        int index = 0;
        float delayIncrement = 0.25f;

        foreach (var modeData in GameModeManager.AvailableModes)
        {
            GameObject cardObj = Instantiate(cardPrefab, cardContainer);

            GameModeCard card = cardObj.GetComponent<GameModeCard>();
            if (card != null)
                card.Initialize(modeData);
            else
                Debug.LogError("[GameSelectionView] CardPrefab missing GameModeCard component!");

            // Start from scale 0
            cardObj.transform.localScale = Vector3.zero;

            // Calculate delay
            float delay = index * delayIncrement;

            // DOTween animation
            cardObj.transform
                .DOScale(1f, 0.5f)
                .SetEase(Ease.OutBack)
                .SetDelay(delay);

            _instantiatedCards.Add(cardObj);

            index++;
        }

        Debug.Log($"[GameSelectionView] Spawned {_instantiatedCards.Count} game mode cards.");
    }

    private void DestroyAllCards()
    {
        foreach (var card in _instantiatedCards)
            if (card != null) Destroy(card);

        _instantiatedCards.Clear();
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void HandleGameModeSelected(GameModeData modeData)
    {
        // Check coins
        if (PlayerDataManager.Coins < modeData.EntryFee)
        {
            Debug.LogWarning($"[GameSelectionView] Not enough coins. Need: {modeData.EntryFee}, Have: {PlayerDataManager.Coins}");
            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            EventManager.FireShowPopUp(
                $"You need {modeData.EntryFee} coins to enter this game mode.\nYou have {PlayerDataManager.Coins} coins.");
            return;
        }

        // Store the selected mode FIRST so MatchmakingView can read it in OnEnable
        // This must happen before FireShowView — FireShowView calls SetActive(true)
        // which triggers OnEnable on MatchmakingView immediately
        GameModeManager.SetSelectedMode(modeData);

        EventManager.FireShowView(ViewType.Matchmaking);
    }

    private void OnBackClicked()
    {
        EventManager.FireShowView(ViewType.Home);
    }
}
