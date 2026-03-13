using System;
using System.Collections.Generic;

[Serializable]
public class PlayerStatsData
{
    public int trophies;
    public int avatarIndex;
    public bool hasSetupProfile;
    public int lastRewardedMilestone;

    // Coin transaction history - kept inside PlayerStats JSON
    public List<CoinTransaction> coinHistory = new List<CoinTransaction>();
}

[Serializable]
public class CoinTransaction
{
    public string reason;       // e.g. "entry_fee", "game_win", "trophy_reward"
    public int amount;          // positive = add, negative = subtract
    public string timestamp;    // UTC ISO string

    public CoinTransaction() { }

    public CoinTransaction(int amount, string reason)
    {
        this.amount = amount;
        this.reason = reason;
        this.timestamp = DateTime.UtcNow.ToString("o");
    }
}
