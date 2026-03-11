using UnityEngine;
using UnityEngine.UI;
using UI;
using System.Collections.Generic;
using DG.Tweening;

public class FriendsView : MonoBehaviour
{
    [Header("UI References")] [SerializeField]
    private Transform friendCardContainer;

    [SerializeField] private GameObject friendCardPrefab;
    [SerializeField] private Button addFriendButton;
    [SerializeField] private Button inviteButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private GameObject noFriendsText;

    private List<GameObject> _instantiatedCards = new List<GameObject>();

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

        float baseDelay = 0.5f;
        float delayIncrement = 0.25f;
        int index = 0;

        foreach (var friendData in FriendsManager.Friends)
        {
            GameObject cardObj = Instantiate(friendCardPrefab, friendCardContainer);

            FriendCard card = cardObj.GetComponent<FriendCard>();
            if (card != null)
                card.Initialize(friendData);

            // Start scale at 0
            cardObj.transform.localScale = Vector3.zero;

            // Calculate delay
            float delay = baseDelay + (index * delayIncrement);

            // Animate
            cardObj.transform
                .DOScale(1f, 0.5f)
                .SetEase(Ease.OutBack)
                .SetDelay(delay);

            _instantiatedCards.Add(cardObj);

            index++;
        }
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

    private void HandleFriendAdded(FriendData friend)
    {
        SpawnFriendCards();
    }

    private void HandleFriendRemoved()
    {
        SpawnFriendCards();
    }

    private void OnAddFriendClicked()
    {
        EventManager.FireShowView(ViewType.AddFriend, showAsDialogue: true);
    }

    private void OnInviteClicked()
    {
        string playerName = PlayerDataManager.DisplayName;
        string appUrl = "https://play.google.com/store/apps/details?id=com.yourcompany.thullamasters";

        string message = $"Play Thulla Masters with me!\n\nMy Username: {playerName}\n\nDownload: {appUrl}";
        string encodedMessage = UnityEngine.Networking.UnityWebRequest.EscapeURL(message);

        Application.OpenURL($"https://wa.me/?text={encodedMessage}");
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Friends);
    }
}