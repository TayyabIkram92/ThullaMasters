using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Queues Actions from background threads (e.g. Firestore listeners) and
/// executes them on the Unity main thread during Update().
///
/// SETUP — one time only:
///   1. Create an empty GameObject in your first/persistent scene.
///   2. Name it "UnityMainThreadDispatcher".
///   3. Attach this script to it.
///   4. Enable "Don't Destroy On Load" is handled automatically in Awake().
///
/// USAGE (from any thread):
///   UnityMainThreadDispatcher.Enqueue(() => { /* your Unity code here */ });
/// </summary>
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private readonly Queue<Action> _queue = new Queue<Action>();
    private readonly object _lock = new object();

    // ── Singleton ─────────────────────────────────────────────────────────────

    public static UnityMainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
                Debug.LogError("[UnityMainThreadDispatcher] No instance found in scene. " +
                               "Add the script to a GameObject in your first scene.");
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Enqueue (call from any thread) ────────────────────────────────────────

    /// <summary>
    /// Schedules <paramref name="action"/> to run on the main thread
    /// during the next Update(). Safe to call from any thread.
    /// </summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;

        if (_instance == null)
        {
            Debug.LogError("[UnityMainThreadDispatcher] Cannot enqueue — no instance in scene.");
            return;
        }

        lock (_instance._lock)
        {
            _instance._queue.Enqueue(action);
        }
    }

    // ── Drain queue on main thread ────────────────────────────────────────────

    private void Update()
    {
        // Drain up to all queued actions this frame.
        // Swap under lock so background threads can still enqueue
        // while we execute the current batch without holding the lock.
        Queue<Action> batch = null;

        lock (_lock)
        {
            if (_queue.Count == 0) return;
            batch = new Queue<Action>(_queue);
            _queue.Clear();
        }

        while (batch.Count > 0)
        {
            Action action = batch.Dequeue();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnityMainThreadDispatcher] Exception in queued action: " + ex);
            }
        }
    }
}
