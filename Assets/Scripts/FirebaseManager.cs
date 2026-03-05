using UnityEngine;
using Firebase;
using Firebase.Extensions;
using Firebase.Firestore;

/// <summary>
/// Initializes Firebase on startup and exposes a single FirebaseFirestore instance.
/// Attach to the same root GameObject as PlayFabManager.
/// 
/// NOTE: ContinueWithOnMainThread (Firebase's built-in extension) is used throughout
/// the project instead of ContinueWith, which crashes on background threads in Unity.
/// UnityMainThreadDispatcher is therefore NOT needed and can be removed from the project.
/// </summary>
public class FirebaseManager : MonoBehaviour
{
    public static FirebaseManager Instance { get; private set; }
    public static FirebaseFirestore DB      { get; private set; }
    public static bool              IsReady { get; private set; } = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // If you see "Database URL not set in Firebase config" warning:
        // 1. Go to Firebase Console → your project → Firestore Database → Create database
        // 2. Re-download google-services.json and replace the one in Assets/
        // 3. Make sure you created a FIRESTORE database (not Realtime Database)
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
        {
            var status = task.Result;
            if (status == DependencyStatus.Available)
            {
                DB      = FirebaseFirestore.DefaultInstance;
                IsReady = true;
                Debug.Log("[FirebaseManager] Firebase ready.");
                EventManager.FireFirebaseReady();
            }
            else
            {
                Debug.LogError($"[FirebaseManager] Firebase dependency error: {status}");
            }
        });
    }
}
