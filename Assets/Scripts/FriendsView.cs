using UnityEngine;
using UnityEngine.UI;
using UI;
using System.Collections.Generic;

/// <summary>
/// Displays friends list with add/invite functionality.
/// Shows as dialogue over HomePage.
/// </summary>
public class FriendsView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Transform friendCardContainer; // ScrollView Content

    [SerializeField] private GameObject friendCardPrefab;
    [SerializeField] private Button addFriendButton;
    [SerializeField] private Button inviteButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private GameObject noFriendsText; // "No Friends in your list"

    private List<GameObject> _instantiatedCards = new List<GameObject>();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (addFriendButton != null)
            addFriendButton.onClick.AddListener(OnAddFriendClicked);

        if (inviteButton != null)
            inviteButton.onClick.AddListener(OnInviteClicked);

        if (closeButton != null)
            closeButton.onClick.AddListener(OnCloseClicked);
    }

    private void OnEnable()
    {
        EventManager.OnFriendAdded += HandleFriendAdded;
        EventManager.OnFriendRemoved += HandleFriendRemoved;

        SpawnFriendCards();
    }

    private void OnDisable()
    {
        EventManager.OnFriendAdded -= HandleFriendAdded;
        EventManager.OnFriendRemoved -= HandleFriendRemoved;

        DestroyAllCards();
    }

    private void OnDestroy()
    {
        if (addFriendButton != null) addFriendButton.onClick.RemoveAllListeners();
        if (inviteButton != null) inviteButton.onClick.RemoveAllListeners();
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
    }

    // ── Card Spawning ─────────────────────────────────────────────────────────

    private void SpawnFriendCards()
    {
        DestroyAllCards();

        if (!FriendsManager.IsInitialized || FriendsManager.Friends.Count == 0)
        {
            if (noFriendsText != null)
                noFriendsText.SetActive(true);
            return;
        }

        if (noFriendsText != null)
            noFriendsText.SetActive(false);

        foreach (var friendData in FriendsManager.Friends)
        {
            GameObject cardObj = Instantiate(friendCardPrefab, friendCardContainer);

            FriendCard card = cardObj.GetComponent<FriendCard>();
            if (card != null)
            {
                card.Initialize(friendData);
            }

            _instantiatedCards.Add(cardObj);
        }

        Debug.Log($"[FriendsView] Spawned {_instantiatedCards.Count} friend cards.");
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

    private void HandleFriendAdded(FriendData friend)
    {
        // Refresh the list
        SpawnFriendCards();
    }

    private void HandleFriendRemoved()
    {
        // Refresh the list
        SpawnFriendCards();
    }

    // ── Button Handlers ───────────────────────────────────────────────────────

    private void OnAddFriendClicked()
    {
        EventManager.FireShowView(ViewType.AddFriend, showAsDialogue: true);
    }

    private void OnInviteClicked()
    {
        string playerUsername = PlayerDataManager.DisplayName;
        string appUrl =
            "https://play.google.com/store/apps/details?id=com.yourcompany.thullamasters"; // Update with your actual URL

        string message = $"Play Thulla Masters with me! My ID: {playerUsername}. Download: {appUrl}";
        string encodedMessage = UnityEngine.Networking.UnityWebRequest.EscapeURL(message);

        string whatsappUrl = $"https://wa.me/?text={encodedMessage}";

        Application.OpenURL(whatsappUrl);

        Debug.Log($"[FriendsView] Opening WhatsApp with invite message.");
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Friends);
    }
}