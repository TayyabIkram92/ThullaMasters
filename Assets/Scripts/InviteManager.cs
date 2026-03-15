using System;
using System.Collections;
using System.Collections.Generic;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// Manages friend invite system via Firestore.
///
/// Paths:
///   invites/{recipientId}/pending/{docId}            — invite sent to recipient
///   rooms/{roomId}/inviteResponses/{recipientId}     — accept/reject written back so host is notified
///
/// Listener starts automatically once both Firebase and PlayFabId are ready,
/// regardless of initialization order.
/// </summary>
public class InviteManager : MonoBehaviour
{
    private const string InvitesCollection = "invites";
    private const string PendingSubcollection = "pending";
    private const string RoomsCollection = "rooms";
    private const string InviteResponsesSubcol = "inviteResponses";

    public static string CurrentRoomId { get; private set; }

    public static void SetCurrentRoomId(string roomId) => CurrentRoomId = roomId;
    public static void ClearCurrentRoomId() => CurrentRoomId = null;

    private ListenerRegistration _inviteListener;
    private ListenerRegistration _responseListener; // host listens for accept/reject responses

    // Tracks pending offline-timeout coroutines keyed by recipientId.
    // If the recipient responds (accept OR reject) before the timeout fires,
    // the coroutine is cancelled so HandleInviteRejected is not called twice.
    private readonly Dictionary<string, Coroutine> _pendingOfflineTimeouts =
        new Dictionary<string, Coroutine>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        EventManager.OnSendInviteRequested += HandleSendInvite;
        EventManager.OnAcceptInviteRequested += HandleAcceptInvite;
        EventManager.OnRejectInviteRequested += HandleRejectInvite;
        EventManager.OnInviteResponseRequested += HandleInviteResponse;
        EventManager.OnMatchFound += HandleMatchFound;
        EventManager.OnRoomLeft += HandleRoomLeft;
        EventManager.OnInviteReceived += HandleInviteReceived;

        StartCoroutine(WaitAndStartListening());
    }

    private void OnDisable()
    {
        EventManager.OnSendInviteRequested -= HandleSendInvite;
        EventManager.OnAcceptInviteRequested -= HandleAcceptInvite;
        EventManager.OnRejectInviteRequested -= HandleRejectInvite;
        EventManager.OnInviteResponseRequested -= HandleInviteResponse;
        EventManager.OnMatchFound -= HandleMatchFound;
        EventManager.OnRoomLeft -= HandleRoomLeft;
        EventManager.OnInviteReceived -= HandleInviteReceived;

        StopAllCoroutines();
        StopInviteListener();
        StopResponseListener();
    }

    // ── Listener Setup ────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until both FirebaseManager.DB and PlayerDataManager.PlayFabId
    /// are ready. Works regardless of initialization order.
    /// </summary>
    private IEnumerator WaitAndStartListening()
    {
        yield return new WaitUntil(() =>
            FirebaseManager.DB != null &&
            !string.IsNullOrEmpty(PlayerDataManager.PlayFabId));

        StartListeningForInvites();
    }

    /// <summary>
    /// Listens to invites/{myId}/pending/ — scoped strictly to this player only.
    /// Invites sent to others are written to their own path and never seen here.
    /// </summary>
    private void StartListeningForInvites()
    {
        StopInviteListener();

        string myId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(myId) || FirebaseManager.DB == null) return;

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

                    // ── Coin check: silently auto-reject if player cannot afford the entry fee ──
                    // We must dispatch back to the main thread because Firestore listeners
                    // fire on a background thread and EventManager.FireGetCoinsRequested
                    // is a synchronous Unity-side callback.
                    int capturedEntryFee = entryFee;
                    string capturedRoomId = roomId;
                    string capturedSenderId = senderId;
                    string capturedSenderName = senderName;
                    DocumentReference capturedDocRef = doc.Reference;

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        EventManager.FireGetCoinsRequested(coins =>
                        {
                            if (coins < capturedEntryFee)
                            {
                                Debug.Log(
                                    $"[InviteManager] Auto-rejecting invite — need {capturedEntryFee} coins, have {coins}.");

                                // Notify host of rejection so they are not left waiting
                                EventManager.FireInviteResponseRequested(capturedRoomId, capturedSenderId, false);

                                // Clean up the invite doc immediately
                                capturedDocRef.DeleteAsync();
                                PlayerPrefs.DeleteKey("InviteDocId");
                                PlayerPrefs.DeleteKey("InviteEntryFee");
                                PlayerPrefs.Save();
                                return;
                            }

                            // Player has enough coins — show the popup
                            EventManager.FireInviteReceived(capturedSenderName, capturedSenderId, capturedRoomId);
                        });
                    });
                }
            });
    }

    /// <summary>
    /// Host listens to rooms/{roomId}/inviteResponses/ to get notified
    /// when invited friends accept or reject. Called after sending an invite.
    /// </summary>
    private void StartListeningForInviteResponses(string roomId)
    {
        StopResponseListener();

        if (string.IsNullOrEmpty(roomId) || FirebaseManager.DB == null) return;

        _responseListener = FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .Collection(InviteResponsesSubcol)
            .Listen(snapshot =>
            {
                foreach (var change in snapshot.GetChanges())
                {
                    if (change.ChangeType != DocumentChange.Type.Added) continue;

                    var doc = change.Document;
                    string recipientId = doc.Id;
                    bool accepted = false;

                    if (doc.TryGetValue("accepted", out bool a)) accepted = a;

                    // Clean up the response doc immediately
                    doc.Reference.DeleteAsync();

                    if (accepted)
                    {
                        CancelOfflineTimeout(recipientId);
                        EventManager.FireInviteAccepted(recipientId);
                    }
                    else
                    {
                        CancelOfflineTimeout(recipientId);
                        EventManager.FireInviteRejected(recipientId);
                    }
                }
            });
    }

    private void StopInviteListener()
    {
        _inviteListener?.Stop();
        _inviteListener = null;
    }

    private void StopResponseListener()
    {
        _responseListener?.Stop();
        _responseListener = null;
    }

    // ── Invite Received (recipient side) ──────────────────────────────────────

    /// <summary>
    /// Shows invite popup globally regardless of which screen the recipient is on.
    /// </summary>
    private void HandleInviteReceived(string senderName, string senderId, string roomId)
    {
        PlayerPrefs.SetString("InviteSenderName", senderName);
        PlayerPrefs.SetString("InviteSenderId", senderId);
        PlayerPrefs.SetString("InviteRoomId", roomId);
        PlayerPrefs.Save();
        EventManager.FireShowView(ViewType.InvitePopUp, true);
    }

    // ── Send Invite (host side) ───────────────────────────────────────────────

    /// <summary>
    /// Writes invite doc to invites/{recipientId}/pending/.
    /// Auto-deletes after 10 seconds if recipient hasn't acted.
    /// Starts response listener so host knows when friend accepts/rejects.
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

        // Start listening for the response so host knows accept/reject
        StartListeningForInviteResponses(roomId);

        // If recipient is offline their Firestore listener never fires,
        // so we auto-reject on the host side after the same 10-second window.
        string capturedRecipient = recipientPlayFabId;
        CancelOfflineTimeout(capturedRecipient); // clear any stale timeout for this recipient
        Coroutine timeout = StartCoroutine(AutoRejectIfNoResponse(capturedRecipient, 10f));
        _pendingOfflineTimeouts[capturedRecipient] = timeout;
    }

    private IEnumerator DeleteInviteAfterDelay(DocumentReference docRef, float delay)
    {
        yield return new WaitForSeconds(delay);
        docRef?.DeleteAsync();
    }

    /// <summary>
    /// Fires FireInviteRejected on the host side if the recipient never responds
    /// within <paramref name="delay"/> seconds. Covers the offline / app-closed case
    /// where the recipient's Firestore listener never fires and no response doc is written.
    /// </summary>
    private IEnumerator AutoRejectIfNoResponse(string recipientId, float delay)
    {
        yield return new WaitForSeconds(delay);

        // If we're still tracking this recipient they never responded — treat as rejection.
        if (_pendingOfflineTimeouts.ContainsKey(recipientId))
        {
            _pendingOfflineTimeouts.Remove(recipientId);
            Debug.Log($"[InviteManager] No response from {recipientId} after {delay}s — auto-rejecting (offline).");
            EventManager.FireInviteRejected(recipientId);
        }
    }

    private void CancelOfflineTimeout(string recipientId)
    {
        if (_pendingOfflineTimeouts.TryGetValue(recipientId, out Coroutine c))
        {
            if (c != null) StopCoroutine(c);
            _pendingOfflineTimeouts.Remove(recipientId);
        }
    }

    // ── Invite Response (recipient writes, host reads) ────────────────────────

    /// <summary>
    /// Recipient writes accept/reject to rooms/{roomId}/inviteResponses/{myId}.
    /// Host's _responseListener picks this up and fires FireInviteAccepted/Rejected.
    /// </summary>
    private void HandleInviteResponse(string roomId, string senderId, bool accepted)
    {
        if (FirebaseManager.DB == null || string.IsNullOrEmpty(roomId)) return;

        string myId = PlayerDataManager.PlayFabId;
        if (string.IsNullOrEmpty(myId)) return;

        var responseData = new Dictionary<string, object>
        {
            { "accepted", accepted },
            { "recipientId", myId },
            { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };

        // Keyed by recipientId so host knows exactly who responded
        FirebaseManager.DB
            .Collection(RoomsCollection)
            .Document(roomId)
            .Collection(InviteResponsesSubcol)
            .Document(myId)
            .SetAsync(responseData)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                    Debug.LogWarning("[InviteManager] Write invite response failed: " + task.Exception?.Message);
            });
    }

    // ── Accept / Reject (recipient side cleanup) ──────────────────────────────

    /// <summary>
    /// Sets IsInvitedUser flag so MatchmakingView shows the correct invited state.
    /// MatchmakingManager handles actual room join via OnAcceptInviteRequested.
    /// </summary>
    private void HandleAcceptInvite(string roomId)
    {
        PlayerPrefs.SetInt("IsInvitedUser", 1);
        PlayerPrefs.Save();
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

    private void HandleMatchFound(RoomData room)
    {
        StopInviteListener();
        StopResponseListener();
        CancelAllOfflineTimeouts();
    }

    private void HandleRoomLeft()
    {
        StopResponseListener();
        CancelAllOfflineTimeouts();
        StartCoroutine(WaitAndStartListening());
    }

    private void CancelAllOfflineTimeouts()
    {
        foreach (var kvp in _pendingOfflineTimeouts)
            if (kvp.Value != null)
                StopCoroutine(kvp.Value);
        _pendingOfflineTimeouts.Clear();
    }
}