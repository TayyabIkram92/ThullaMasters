using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows all friends in matchmaking context with Invite buttons.
/// Shown as dialogue on top of MatchmakingView.
/// </summary>
public class OnlineFriendsView : MonoBehaviour
{
    [Header("References")] [SerializeField]
    private Transform cardContainer;

    [SerializeField] private OnlineFriendCard cardPrefab;
    [SerializeField] private Button closeButton;

    private const int InitialPoolSize = 10;
    private List<OnlineFriendCard> _pool = new List<OnlineFriendCard>();
    private List<OnlineFriendCard> _active = new List<OnlineFriendCard>();

    private void OnEnable()
    {
        closeButton.onClick.AddListener(OnCloseClicked);
        EventManager.OnFriendsFetched += HandleFriendsFetched;

        EnsurePool();
        RefreshList();

        if (!FriendsManager.IsInitialized)
            EventManager.FireFetchFriendsRequested();
    }

    private void OnDisable()
    {
        closeButton.onClick.RemoveListener(OnCloseClicked);
        EventManager.OnFriendsFetched -= HandleFriendsFetched;
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

    private OnlineFriendCard GetFromPool()
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
        foreach (var card in _active) card.gameObject.SetActive(false);
        _active.Clear();
    }

    private void RefreshList()
    {
        ReturnAllToPool();
        var friends = FriendsManager.Friends;
        for (int i = 0; i < friends.Count; i++)
        {
            var card = GetFromPool();
            card.Setup(friends[i], i + 1, OnInviteFriend);
            _active.Add(card);
        }
    }

    private void OnInviteFriend(FriendData friend)
    {
        string roomId = InviteManager.CurrentRoomId;
        if (string.IsNullOrEmpty(roomId))
        {
            Debug.LogWarning("[OnlineFriendsView] No room ID to invite into.");
            return;
        }

        EventManager.FireSendInviteRequested(friend.PlayFabId, roomId);

        // Notify MatchmakingView to hide AddIcon/PopupAddFriend and disable back button
        EventManager.FireInviteSent();

        // Close after invite sent
        EventManager.FireHideView(ViewType.OnlineFriends);
    }

    private void HandleFriendsFetched() => RefreshList();

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.OnlineFriends);
    }
}