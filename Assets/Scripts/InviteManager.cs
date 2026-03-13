using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// Manages friend invite system via Firestore.
/// invites/{recipientId}/pending/{docId} = {senderId, senderName, roomId, timestamp}
/// Listener starts automatically once both Firebase and PlayFabId are ready,
/// regardless of initialization order.
/// </summary>
public class InviteManager : MonoBehaviour
{
    private const string InvitesCollection = "invites";
    private const string PendingSubcollection = "pending";

    public static string CurrentRoomId { get; private set; }

    public static void SetCurrentRoomId(string roomId) => CurrentRoomId = roomId;
    public static void ClearCurrentRoomId() => CurrentRoomId = null;

    private ListenerRegistration _inviteListener;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        EventManager.OnSendInviteRequested += HandleSendInvite;
        EventManager.OnAcceptInviteRequested += HandleAcceptInvite;
        EventManager.OnRejectInviteRequested += HandleRejectInvite;
        EventManager.OnMatchFound += HandleMatchFound;
        EventManager.OnRoomLeft += HandleRoomLeft;
        EventManager.OnInviteReceived += HandleInviteReceived;

        // Use coroutine to wait for both Firebase and PlayFabId —
        // safe regardless of which initializes first.
        StartCoroutine(WaitAndStartListening());
    }

    private void OnDisable()
    {
        EventManager.OnSendInviteRequested -= HandleSendInvite;
        EventManager.OnAcceptInviteRequested -= HandleAcceptInvite;
        EventManager.OnRejectInviteRequested -= HandleRejectInvite;
        EventManager.OnMatchFound -= HandleMatchFound;
        EventManager.OnRoomLeft -= HandleRoomLeft;
        EventManager.OnInviteReceived -= HandleInviteReceived;

        StopAllCoroutines();
        StopInviteListener();
    }

    // ── Listener Setup ────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until both FirebaseManager.DB and PlayerDataManager.PlayFabId
    /// are ready before starting the invite listener. Works regardless of
    /// initialization order since it polls every frame via WaitUntil.
    /// </summary>
    private IEnumerator WaitAndStartListening()
    {
        yield return new WaitUntil(() =>
            FirebaseManager.DB != null &&
            !string.IsNullOrEmpty(PlayerDataManager.PlayFabId));

        StartListeningForInvites();
    }

    private void StartListeningForInvites()
    {
        StopInviteListener();

        string myId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(myId) || FirebaseManager.DB == null) return;

        // Listen ONLY to this player's own pending subcollection.
        // Path: invites/{myId}/pending/
        // Invites sent to others are written to invites/{recipientId}/pending/ — separate path.
        _inviteListener = FirebaseManager.DB
            .Collection(InvitesCollection)
            .Document(myId)
            .Collection(PendingSubcollection)
            .Listen(snapshot =>
            {
                foreach (var change in snapshot.GetChanges())
                {
                    if (change.ChangeType != DocumentChange.Type.Added) continue;

                    var doc = change.Document;
                    string senderId = "", senderName = "", roomId = "";
                    long timestamp = 0;
                    int entryFee = 0;

                    if (doc.TryGetValue("senderId", out string sid)) senderId = sid;
                    if (doc.TryGetValue("senderName", out string sname)) senderName = sname;
                    if (doc.TryGetValue("roomId", out string rid)) roomId = rid;
                    if (doc.TryGetValue("timestamp", out long ts)) timestamp = ts;
                    if (doc.TryGetValue("entryFee", out int ef)) entryFee = ef;

                    // Discard stale invites older than 10 seconds
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (nowMs - timestamp > 10000)
                    {
                        doc.Reference.DeleteAsync();
                        continue;
                    }

                    PlayerPrefs.SetString("InviteDocId", doc.Id);
                    PlayerPrefs.SetInt("InviteEntryFee", entryFee);
                    PlayerPrefs.Save();

                    // Fire local C# event — only this device receives it
                    EventManager.FireInviteReceived(senderName, senderId, roomId);
                }
            });
    }

    private void StopInviteListener()
    {
        _inviteListener?.Stop();
        _inviteListener = null;
    }

    // ── Invite Received ───────────────────────────────────────────────────────

    /// <summary>
    /// Handles invite popup globally — works from any screen since
    /// InviteManager is always active in a single-scene setup.
    /// </summary>
    private void HandleInviteReceived(string senderName, string senderId, string roomId)
    {
        PlayerPrefs.SetString("InviteSenderName", senderName);
        PlayerPrefs.SetString("InviteSenderId", senderId);
        PlayerPrefs.SetString("InviteRoomId", roomId);
        PlayerPrefs.Save();
        EventManager.FireShowView(ViewType.InvitePopUp, true);
    }

    // ── Send Invite ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes invite doc to invites/{recipientId}/pending/.
    /// Only fires on the local sender's device via C# event.
    /// Auto-deletes after 10 seconds if recipient hasn't acted.
    /// </summary>
    private void HandleSendInvite(string recipientPlayFabId, string roomId)
    {
        if (FirebaseManager.DB == null) return;

        int entryFee = GameModeManager.SelectedMode != null
            ? GameModeManager.SelectedMode.EntryFee
            : 0;

        var inviteData = new Dictionary<string, object>
        {
            { "senderId", PlayerDataManager.PlayFabId },
            { "senderName", PlayerDataManager.DisplayName },
            { "roomId", roomId },
            { "entryFee", entryFee },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };

        FirebaseManager.DB
            .Collection(InvitesCollection)
            .Document(recipientPlayFabId)
            .Collection(PendingSubcollection)
            .AddAsync(inviteData)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogWarning("[InviteManager] Send invite failed: " + task.Exception?.Message);
                    return;
                }

                StartCoroutine(DeleteInviteAfterDelay(task.Result, 10f));
            });
    }

    private IEnumerator DeleteInviteAfterDelay(DocumentReference docRef, float delay)
    {
        yield return new WaitForSeconds(delay);
        docRef?.DeleteAsync();
    }

    // ── Accept / Reject ───────────────────────────────────────────────────────

    /// <summary>
    /// Cleans up the invite doc on accept.
    /// MatchmakingManager is also subscribed to OnAcceptInviteRequested
    /// and handles the actual room join independently.
    /// </summary>
    private void HandleAcceptInvite(string roomId)
    {
        CleanupMyInviteDoc();
    }

    private void HandleRejectInvite()
    {
        CleanupMyInviteDoc();
    }

    private void CleanupMyInviteDoc()
    {
        string docId = PlayerPrefs.GetString("InviteDocId", "");
        string myId = PlayerDataManager.PlayFabId;

        if (!string.IsNullOrEmpty(docId) &&
            !string.IsNullOrEmpty(myId) &&
            FirebaseManager.DB != null)
        {
            FirebaseManager.DB
                .Collection(InvitesCollection)
                .Document(myId)
                .Collection(PendingSubcollection)
                .Document(docId)
                .DeleteAsync();
        }

        PlayerPrefs.DeleteKey("InviteDocId");
        PlayerPrefs.DeleteKey("InviteEntryFee");
        PlayerPrefs.Save();
    }

    // ── Match / Room Events ───────────────────────────────────────────────────

    /// <summary>
    /// Stop listening once match is found — no more invites needed mid-game.
    /// </summary>
    private void HandleMatchFound(RoomData room)
    {
        StopInviteListener();
    }

    /// <summary>
    /// Re-start listening after leaving a room so player can receive invites again.
    /// Uses coroutine to safely wait for DB/PlayFabId in case of re-login.
    /// </summary>
    private void HandleRoomLeft()
    {
        StartCoroutine(WaitAndStartListening());
    }
}