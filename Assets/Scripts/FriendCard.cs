using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Represents a single friend in FriendsView.
/// Poolable — call Setup() to reuse.
/// trophiesTxt shows the friend's position number (1, 2, 3...) not their trophies.
/// </summary>
public class FriendCard : MonoBehaviour
{
    [SerializeField] private Image avatarImage;
    [SerializeField] private Text displayNameTxt;
    [SerializeField] private Text trophiesTxt;   // shows friend number
    [SerializeField] private Button deleteButton;
    [SerializeField] private Sprite[] avatarSprites;

    private FriendData _friendData;
    private System.Action<FriendData> _onDeleteClicked;

    private void OnEnable()
    {
        deleteButton.onClick.AddListener(OnDeleteClicked);
    }

    private void OnDisable()
    {
        deleteButton.onClick.RemoveListener(OnDeleteClicked);
    }

    /// <summary>
    /// Setup this card for a friend.
    /// </summary>
    /// <param name="data">Friend data</param>
    /// <param name="friendNumber">1-based position number shown in trophiesTxt</param>
    /// <param name="onDelete">Callback when delete is pressed</param>
    public void Setup(FriendData data, int friendNumber, System.Action<FriendData> onDelete)
    {
        _friendData = data;
        _onDeleteClicked = onDelete;

        displayNameTxt.text = data.DisplayName;
        trophiesTxt.text = friendNumber.ToString();

        if (avatarImage != null && avatarSprites != null &&
            data.AvatarIndex >= 0 && data.AvatarIndex < avatarSprites.Length)
            avatarImage.sprite = avatarSprites[data.AvatarIndex];
    }

    private void OnDeleteClicked()
    {
        _onDeleteClicked?.Invoke(_friendData);
    }

    public FriendData GetFriendData() => _friendData;
}
