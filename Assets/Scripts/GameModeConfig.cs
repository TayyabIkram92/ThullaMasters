using System;
using System.Collections.Generic;

/// <summary>
/// Holds configuration for all game modes fetched from PlayFab Title Data.
/// </summary>
[Serializable]
public class GameModeConfig
{
    public List<int> entryFees = new List<int>(); // e.g., [120, 300, 600]
}

/// <summary>
/// Runtime data for a single game mode card.
/// </summary>
public class GameModeData
{
    public int EntryFee { get; set; }
    public int Prize { get; set; } // (EntryFee ÷ 4) + EntryFee
    public int Prize1st { get; set; } // Same as Prize
    public int Prize2nd { get; set; } // Same as Prize
    public int Prize3rd { get; set; } // Same as Prize

    public GameModeData(int entryFee)
    {
        EntryFee = entryFee;
        Prize = (entryFee / 4) + entryFee;
        Prize1st = Prize;
        Prize2nd = Prize;
        Prize3rd = Prize;
    }
}