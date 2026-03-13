using System.Collections.Generic;

public static class GameModeManager
{
    public static List<GameModeData> AvailableModes { get; private set; } = new List<GameModeData>();
    public static GameModeData       SelectedMode   { get; private set; }
    public static bool               IsInitialized  { get; private set; }

    public static void Initialize(List<int> entryFees)
    {
        AvailableModes = new List<GameModeData>();
        if (entryFees != null)
            foreach (int fee in entryFees)
                AvailableModes.Add(new GameModeData(fee));
        IsInitialized = true;
    }

    public static void SetSelectedMode(GameModeData mode) => SelectedMode = mode;

    public static void Clear()
    {
        AvailableModes.Clear();
        SelectedMode  = null;
        IsInitialized = false;
    }
}
