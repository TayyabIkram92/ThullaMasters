using UnityEngine;
using UnityEngine.UI;
using UI;
using System.Collections.Generic;

/// <summary>
/// Displays all available game mode cards.
/// Instantiates cards dynamically based on GameModeManager data.
/// </summary>
public class GameSelectionView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Transform cardContainer; // Parent for instantiated cards (ScrollView Content)

    [SerializeField] private GameObject cardPrefab; // GameModeCard prefab
    [SerializeField] private Button backButton;
    [SerializeField] private Text coinsText;

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

        // Refresh coins display
        if (coinsText != null)
            coinsText.text = PlayerDataManager.Coins.ToString();

        // Spawn cards
        SpawnGameModeCards();
    }

    private void OnDisable()
    {
        EventManager.OnGameModeSelected -= HandleGameModeSelected;

        // Clean up instantiated cards
        DestroyAllCards();
    }

    private void OnDestroy()
    {
        if (backButton != null) backButton.onClick.RemoveAllListeners();
    }

    // ── Card Instantiation ────────────────────────────────────────────────────

    private void SpawnGameModeCards()
    {
        // Clean up existing cards first
        DestroyAllCards();

        if (!GameModeManager.IsInitialized)
        {
            Debug.LogWarning("[GameSelectionView] GameModeManager not initialized. Fetching...");
            EventManager.FireFetchGameModesRequested();
            EventManager.OnGameModesFetched += SpawnGameModeCards;
            return;
        }

        // Unsubscribe if we were waiting
        EventManager.OnGameModesFetched -= SpawnGameModeCards;

        // Instantiate a card for each game mode
        foreach (var modeData in GameModeManager.AvailableModes)
        {
            GameObject cardObj = Instantiate(cardPrefab, cardContainer);

            GameModeCard card = cardObj.GetComponent<GameModeCard>();
            if (card != null)
            {
                card.Initialize(modeData);
            }
            else
            {
                Debug.LogError("[GameSelectionView] CardPrefab missing GameModeCard component!");
            }

            _instantiatedCards.Add(cardObj);
        }

        Debug.Log($"[GameSelectionView] Spawned {_instantiatedCards.Count} game mode cards.");
    }

    private void DestroyAllCards()
    {
        foreach (var card in _instantiatedCards)
        {
            if (card != null)
                Destroy(card);
        }

        _instantiatedCards.Clear();
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void HandleGameModeSelected(GameModeData modeData)
    {
        // Check if user has enough coins
        if (PlayerDataManager.Coins < modeData.EntryFee)
        {
            Debug.LogWarning(
                $"[GameSelectionView] Not enough coins. Need: {modeData.EntryFee}, Have: {PlayerDataManager.Coins}");

            EventManager.FireShowView(ViewType.UserPopUp, showAsDialogue: true);
            EventManager.FireShowPopUp(
                $"You need {modeData.EntryFee} coins to enter this game mode.\nYou have {PlayerDataManager.Coins} coins.");
            return;
        }

        // Navigate to matchmaking/room creation (placeholder for now)
        Debug.Log($"[GameSelectionView] Navigating to matchmaking with entry fee: {modeData.EntryFee}");

        // TODO: Navigate to Matchmaking/Room view
        // EventManager.FireShowView(ViewType.Matchmaking);
    }

    private void OnBackClicked()
    {
        EventManager.FireShowView(ViewType.Home);
    }
}