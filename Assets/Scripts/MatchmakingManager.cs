using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// Handles Firestore matchmaking. Uses Firebase SDK correctly (no snap.Documents[0]).
/// No Newtonsoft — uses JsonUtility for bot names.
/// </summary>
public class MatchmakingManager : MonoBehaviour
{
    private const string RoomsCollection = "rooms";
    private const int MaxPlayers = 4;
    private const float BotFillInterval = 20f;

    private string[] _botNames;
    private int _botCounter = 0;

    private ListenerRegistration _roomListener;
    private Coroutine _botFillCoroutine;
    private RoomData _currentRoom;
    private bool _isHost = false;
    private int _botNameIndex = 0;

    private void OnEnable()
    {
        EventManager.OnMatchmakingStartRequested += HandleMatchmakingStart;
        EventManager.OnMatchmakingCancelRequested += HandleMatchmakingCancel;
        EventManager.OnAcceptInviteRequested += HandleAcceptInvite;
        EventManager.OnLeaveRoomRequested += HandleLeaveRoom;
        EventManager.OnFirebaseReady += OnFirebaseReady;
    }

    private void OnDisable()
    {
        EventManager.OnMatchmakingStartRequested -= HandleMatchmakingStart;
        EventManager.OnMatchmakingCancelRequested -= HandleMatchmakingCancel;
        EventManager.OnAcceptInviteRequested -= HandleAcceptInvite;
        EventManager.OnLeaveRoomRequested -= HandleLeaveRoom;
        EventManager.OnFirebaseReady -= OnFirebaseReady;

        CleanupRoom();
    }

    private void OnFirebaseReady()
    {
        LoadBotNames();
    }

    private void LoadBotNames()
    {
        TextAsset asset = Resources.Load<TextAsset>("botNames");
        if (asset != null)
        {
            try
            {
                var wrapper = JsonUtility.FromJson<BotNamesWrapper>(asset.text);
                _botNames = (wrapper != null && wrapper.names != null && wrapper.names.Length > 0)
                    ? wrapper.names
                    : DefaultBotNames();
            }
            catch
            {
                _botNames = DefaultBotNames();
            }
        }
        else
        {
            _botNames = DefaultBotNames();
        }

        ShuffleBotNames();
    }

    private void ShuffleBotNames()
    {
        if (_botNames == null) return;
        for (int i = _botNames.Length - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (_botNames[i], _botNames[j]) = (_botNames[j], _botNames[i]);
        }

        _botNameIndex = 0;
    }

    private string[] DefaultBotNames() => new[]
    {
        "Iqra", "Nusha", "Iffat", "Mujtaba", "Danish", "Usman", "Shaista", "Roohi", "Ghazal", "Taimoor",
        "Nimra", "Saira", "Kanza", "Waleed", "Maha", "Shazia", "Hadi", "Muneeb", "Kabir", "Rubab",
        "Hamza", "Fareeha", "Naveed", "Laila", "Rashid", "Amna", "Asif", "Haris", "Khalid", "Fahd",
        "Yasir", "Imran", "Shahzad", "Raheel", "Bushra", "Kamran", "Fozia", "Huma", "Qasim", "Arman",
        "Mehwish", "Zunaira", "Hashim", "Farah", "Majid", "Munir", "Tanveer", "Zahir", "Khadija", "Masood",
        "Junaid", "Ayesha", "Amina", "Zeeshan", "Ali", "Fahim", "Ahsan", "Sadia", "Hina", "Shayan",
        "Yusra", "Rehan", "Shahjahan", "Nargis", "Zubair", "Shabbir", "Mahnoor", "Zohair", "Raheel", "Imtiaz",
        "Uzma", "Fatima", "Erum", "Shahid", "Noor", "Hassan", "Irshad", "Lubna", "Rashida", "Sharjeel",
        "Mahwish", "AyeshaKhan", "Shamim", "Yasmin", "Abid", "Abubakar", "Sabir", "Tariq", "Nadia", "Bushra",
        "SyedZafar", "Aleena", "Tariq", "Marium", "Noman", "Alia", "Sakina", "Aqsa", "Nazar", "Zara",
        "Momin", "Humayun", "Shiza", "Mahira", "Rayaan", "Tuba", "Ayat", "Ali1", "Marwa", "Dilawar",
        "Hussain", "Sheraz", "Kiran", "Younis", "Nida", "Kanwal", "Rafay", "Sidra", "Farooq", "Kashif",
        "Javeria", "AliFatima", "Salman", "Farhan", "Safia", "Wasiq", "Naila", "Mumtaz", "Khadim", "Nazish",
        "Sanam", "Qaiser", "Komal", "Noman1997", "Sachal", "SidraNaeem", "Karishma", "Azeem", "Rameez", "Shiraz",
        "Tanzeel", "Sumaira", "Rahim", "Areesha", "Shazia1991", "Eman", "Ubaid", "Areej", "Imtisal", "Hifza",
        "Irfan", "Kamil", "Shahzad15", "Amal", "Abdul", "Anwar", "HassanOP", "Arsalan", "Maliha", "Nadeem",
        "Mahum", "Alee", "Noreen", "Mufaddal", "Nusrat", "Naseem", "Raheela", "Aleem", "Hania", "Zehra",
        "Yasir", "Fareeda", "Amna", "Fayyaz", "ShaziaOP", "Shakeel", "Mahrukh", "ShahidOP", "Hamid", "Afaq",
        "AbdulKarim", "Noman_Fahad", "Mustafa", "Munawar", "Yusra", "Kashif", "KashifLive", "Nishat", "KamranOP",
        "ShaziaOP", "Aftab", "UsmanLive", "Rahat", "Sara", "AhmedOP", "Hajra", "Hasnat", "Hafsa", "Mona",
        "KashifLive", "Noor", "Ghazanfar", "Nayab", "Ishraq", "JamalLive", "Imad", "Ghous", "Nihar", "OsmanLive",
        "Qadeer", "Nawal", "Mehar", "YasirOP", "Shafqat", "Hina", "AshfaqLive", "Shabaz", "Owais", "Rashid",
        "Dania", "AshfaqPro", "Luqman", "Qaisar", "Ruqaiya", "MaazOP", "Atif", "Nazli", "AnasLive", "Tashfeen",
        "MalikOP", "Gulzar", "Faisal", "JaveriaLive", "Maryam", "Karam", "Masood", "Haris", "AdilOP", "Shehla",
        "Parveen", "Fahad", "Muzna", "RaheelOP", "Shaista", "IhsanLive", "Bushra", "Adeeb", "Ranya", "ShahidOP",
        "Sadiq", "Sabahat", "Laraib", "Sarfaraz", "HaniaLive", "Hamza", "Azfar", "RashidOP", "AdnanOP", "Rabia",
        "AyubLive", "Suleman", "Tariq", "Subhan", "Nighat", "BilalOP", "AslamLive", "Rana", "Waseem", "NoreenLive",
        "Muniba", "Farida", "NawabOP", "Nazim", "Fawad", "MahumOP", "Sikandar", "ImranLive", "Fiza", "JamshedOP",
        "Shahbaz", "Muniza", "TahirLive", "AshfaqOP", "AdeelOP", "Sadia", "FarhanLive", "BushraOP", "MahnoorLive",
        "Johar", "Talha", "AzamOP", "Shraddha", "ZainLive", "Huma", "AshrafLive", "Ayesha", "KiranLive", "Munawar",
        "RabiaLive", "Adil", "Kimya", "AmirLive", "Shazia", "KamranLive", "TashfeenOP", "AymanOP", "Sabir", "HiraOP",
        "Shanzeela", "Madiha", "FaisalLive", "SaqlainOP", "FaisalOP", "HamzaLive", "SabirOP", "AyeshaOP", "ImranOP",
        "AqsaLive", "Ayman", "AshrafOP", "SajidLive", "SabirLive", "Hassaan", "FawadLive", "FawadOP", "BasitLive",
        "Farhaan", "MahwishLive", "ZainOP", "RizwanLive", "RizwanOP", "AliGW", "Zainalabdin", "BasitOP", "Nomi",
        "Mehak", "HammadOP", "FahadLive", "JaveriaOP", "SairaLive", "Neha", "Hamza99", "SunilLive", "SundasOP",
        "ImadLive", "SadiaOP", "NusratLive", "Imran786", "YousafLive", "MuneebOP", "Aman", "ReemaOP", "SameerLive",
        "HajraLive", "Saif786", "HumairaLive", "Sara", "WaqasLive", "AnwarOP", "MalihaLive", "Raheel786", "JunaidLive",
        "Ayesha786", "RaimaLive", "Shazia786", "SabahatLive", "Nabeel786", "HinaLive", "Shaista786", "MalikLive",
        "Omer786", "LaibaLive", "Shahid786", "SyedaLive", "Talha786", "NaginaLive", "Omer99", "Junaid786", "RehanLive",
        "Fatima786", "Sana786", "Rabia786", "Amal786", "Waseem786", "Salman786", "TalhaLive", "Uzair786", "Yasir786",
        "Alina786", "Zainab786", "YasirLive", "Adil786", "Areeba786", "Haroon786", "Nimra786", "Amir786", "Kashif786",
        "Sikandar786", "RabiaLive", "Ashfaq786", "Rabia99", "Zoya786", "Sakib786", "Yasir99", "Afzal786", "Saif99",
        "Daniyal786", "Amjad786", "Madiha786", "Adil99", "Misha786", "Faisal99", "Shahzaib786", "Arsal786", "Rida786",
        "Saadia786", "Tamanna786", "Fareeha786", "Uzma786", "Yasir777", "Tamanna777", "Mehwish777", "Hana777",
        "Huma777",
        "Umair77", "Saif77", "Maya77", "Fahad77", "Zahida77", "Shahid77", "Pankaj77", "Iffat77", "Nawal77", "Dilbar77",
        "Zeeshan77", "Huma77", "Shazia77", "Kashif77", "Uzma77", "Faizan77", "Samina77", "Waseem77", "Shabina77",
        "Taimoor77",
        "Sundus77", "Hassan77", "Ubaid77", "Noor77", "Hareem77", "Maira77", "Rimsha77", "Atif77", "Ayesha77",
        "Shamshad77",
        "Shabnam77", "Shahbaz77", "Sakina77", "Imad77", "Nabeel77", "Adeel77", "Nisha77", "Sidra77", "Nida77", "Shan47",
        "Qasim47", "Hina47", "Feroza47", "Amar47", "Amna47", "Fahad47", "Noreen47", "Sara47", "Tamanna47", "Hasan47",
        "Hafeez47", "Noman47", "Aamina47", "Amal47", "Sarwar47", "Iqra47", "Babar47", "Shahbaz47", "Faisal47", "Rida47",
        "Asif47", "Salman47", "Masood47", "Ayesha47", "Hafsa47", "Shehzad47", "Afan47", "Naila47", "Ishaq47",
        "Mahwish47",
        "Naila77", "Aqsa47", "Shanzeela47", "Rashida47", "Nadia47", "Laraib47", "Ayesha47", "Zunaira47", "Reema47",
        "Sarah47",
        "Naima47", "Sadia47", "Sania47", "Alia47", "Raneem47", "Usha47", "Maliha47", "Nabeel47", "Sumaira47",
        "Haleema47",
        "Amara47", "Kausar47", "Nazrat47", "Komal47", "Baljeet47", "Rafia47", "Mustafa47", "Anum47", "Nimra47",
        "Shuma48",
        "Iqra48", "Sara48", "Humaira48", "Nadia48", "Ayesha48", "Munir48", "Imran48", "Hania48", "Rizwana48", "Uzma48",
        "Rukhsana48", "Sumayya48", "Hiba48", "Rahim48", "Ambreen48", "Haniya48", "Samina48", "Nehal48", "Shifa48",
        "Sanam48",
        "Zunaira48", "Ameen49", "Sabeen49", "Sara49", "Bariha49", "Asifa49", "Uzma49", "Naheed49", "Atif49", "Maha49",
        "Haq49", "Zulekha49", "Wahab49", "Khalid49", "Qaiser49", "Munaza49", "Aliya49", "Iqra49", "Aqsa49", "Mehreen49",
        "Manahil49", "Zara49", "Shabana49", "Fizza49", "Karishma49", "Afshan49", "Farheen49", "Uzo49", "Zaara49",
        "Rehmat49",
        "Zahra49", "Khurram49", "Romee49", "Hira49", "Naiza49", "Farzana49", "Quadri49", "Ilma49", "Atiqa49", "Sabah49",
        "Iqra49", "Laila49", "Zainab49", "Anam49", "Fizza49", "Haniya49", "Iqra49", "Shaheen49", "Shiza49", "Salma49",
        "Talha49", "Sadia49", "Ayesha49", "Zuhoor49", "Shehnaz49", "Hania49", "Kainat49", "Zoya49", "Ali50", "Alee50",
        "Babar50", "Wasiq50", "Hadi50", "Ali50", "Bilal50", "Mona50", "Hassan50", "XxKhan", "ProAhmed", "WolfFatima",
        "LiveAyesha", "YTHamza", "DrAli", "KingSara", "QueenZara", "OPUsman", "GodBilal", "MasterHassan",
        "SniperFatima", "TigerAyesha",
        "DragonHamza", "NinjaAli", "GhostSara", "LegendZara", "HeroUsman", "AngelBilal", "WolfHamzaX", "TigerBilalX",
        "DragonAliX", "Xx_Fatima",
        "Ali_Gamer", "Bilal_007", "Hassan_77", "Fatima_47", "Ayesha_88", "Hamza_99", "RealAli", "RealAhmed", "OpenSara",
        "KnightZara",
        "ShadowUsman", "GhostBilal", "KnightHassan", "RiderFatima", "DragonAyesha", "GamerHamza", "ZaraOP", "AliTube",
        "AhmedPlay", "SaraNoFear",
        "ProZara", "CaptainUsman", "AceBilal", "SniperHassan", "NinjaFatima", "SamuraiAyesha", "FalconHamza",
        "PelicanAli", "HawkAhmed", "WolfSara",
        "TigerZara", "EagleUsman", "ScorpionBilal", "GhostHassan", "MidnightFatima", "BlackAyesha", "PhantomHamza",
        "SpartaAli", "GODAhmed", "YTalaib",
        "QamarX", "GamzaX", "FoxyYasir", "WolfZaan", "TigerFahad", "NinjaAisha", "ViperAyesha", "HawkZara", "AlphaAli",
        "OmegaSara", "ZunamiAli", "Zearah786",
        "CobraBilal", "TalonFatima", "HussainX", "MalikX", "XYZ", "Ali11", "AzraX", "YasirOPX", "UltraSara",
        "KnightHamzaOP", "ShayanGOD", "BetaAyesha", "AqeelPro",
        "Nemo23", "Zoltar", "RogueAli", "AgentAyesha", "MaxUsman", "BetaSara", "BladeZara", "SuperFatima",
        "GamerHassan", "XAliX", "Bobustudio", "Aman47", "XYZ123",
        "NickName", "Ali42", "Hamza42", "Rashid42", "Hassan42", "YaAli", "Tauqeer", "UniversalAli", "TerrorAyesha",
        "ZombieFatima", "AlphaHassan", "CandyHamza", "007Bilal"
    };


    // ─── Start Matchmaking ───────────────────────────────────────────────────

    // ─── Start Matchmaking ───────────────────────────────────────────────────
    private void HandleMatchmakingStart(GameModeData mode)
    {
        var db = FirebaseManager.DB;
        if (db == null)
        {
            EventManager.FireMatchmakingError("Firebase not ready.");
            return;
        }

        // If room was pre-created via invite flow, reuse it instead of searching
        string existingRoomId = InviteManager.CurrentRoomId;
        if (!string.IsNullOrEmpty(existingRoomId))
        {
            _isHost = true;
            _currentRoom = new RoomData
            {
                roomId   = existingRoomId,
                hostId   = PlayerDataManager.PlayFabId,
                entryFee = mode != null ? mode.EntryFee : 0,
                status   = "waiting",
                players  = new List<SlotData> { BuildLocalSlot() }
            };
            StartListening(existingRoomId);
            _botFillCoroutine = StartCoroutine(BotFillRoutine(existingRoomId));
            return;
        }

        // Normal matchmaking — search for existing room
        db.Collection(RoomsCollection)
            .WhereEqualTo("status", "waiting")
            .WhereEqualTo("entryFee", mode.EntryFee)
            .Limit(1)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    EventManager.FireMatchmakingError("Search failed.");
                    return;
                }

                QuerySnapshot snap = task.Result;
                if (snap.Count > 0)
                {
                    DocumentSnapshot firstDoc = null;
                    foreach (var doc in snap.Documents)
                    {
                        firstDoc = doc;
                        break;
                    }

                    if (firstDoc != null)
                        JoinRoom(firstDoc, mode);
                    else
                        CreateRoom(mode);
                }
                else
                {
                    CreateRoom(mode);
                }
            });
    }

    // ─── Accept Invite ────────────────────────────────────────────────────────

    private void HandleAcceptInvite(string roomId)
    {
        var db = FirebaseManager.DB;
        if (db == null) return;

        db.Collection(RoomsCollection)
            .Document(roomId)
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled || !task.Result.Exists)
                {
                    // Room gone — start own matchmaking
                    if (GameModeManager.SelectedMode != null)
                        HandleMatchmakingStart(GameModeManager.SelectedMode);
                    return;
                }

                JoinRoom(task.Result, GameModeManager.SelectedMode);
            });
    }

    // ─── Create Room ──────────────────────────────────────────────────────────

    private void CreateRoom(GameModeData mode)
    {
        _isHost = true;
        string roomId = Guid.NewGuid().ToString("N");
        var localPlayer = BuildLocalSlot();

        var roomData = new Dictionary<string, object>
        {
            { "hostId", PlayerDataManager.PlayFabId },
            { "entryFee", mode != null ? mode.EntryFee : 0 },
            { "status", "waiting" },
            { "players", new List<object> { SlotToDict(localPlayer) } },
            { "hostLastSeen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };

        FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .SetAsync(roomData)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    EventManager.FireMatchmakingError("Room creation failed.");
                    return;
                }

                _currentRoom = new RoomData
                {
                    roomId = roomId,
                    hostId = PlayerDataManager.PlayFabId,
                    entryFee = mode != null ? mode.EntryFee : 0,
                    status = "waiting",
                    players = new List<SlotData> { localPlayer }
                };

                InviteManager.SetCurrentRoomId(roomId);
                StartListening(roomId);
                _botFillCoroutine = StartCoroutine(BotFillRoutine(roomId));
            });
    }

    // ─── Join Room ────────────────────────────────────────────────────────────

    private void JoinRoom(DocumentSnapshot doc, GameModeData mode)
    {
        _isHost = false;
        string roomId = doc.Id;
        var localPlayer = BuildLocalSlot();

        FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .UpdateAsync(new Dictionary<string, object>
            {
                { "players", FieldValue.ArrayUnion(SlotToDict(localPlayer)) }
            })
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    EventManager.FireMatchmakingError("Join failed.");
                    return;
                }

                _currentRoom = ParseRoomDoc(doc);
                if (_currentRoom.players == null) _currentRoom.players = new List<SlotData>();
                _currentRoom.players.Add(localPlayer);

                InviteManager.SetCurrentRoomId(roomId);
                StartListening(roomId);
            });
    }

    // ─── Listener ────────────────────────────────────────────────────────────

    private void StartListening(string roomId)
    {
        _roomListener?.Stop();
        _roomListener = FirebaseManager.DB.Collection(RoomsCollection)
            .Document(roomId)
            .Listen(snapshot =>
            {
                if (!snapshot.Exists) return;

                var room = ParseRoomDoc(snapshot);

                // Host preserves locally-added bots
                if (_isHost && _currentRoom?.players != null)
                {
                    foreach (var p in _currentRoom.players)
                    {
                        if (p.isBot && (room.players == null || !room.players.Exists(r => r.id == p.id)))
                        {
                            if (room.players == null) room.players = new List<SlotData>();
                            room.players.Add(p);
                        }
                    }
                }

                _currentRoom = room;
                EventManager.FireRoomUpdated(room);

                if (_isHost && room.players != null && room.players.Count >= MaxPlayers &&
                    room.status == "waiting")
                {
                    SetRoomStatus(roomId, "starting");
                    StopBotFill();
                    EventManager.FireMatchFound(room);
                }
                else if (!_isHost && room.status == "starting")
                {
                    EventManager.FireMatchFound(room);
                }
            });
    }

    // ─── Bot Fill ─────────────────────────────────────────────────────────────

    private IEnumerator BotFillRoutine(string roomId)
    {
        yield return new WaitForSeconds(BotFillInterval);

        while (_currentRoom != null &&
               (_currentRoom.players == null || _currentRoom.players.Count < MaxPlayers))
        {
            var bot = CreateBotSlot();
            if (_currentRoom.players == null) _currentRoom.players = new List<SlotData>();
            _currentRoom.players.Add(bot);

            FirebaseManager.DB.Collection(RoomsCollection)
                .Document(roomId)
                .UpdateAsync(new Dictionary<string, object>
                {
                    { "players", FieldValue.ArrayUnion(SlotToDict(bot)) }
                });

            yield return new WaitForSeconds(BotFillInterval);
        }
    }

    // ─── Create Room For Invite (called before matchmaking starts) ────────────
    public static async Task<bool> CreateRoomAsync()
    {
        var db = FirebaseManager.DB;
        if (db == null)
        {
            Debug.LogWarning("[MatchmakingManager] Firebase not ready for CreateRoomAsync.");
            return false;
        }

        try
        {
            string roomId = Guid.NewGuid().ToString("N");
            int entryFee = GameModeManager.SelectedMode != null ? GameModeManager.SelectedMode.EntryFee : 0;

            var roomData = new Dictionary<string, object>
            {
                { "hostId", PlayerDataManager.PlayFabId },
                { "entryFee", entryFee },
                { "status", "waiting" },
                {
                    "players", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "id", PlayerDataManager.PlayFabId },
                            { "displayName", PlayerDataManager.DisplayName },
                            { "avatarIndex", PlayerDataManager.AvatarIndex },
                            { "isBot", false }
                        }
                    }
                },
                { "hostLastSeen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            };

            DocumentReference docRef = db.Collection(RoomsCollection).Document(roomId);

            // Step 1: Write room
            await docRef.SetAsync(roomData);

            // Step 2: Verify room exists — retry up to 3 times
            DocumentSnapshot snapshot = null;
            for (int i = 0; i < 3; i++)
            {
                snapshot = await docRef.GetSnapshotAsync(); // correct SDK method
                if (snapshot.Exists) break;
                await Task.Delay(500);
            }

            if (snapshot == null || !snapshot.Exists)
            {
                Debug.LogWarning("[MatchmakingManager] Room write could not be verified in Firestore.");
                return false;
            }

            InviteManager.SetCurrentRoomId(roomId);
            Debug.Log("[MatchmakingManager] Room pre-created and verified: " + roomId);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MatchmakingManager] CreateRoomAsync failed: " + e.Message);
            return false;
        }
    }

    private SlotData CreateBotSlot()
    {
        _botCounter++;
        string name = PickUniqueBotName();
        return new SlotData
        {
            id = "BOT_" + _botCounter,
            displayName = name,
            avatarIndex = UnityEngine.Random.Range(0, 16),
            isBot = true
        };
    }

    private string PickUniqueBotName()
    {
        if (_botNames == null || _botNames.Length == 0) return "Bot" + _botCounter;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentRoom?.players != null)
            foreach (var p in _currentRoom.players)
                usedNames.Add(p.displayName);

        int attempts = 0;
        while (attempts < _botNames.Length)
        {
            string candidate = _botNames[_botNameIndex % _botNames.Length];
            _botNameIndex++;
            if (!usedNames.Contains(candidate))
                return candidate;
            attempts++;
        }

        return _botNames[_botCounter % _botNames.Length] + "_" + _botCounter;
    }

    private void StopBotFill()
    {
        if (_botFillCoroutine != null)
        {
            StopCoroutine(_botFillCoroutine);
            _botFillCoroutine = null;
        }
    }

    // ─── Leave / Cancel ───────────────────────────────────────────────────────

    private void HandleMatchmakingCancel() => CleanupRoom();
    private void HandleLeaveRoom() => CleanupRoom();

    private void CleanupRoom()
    {
        StopBotFill();
        _roomListener?.Stop();
        _roomListener = null;

        if (_currentRoom != null && _isHost && FirebaseManager.DB != null)
            FirebaseManager.DB.Collection(RoomsCollection).Document(_currentRoom.roomId).DeleteAsync();

        InviteManager.ClearCurrentRoomId();
        _currentRoom = null;
        _isHost = false;
        EventManager.FireRoomLeft();
    }

    private void SetRoomStatus(string roomId, string status)
    {
        FirebaseManager.DB.Collection(RoomsCollection).Document(roomId)
            .UpdateAsync(new Dictionary<string, object> { { "status", status } });
    }

    public RoomData GetCurrentRoom() => _currentRoom;

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private SlotData BuildLocalSlot() => new SlotData
    {
        id = PlayerDataManager.PlayFabId,
        displayName = PlayerDataManager.DisplayName,
        avatarIndex = PlayerDataManager.AvatarIndex,
        isBot = false
    };

    private Dictionary<string, object> SlotToDict(SlotData s) => new Dictionary<string, object>
    {
        { "id", s.id }, { "displayName", s.displayName },
        { "avatarIndex", s.avatarIndex }, { "isBot", s.isBot }
    };

    private RoomData ParseRoomDoc(DocumentSnapshot doc)
    {
        var room = new RoomData { roomId = doc.Id, players = new List<SlotData>() };
        if (doc.TryGetValue("hostId", out string hostId)) room.hostId = hostId;
        if (doc.TryGetValue("entryFee", out int fee)) room.entryFee = fee;
        if (doc.TryGetValue("status", out string status)) room.status = status;

        if (doc.TryGetValue("players", out List<object> players))
        {
            foreach (var p in players)
            {
                if (p is Dictionary<string, object> pd)
                {
                    room.players.Add(new SlotData
                    {
                        id = pd.TryGetValue("id", out var id) ? id.ToString() : "",
                        displayName = pd.TryGetValue("displayName", out var dn) ? dn.ToString() : "Player",
                        avatarIndex = pd.TryGetValue("avatarIndex", out var ai) ? Convert.ToInt32(ai) : 0,
                        isBot = pd.TryGetValue("isBot", out var ib) && Convert.ToBoolean(ib)
                    });
                }
            }
        }

        return room;
    }

    [Serializable]
    private class BotNamesWrapper
    {
        public string[] names;
    }
}