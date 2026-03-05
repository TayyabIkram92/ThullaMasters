using System;
using System.Collections.Generic;

/// <summary>
/// Data models used by Matchmaking and InGame systems.
/// Serialized to/from Firestore.
/// </summary>

// ── SlotData ──────────────────────────────────────────────────────────────────

[Serializable]
public class SlotData
{
    public string id;           // PlayFabId or "BOT_1" etc.
    public string displayName;
    public int    avatarIndex;
    public bool   isBot;

    public SlotData() { }

    public SlotData(string id, string displayName, int avatarIndex, bool isBot)
    {
        this.id          = id;
        this.displayName = displayName;
        this.avatarIndex = avatarIndex;
        this.isBot       = isBot;
    }

    public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
    {
        { "id",          id          },
        { "displayName", displayName },
        { "avatarIndex", avatarIndex },
        { "isBot",       isBot       }
    };

    public static SlotData FromDictionary(Dictionary<string, object> dict)
    {
        string id = dict.ContainsKey("id") ? dict["id"].ToString() : "";

        if (string.IsNullOrWhiteSpace(id))
            UnityEngine.Debug.LogWarning("[SlotData] Parsed a slot with empty id from Firestore.");

        return new SlotData(
            id,
            dict.ContainsKey("displayName") ? dict["displayName"].ToString()        : "Unknown",
            dict.ContainsKey("avatarIndex") ? Convert.ToInt32(dict["avatarIndex"])  : 0,
            dict.ContainsKey("isBot")       ? Convert.ToBoolean(dict["isBot"])      : false
        );
    }
}

// ── RoomData ──────────────────────────────────────────────────────────────────

[Serializable]
public class RoomData
{
    public string         roomId;
    public string         hostId;
    public int            entryFee;
    public string         status;    // "waiting" | "starting" | "dealing"
    public List<SlotData> players;

    /// <summary>
    /// Hands keyed by player id.
    /// e.g. hands["abc123"] = ["AS","KH","10C", ...]
    /// Written once by host, read once by each player.
    /// </summary>
    public Dictionary<string, List<string>> hands;

    public RoomData()
    {
        players = new List<SlotData>();
        hands   = new Dictionary<string, List<string>>();
    }

    public RoomData(string roomId, string hostId, int entryFee)
    {
        this.roomId   = roomId;
        this.hostId   = hostId;
        this.entryFee = entryFee;
        this.status   = "waiting";
        this.players  = new List<SlotData>();
        this.hands    = new Dictionary<string, List<string>>();
    }

    // ── Firestore Serialization ───────────────────────────────────────────────

    public Dictionary<string, object> ToDictionary()
    {
        var playerList = new List<object>();
        foreach (var p in players)
            playerList.Add(p.ToDictionary());

        return new Dictionary<string, object>
        {
            { "hostId",   hostId    },
            { "entryFee", entryFee  },
            { "status",   status    },
            { "players",  playerList }
        };
    }

    /// <summary>
    /// Serialize hands dict for Firestore.
    /// hands[playerId] = List of short code strings.
    /// </summary>
    public Dictionary<string, object> HandsToDictionary()
    {
        var dict = new Dictionary<string, object>();
        foreach (var kv in hands)
        {
            var cardList = new List<object>();
            foreach (var card in kv.Value)
                cardList.Add(card);
            dict[kv.Key] = cardList;
        }
        return dict;
    }

    public static RoomData FromDictionary(string roomId, Dictionary<string, object> dict)
    {
        var room = new RoomData
        {
            roomId   = roomId,
            hostId   = dict.ContainsKey("hostId")   ? dict["hostId"].ToString()         : "",
            entryFee = dict.ContainsKey("entryFee") ? Convert.ToInt32(dict["entryFee"]) : 0,
            status   = dict.ContainsKey("status")   ? dict["status"].ToString()         : "waiting",
            players  = new List<SlotData>(),
            hands    = new Dictionary<string, List<string>>()
        };

        // Parse players array
        if (dict.ContainsKey("players") && dict["players"] is List<object> rawList)
            foreach (var item in rawList)
                if (item is Dictionary<string, object> pd)
                    room.players.Add(SlotData.FromDictionary(pd));

        // Parse hands dict
        if (dict.ContainsKey("hands") && dict["hands"] is Dictionary<string, object> rawHands)
        {
            foreach (var kv in rawHands)
            {
                var cardCodes = new List<string>();
                if (kv.Value is List<object> cardList)
                    foreach (var c in cardList)
                        cardCodes.Add(c.ToString());
                room.hands[kv.Key] = cardCodes;
            }
        }

        return room;
    }
}
