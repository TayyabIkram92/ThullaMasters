using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Controls a single player slot in the Matchmaking screen.
///
/// Three slot types share this one script, configured via Inspector:
///
/// ── Slot 1: UserProfile (local player) ───────────────────────────────────────
///   Wire: avatarGO, avatarImage, playerNameTxt
///   isFriendSlot = false
///   Call SetLocalPlayer() once on enable — never SetEmpty().
///
/// ── Slot 2: Friend/RandomProfile (friend invite slot) ────────────────────────
///   Wire: avatarGO, avatarImage, playerNameTxt, addIconGO, popupAddFriendGO
///   isFriendSlot = true
///   Empty state  → avatarGO hidden, addIconGO + popupAddFriendGO visible.
///   Filled state → avatarGO visible, addIconGO + popupAddFriendGO hidden.
///
/// ── Slots 3 & 4: RandomProfile ───────────────────────────────────────────────
///   Wire: avatarGO, avatarImage, playerNameTxt
///   isFriendSlot = false
///   Empty state  → avatarGO hidden.
///   Filled state → avatarGO visible.
///
/// Avatar Sprites (0-15): drag all 16 sprites in order in every slot's Inspector.
/// </summary>
public class PlayerSlotUI : MonoBehaviour
{
    [Header("Slot Type")]
    [SerializeField] private bool isFriendSlot = false; // true only for Slot 2

    [Header("Always Required")]
    [SerializeField] private GameObject avatarGO;       // Avatar child GO — hidden when empty
    [SerializeField] private Image      avatarImage;    // Image inside avatarGO
    [SerializeField] private Text       playerNameTxt;  // Name label

    [Header("Friend Slot Only (Slot 2)")]
    [SerializeField] private GameObject addIconGO;          // AddIcon GO — visible when empty
    [SerializeField] private GameObject popupAddFriendGO;   // PopupAddFriend GO — visible when empty

    [Header("Avatar Sprites (0-15)")]
    [SerializeField] private Sprite[] avatarSprites = new Sprite[16];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Set slot to empty/waiting state.
    /// - avatarGO hidden
    /// - Friend slot: addIconGO + popupAddFriendGO shown
    /// </summary>
    public void SetEmpty()
    {
        if (avatarGO != null)      avatarGO.SetActive(false);
        if (playerNameTxt != null) playerNameTxt.text = "";

        if (isFriendSlot)
        {
            if (addIconGO        != null) addIconGO.SetActive(true);
            if (popupAddFriendGO != null) popupAddFriendGO.SetActive(true);
        }
    }

    /// <summary>
    /// Fill this slot with a player or bot.
    /// - avatarGO shown with correct sprite
    /// - Friend slot: addIconGO + popupAddFriendGO hidden
    /// No bot badge shown — bots look identical to real players intentionally.
    /// </summary>
    public void SetPlayer(SlotData slot)
    {
        // Show avatar GO
        if (avatarGO != null) avatarGO.SetActive(true);

        // Set avatar sprite
        if (avatarImage != null &&
            slot.avatarIndex >= 0 &&
            slot.avatarIndex < avatarSprites.Length &&
            avatarSprites[slot.avatarIndex] != null)
        {
            avatarImage.sprite = avatarSprites[slot.avatarIndex];
        }

        // Set name
        if (playerNameTxt != null)
            playerNameTxt.text = slot.displayName;

        // Friend slot: hide add elements
        if (isFriendSlot)
        {
            if (addIconGO        != null) addIconGO.SetActive(false);
            if (popupAddFriendGO != null) popupAddFriendGO.SetActive(false);
        }
    }

    /// <summary>
    /// Sets the local player's own avatar and name (Slot 1 only).
    /// Called once on MatchmakingView enable — this slot never goes empty.
    /// </summary>
    public void SetLocalPlayer(string displayName, int avatarIndex)
    {
        if (avatarGO != null) avatarGO.SetActive(true);

        if (avatarImage != null &&
            avatarIndex >= 0 &&
            avatarIndex < avatarSprites.Length &&
            avatarSprites[avatarIndex] != null)
        {
            avatarImage.sprite = avatarSprites[avatarIndex];
        }

        if (playerNameTxt != null)
            playerNameTxt.text = displayName;
    }
}
