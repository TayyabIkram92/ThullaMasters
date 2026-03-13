using System;

public static partial class EventManager
{
    // ─── Firebase Ready ───────────────────────────────────────────────────────
    public static event Action OnFirebaseReady;
    public static void FireFirebaseReady() => OnFirebaseReady?.Invoke();

    // ─── Matchmaking ─────────────────────────────────────────────────────────
    public static event Action<GameModeData> OnMatchmakingStartRequested;
    public static void FireMatchmakingStartRequested(GameModeData mode)
        => OnMatchmakingStartRequested?.Invoke(mode);

    public static event Action OnMatchmakingCancelRequested;
    public static void FireMatchmakingCancelRequested()
        => OnMatchmakingCancelRequested?.Invoke();

    public static event Action<RoomData> OnRoomUpdated;
    public static void FireRoomUpdated(RoomData room) => OnRoomUpdated?.Invoke(room);

    public static event Action<RoomData> OnMatchFound;
    public static void FireMatchFound(RoomData room) => OnMatchFound?.Invoke(room);

    public static event Action<string> OnMatchmakingError;
    public static void FireMatchmakingError(string message)
        => OnMatchmakingError?.Invoke(message);

    // ─── Room Lifecycle ───────────────────────────────────────────────────────
    public static event Action OnLeaveRoomRequested;
    public static void FireLeaveRoomRequested() => OnLeaveRoomRequested?.Invoke();

    public static event Action OnRoomLeft;
    public static void FireRoomLeft() => OnRoomLeft?.Invoke();

    // ─── Friend Invite System ────────────────────────────────────────────────
    public static event Action<string, string> OnSendInviteRequested;
    public static void FireSendInviteRequested(string friendPlayFabId, string roomId)
        => OnSendInviteRequested?.Invoke(friendPlayFabId, roomId);

    public static event Action<string, string, string> OnInviteReceived;
    public static void FireInviteReceived(string senderName, string senderId, string roomId)
        => OnInviteReceived?.Invoke(senderName, senderId, roomId);

    public static event Action<string> OnAcceptInviteRequested;
    public static void FireAcceptInviteRequested(string roomId)
        => OnAcceptInviteRequested?.Invoke(roomId);

    public static event Action OnRejectInviteRequested;
    public static void FireRejectInviteRequested() => OnRejectInviteRequested?.Invoke();
}
