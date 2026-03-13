using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class FriendsView : MonoBehaviour
{
    [Header("References")] [SerializeField]
    private Transform cardContainer;

    [SerializeField] private FriendCard cardPrefab;
    [SerializeField] private Button addFriendButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button inviteButton;

    private const int InitialPoolSize = 10;
    private List<FriendCard> _pool = new List<FriendCard>();
    private List<FriendCard> _active = new List<FriendCard>();

    private void OnEnable()
    {
        addFriendButton.onClick.AddListener(OnAddFriendClicked);
        closeButton.onClick.AddListener(OnCloseClicked);
        if (inviteButton != null)
            inviteButton.onClick.AddListener(OnInviteClicked);
        EventManager.OnFriendsFetched += HandleFriendsFetched;
        EventManager.OnFriendAdded += HandleFriendListChanged;
        EventManager.OnFriendRemoved += HandleFriendRemoved;

        EnsurePool();
        RefreshList();

        if (!FriendsManager.IsInitialized)
            EventManager.FireFetchFriendsRequested();
    }

    private void OnDisable()
    {
        addFriendButton.onClick.RemoveListener(OnAddFriendClicked);
        closeButton.onClick.RemoveListener(OnCloseClicked);
        if (inviteButton != null) inviteButton.onClick.RemoveAllListeners();

        EventManager.OnFriendsFetched -= HandleFriendsFetched;
        EventManager.OnFriendAdded -= HandleFriendListChanged;
        EventManager.OnFriendRemoved -= HandleFriendRemoved;
    }

    private void EnsurePool()
    {
        while (_pool.Count < InitialPoolSize)
        {
            var card = Instantiate(cardPrefab, cardContainer);
            card.gameObject.SetActive(false);
            _pool.Add(card);
        }
    }

    private FriendCard GetFromPool()
    {
        foreach (var card in _pool)
            if (!card.gameObject.activeSelf)
            {
                card.gameObject.SetActive(true);
                return card;
            }

        var newCard = Instantiate(cardPrefab, cardContainer);
        _pool.Add(newCard);
        newCard.gameObject.SetActive(true);
        return newCard;
    }

    private void ReturnAllToPool()
    {
        foreach (var card in _active)
            card.gameObject.SetActive(false);
        _active.Clear();
    }

    private void RefreshList()
    {
        ReturnAllToPool();
        var friends = FriendsManager.Friends;
        for (int i = 0; i < friends.Count; i++)
        {
            var card = GetFromPool();
            card.Setup(friends[i], i + 1, OnDeleteRequested);
            _active.Add(card);
        }
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

    private void OnDeleteRequested(FriendData friend)
    {
        PlayerPrefs.SetString("PendingDeleteFriendId", friend.PlayFabId);
        PlayerPrefs.SetString("PendingDeleteFriendName", friend.DisplayName);
        PlayerPrefs.Save();
        // Show as dialogue — FriendsView stays active underneath
        EventManager.FireShowView(ViewType.DeleteFriend, true);
    }

    private void HandleFriendsFetched() => RefreshList();

    private void HandleFriendListChanged(string playFabId) => RefreshList();

    private void HandleFriendRemoved(string playFabId) => RefreshList();

    private void OnAddFriendClicked()
    {
        // Show as dialogue — FriendsView stays active
        EventManager.FireShowView(ViewType.AddFriend, true);
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Friends);
    }
}